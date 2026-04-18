using System.Text.Json;
using System.Text.Json.Nodes;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Model;
using AdaptiveApi.Core.Pipeline;

namespace AdaptiveApi.Core.Streaming;

public sealed class ProgressiveOptions
{
    /// Flush the buffer every this many chars regardless of terminators.
    /// Lower = snappier UI, higher translator call rate. Defaults to 40.
    public int MinChars { get; init; } = 40;
    /// Hard cap: if the buffer grows past this (e.g. code output, one long sentence),
    /// translate + emit immediately.
    public int MaxChars { get; init; } = 240;
}

/// Alternative to `OpenAiStreamTranslator` for cases where time-to-first-token
/// matters more than pristine sentence boundaries. Flushes whenever the accumulated
/// content delta crosses a `MinChars` threshold OR a terminator is seen. Always
/// flushes on `finish_reason` / `[DONE]`.
///
/// Each flush translates the CUMULATIVE buffer so far (not just the delta) — that
/// gives the translator enough context to produce coherent output per chunk.
/// Clients receive synthetic deltas where each new delta is the *difference*
/// between the latest translated cumulative chunk and what's already been emitted.
/// This is a conservative approximation of strategy D in plan §5.1 that avoids
/// mid-sentence corrections.
public sealed class ProgressiveStreamTranslator
{
    private readonly ITranslator _translator;
    private readonly ProgressiveOptions _options;
    private readonly string? _systemContext;

    public ProgressiveStreamTranslator(ITranslator translator, ProgressiveOptions? options = null, string? systemContext = null)
    {
        _translator = translator;
        _options = options ?? new ProgressiveOptions();
        _systemContext = systemContext;
    }

    public async Task<StreamTranslatorMetrics> TranslateAsync(
        Stream upstream,
        Stream client,
        LanguageCode source,
        LanguageCode target,
        CancellationToken ct)
    {
        var metrics = new StreamTranslatorMetrics();
        var buffers = new Dictionary<int, ProgressiveBuffer>();

        await foreach (var ev in SseParser.ReadAsync(upstream, ct))
        {
            metrics.EventsIn++;

            if (ev.Data == "[DONE]")
            {
                foreach (var (index, buf) in buffers)
                    if (buf.HasPending) await EmitDiffAsync(client, index, buf, flushAll: true, source, target, metrics, ct);
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

            var absorbed = false;
            var finish = false;

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
                        buffers[index] = buf = new ProgressiveBuffer();
                    buf.Append(piece);
                    absorbed = true;
                }

                if (choice["finish_reason"] is JsonNode fr
                    && fr.GetValue<JsonElement>().ValueKind != JsonValueKind.Null)
                    finish = true;
            }

            foreach (var (index, buf) in buffers.ToList())
            {
                if (buf.ShouldFlush(_options.MinChars, _options.MaxChars) || finish)
                    await EmitDiffAsync(client, index, buf, flushAll: finish, source, target, metrics, ct);
            }

            if (finish)
            {
                StripDeltaContent(obj);
                await WriteAsync(client, new SseEvent(ev.Event, obj.ToJsonString()), metrics, ct);
                continue;
            }

            if (!absorbed)
                await WriteAsync(client, ev, metrics, ct);
        }

        // Upstream EOF without explicit [DONE]: flush residuals.
        foreach (var (index, buf) in buffers)
            if (buf.HasPending) await EmitDiffAsync(client, index, buf, flushAll: true, source, target, metrics, ct);

        return metrics;
    }

    private async Task EmitDiffAsync(
        Stream client, int choiceIndex, ProgressiveBuffer buf, bool flushAll,
        LanguageCode source, LanguageCode target,
        StreamTranslatorMetrics metrics, CancellationToken ct)
    {
        var cumulative = buf.Cumulative;
        if (cumulative.Length == 0) return;

        // Translate the full cumulative buffer. The tokenizer handles placeholders so
        // emitted synthetic deltas keep identifiers/URLs intact.
        var tokenized = PlaceholderTokenizer.Tokenize(cumulative);
        // The progressive approach translates the full cumulative text each time,
        // so only the admin system context is passed — the history is already in the text.
        var context = !string.IsNullOrEmpty(_systemContext)
            ? _systemContext.Length > TranslationContextTracker.MaxContextChars
                ? _systemContext[..TranslationContextTracker.MaxContextChars]
                : _systemContext
            : (string?)null;
        var results = await _translator.TranslateBatchAsync(
            new[] { new TranslationRequest(tokenized.Text, source, target, TagHandling: TagHandling.Xml, Context: context) },
            ct);
        var translatedRaw = results[0].Text;
        var validation = PlaceholderValidator.Validate(translatedRaw, tokenized.Placeholders);

        string full;
        if (validation.Ok)
            full = PlaceholderTokenizer.Reinject(translatedRaw, tokenized.Placeholders);
        else
        {
            metrics.IntegrityFailures++;
            full = cumulative;
        }

        metrics.CharsTranslated += cumulative.Length - buf.LastTranslatedLen;
        buf.LastTranslatedLen = cumulative.Length;

        // Emit only the suffix that hasn't been sent yet.
        var alreadyEmitted = buf.EmittedPrefix;
        if (full.StartsWith(alreadyEmitted, StringComparison.Ordinal))
        {
            var delta = full[alreadyEmitted.Length..];
            if (delta.Length == 0 && !flushAll) return;
            buf.EmittedPrefix = full;
            await EmitDeltaAsync(client, choiceIndex, delta, metrics, ct);
        }
        else
        {
            // The translator diverged from the previously-emitted prefix (rewrite).
            // Emit a "correction" whole-chunk delta so the client renders the new text.
            // Clients that render deltas append-only will see duplication — acceptable
            // trade-off for progressive streaming.
            buf.EmittedPrefix = full;
            await EmitDeltaAsync(client, choiceIndex, full, metrics, ct);
        }
    }

    private static async Task EmitDeltaAsync(Stream client, int choiceIndex, string text, StreamTranslatorMetrics metrics, CancellationToken ct)
    {
        var delta = new JsonObject
        {
            ["choices"] = new JsonArray(
                new JsonObject
                {
                    ["index"] = choiceIndex,
                    ["delta"] = new JsonObject { ["content"] = text },
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

    private static void StripDeltaContent(JsonObject envelope)
    {
        if (envelope["choices"] is not JsonArray choices) return;
        foreach (var c in choices)
            if (c is JsonObject co && co["delta"] is JsonObject d && d.ContainsKey("content"))
                d.Remove("content");
    }

    private sealed class ProgressiveBuffer
    {
        public string Cumulative { get; private set; } = string.Empty;
        public string EmittedPrefix { get; set; } = string.Empty;
        public int LastTranslatedLen { get; set; }

        public bool HasPending => Cumulative.Length > LastTranslatedLen;

        public void Append(string piece)
        {
            if (string.IsNullOrEmpty(piece)) return;
            Cumulative += piece;
        }

        public bool ShouldFlush(int minChars, int maxChars)
        {
            var growth = Cumulative.Length - LastTranslatedLen;
            if (growth >= maxChars) return true;
            if (growth < minChars) return false;
            // Prefer a terminator close to the tail so diffs land on natural boundaries.
            var tail = Cumulative.AsSpan(LastTranslatedLen);
            foreach (var c in tail)
                if (c is '.' or '!' or '?' or ';' or '\n') return true;
            return growth >= minChars * 2; // grown enough without terminator — flush anyway
        }
    }
}
