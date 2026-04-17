using System.Text.Json;
using System.Text.Json.Nodes;
using LlmTrans.Core.Abstractions;
using LlmTrans.Core.Model;
using LlmTrans.Core.Pipeline;

namespace LlmTrans.Core.Streaming;

public sealed class StreamTranslatorMetrics
{
    public int EventsIn { get; set; }
    public int EventsOut { get; set; }
    public int CharsTranslated { get; set; }
    public int IntegrityFailures { get; set; }
}

public sealed class OpenAiStreamTranslator
{
    private readonly ITranslator _translator;
    private readonly string? _systemContext;

    public OpenAiStreamTranslator(ITranslator translator, string? systemContext = null)
    {
        _translator = translator;
        _systemContext = systemContext;
    }

    public async Task<StreamTranslatorMetrics> TranslateAsync(
        Stream upstream,
        Stream client,
        LanguageCode source,
        LanguageCode target,
        CancellationToken ct,
        int minChars = 80)
    {
        var metrics = new StreamTranslatorMetrics();
        var buffers = new Dictionary<int, SentenceBuffer>();
        var contextTrackers = new Dictionary<int, TranslationContextTracker>();

        await foreach (var ev in SseParser.ReadAsync(upstream, ct))
        {
            metrics.EventsIn++;

            if (ev.Data == "[DONE]")
            {
                await FlushAllAsync(buffers, contextTrackers, source, target, client, metrics, ct);
                await WriteAsync(client, ev, metrics, ct);
                continue;
            }

            JsonNode? root;
            try { root = JsonNode.Parse(ev.Data); }
            catch (JsonException) { await WriteAsync(client, ev, metrics, ct); continue; }

            if (root is not JsonObject obj || obj["choices"] is not JsonArray choices)
            {
                await WriteAsync(client, ev, metrics, ct);
                continue;
            }

            var absorbedSomeContent = false;
            var finishPresent = false;

            foreach (var choiceNode in choices)
            {
                if (choiceNode is not JsonObject choice) continue;
                var index = choice["index"]?.GetValue<int>() ?? 0;

                if (choice["delta"] is JsonObject delta
                    && delta["content"] is JsonValue cv
                    && cv.TryGetValue<string>(out var piece)
                    && piece is not null)
                {
                    if (!buffers.TryGetValue(index, out var buf))
                        buffers[index] = buf = new SentenceBuffer(minChars);
                    buf.Append(piece);
                    absorbedSomeContent = true;
                }

                if (IsNonNull(choice["finish_reason"])) finishPresent = true;
            }

            // Drain any completed sentences as translated synthetic deltas.
            foreach (var (index, buf) in buffers.ToList())
            {
                if (!contextTrackers.TryGetValue(index, out var ctx))
                    contextTrackers[index] = ctx = new TranslationContextTracker(_systemContext);
                while (buf.TryFlushSentence(out var segment))
                    await EmitTranslatedDeltaAsync(client, index, segment, source, target, metrics, ctx, ct);
                if (buf.ShouldOverflowFlush())
                    await EmitTranslatedDeltaAsync(client, index, buf.FlushAll(), source, target, metrics, ctx, ct);
            }

            if (finishPresent)
            {
                await FlushAllAsync(buffers, contextTrackers, source, target, client, metrics, ct);
                // Pass the event through, with `delta.content` stripped (we've already emitted translated content).
                StripDeltaContent(obj);
                await WriteAsync(client, new SseEvent(ev.Event, obj.ToJsonString()), metrics, ct);
                continue;
            }

            // No finish, no content → metadata-only event (role marker, etc). Forward verbatim.
            if (!absorbedSomeContent)
                await WriteAsync(client, ev, metrics, ct);
        }

        // Upstream EOF without explicit [DONE]: still flush remaining.
        await FlushAllAsync(buffers, contextTrackers, source, target, client, metrics, ct);

        return metrics;
    }

    private async Task FlushAllAsync(
        Dictionary<int, SentenceBuffer> buffers,
        Dictionary<int, TranslationContextTracker> contextTrackers,
        LanguageCode source, LanguageCode target,
        Stream client, StreamTranslatorMetrics metrics, CancellationToken ct)
    {
        foreach (var (index, buf) in buffers)
        {
            if (buf.HasContent)
            {
                if (!contextTrackers.TryGetValue(index, out var ctx))
                    contextTrackers[index] = ctx = new TranslationContextTracker(_systemContext);
                await EmitTranslatedDeltaAsync(client, index, buf.FlushAll(), source, target, metrics, ctx, ct);
            }
        }
    }

    private async Task EmitTranslatedDeltaAsync(
        Stream client, int choiceIndex, string segment,
        LanguageCode source, LanguageCode target,
        StreamTranslatorMetrics metrics, TranslationContextTracker contextTracker, CancellationToken ct)
    {
        var context = contextTracker.Build();
        var tokenized = PlaceholderTokenizer.Tokenize(segment);
        var results = await _translator.TranslateBatchAsync(
            new[] { new TranslationRequest(tokenized.Text, source, target, TagHandling: TagHandling.Xml, Context: context) }, ct);
        var translatedRaw = results[0].Text;
        var validation = PlaceholderValidator.Validate(translatedRaw, tokenized.Placeholders);

        string finalText;
        if (validation.Ok)
            finalText = PlaceholderTokenizer.Reinject(translatedRaw, tokenized.Placeholders);
        else
        {
            metrics.IntegrityFailures++;
            finalText = segment;
        }

        // Record this source chunk so subsequent translations get it as context.
        contextTracker.Append(segment);

        metrics.CharsTranslated += segment.Length;

        var delta = new JsonObject
        {
            ["choices"] = new JsonArray(
                new JsonObject
                {
                    ["index"] = choiceIndex,
                    ["delta"] = new JsonObject { ["content"] = finalText },
                }),
        };
        await WriteAsync(client, new SseEvent(null, delta.ToJsonString()), metrics, ct);
    }

    private static async Task WriteAsync(Stream client, SseEvent ev, StreamTranslatorMetrics metrics, CancellationToken ct)
    {
        var bytes = SseParser.SerializeEvent(ev);
        await client.WriteAsync(bytes, ct);
        await client.FlushAsync(ct);
        metrics.EventsOut++;
    }

    private static bool IsNonNull(JsonNode? n) =>
        n is not null && n.GetValue<JsonElement>().ValueKind != JsonValueKind.Null;

    private static void StripDeltaContent(JsonObject envelope)
    {
        if (envelope["choices"] is not JsonArray choices) return;
        foreach (var c in choices)
        {
            if (c is JsonObject co && co["delta"] is JsonObject d && d.ContainsKey("content"))
                d.Remove("content");
        }
    }
}
