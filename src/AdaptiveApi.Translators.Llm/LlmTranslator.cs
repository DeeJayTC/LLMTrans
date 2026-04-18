using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaptiveApi.Translators.Llm;

/// LLM-based translator targeting an OpenAI-compatible /v1/chat/completions endpoint.
/// Uses JSON mode with an indexed array so placeholder integrity is easy to verify.
public sealed class AdaptiveApilator : ITranslator
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AdaptiveApilatorOptions> _options;
    private readonly ILogger<AdaptiveApilator> _log;

    public AdaptiveApilator(IHttpClientFactory httpFactory, IOptions<AdaptiveApilatorOptions> options, ILogger<AdaptiveApilator> log)
    {
        _httpFactory = httpFactory;
        _options = options;
        _log = log;
    }

    public string TranslatorId => "llm";

    public TranslatorCapabilities Capabilities =>
        TranslatorCapabilities.Glossary
        | TranslatorCapabilities.Formality
        | TranslatorCapabilities.TagHandling
        | TranslatorCapabilities.Batching
        | TranslatorCapabilities.AutoDetect
        | TranslatorCapabilities.StyleRules
        | TranslatorCapabilities.CustomInstructions;

    public async Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return Array.Empty<TranslationResult>();

        var results = new TranslationResult[requests.Count];
        var groups = requests
            .Select((r, i) => (Req: r, Index: i))
            .GroupBy(x => new LlmBatchKey(x.Req));

        foreach (var group in groups)
        {
            var items = group.ToList();
            var first = items[0].Req;
            var systemPrompt = BuildSystemPrompt(first);

            var user = new JsonObject
            {
                ["source_language"] = first.Source.Value,
                ["target_language"] = first.Target.Value,
                ["items"] = new JsonArray(items.Select((x, i) => (JsonNode?)new JsonObject
                {
                    ["id"] = i,
                    ["text"] = x.Req.Text,
                }).ToArray()),
            };

            var translated = await CallWithIntegrityRetryAsync(items, systemPrompt, user.ToJsonString(Opts), ct);

            for (var i = 0; i < items.Count; i++)
            {
                results[items[i].Index] = new TranslationResult(translated[i]);
            }
        }

        return results;
    }

    private async Task<string[]> CallWithIntegrityRetryAsync(
        List<(TranslationRequest Req, int Index)> items,
        string systemPrompt,
        string userJson,
        CancellationToken ct)
    {
        var max = Math.Max(1, _options.Value.MaxAttempts);
        Exception? last = null;
        for (var attempt = 0; attempt < max; attempt++)
        {
            try
            {
                var raw = await CallAsync(systemPrompt, userJson, ct);
                var parsed = ParseBatchResponse(raw, items.Count);

                // Validate placeholder integrity per site.
                var ok = true;
                for (var i = 0; i < items.Count; i++)
                {
                    var planPlaceholders = ExtractPlaceholders(items[i].Req.Text);
                    var validation = PlaceholderValidator.Validate(parsed[i], planPlaceholders);
                    if (!validation.Ok)
                    {
                        ok = false;
                        _log.LogWarning("placeholder integrity failure on attempt {Attempt} item {Index}: missing={Missing} dup={Dup}",
                            attempt, i, string.Join(",", validation.MissingIds), string.Join(",", validation.DuplicateIds));
                        break;
                    }
                }
                if (ok) return parsed;
            }
            catch (Exception ex)
            {
                last = ex;
                _log.LogWarning(ex, "llm translator attempt {Attempt} failed", attempt);
            }
        }

        if (last is not null) _log.LogError(last, "llm translator exhausted retries");
        // Fallback: return source unchanged so placeholder reinjection still works.
        return items.Select(x => x.Req.Text).ToArray();
    }

    private async Task<string> CallAsync(string systemPrompt, string userJson, CancellationToken ct)
    {
        var apiKey = _options.Value.ApiKey
            ?? throw new InvalidOperationException("LLM translator API key not configured");
        var http = _httpFactory.CreateClient("llm-translator");

        var body = new JsonObject
        {
            ["model"] = _options.Value.Model,
            ["temperature"] = _options.Value.Temperature,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user",   ["content"] = userJson }),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(_options.Value.BaseUrl), "v1/chat/completions"));
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        req.Content = JsonContent.Create(body, options: Opts);

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var envelope = await resp.Content.ReadFromJsonAsync<JsonObject>(Opts, ct)
                       ?? throw new InvalidOperationException("empty LLM response");

        var content = envelope["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                      ?? throw new InvalidOperationException("missing content in LLM response");
        return content;
    }

    private static string[] ParseBatchResponse(string content, int expectedCount)
    {
        var obj = JsonNode.Parse(content) as JsonObject
                  ?? throw new InvalidOperationException("LLM response was not a JSON object");
        var items = obj["items"] as JsonArray
                    ?? throw new InvalidOperationException("LLM response missing 'items' array");
        var result = new string[expectedCount];
        foreach (var entry in items)
        {
            if (entry is not JsonObject e) continue;
            var id = e["id"]?.GetValue<int>() ?? -1;
            if (id < 0 || id >= expectedCount) continue;
            result[id] = e["text"]?.GetValue<string>() ?? "";
        }
        for (var i = 0; i < result.Length; i++)
            result[i] ??= string.Empty;
        return result;
    }

    private static List<Placeholder> ExtractPlaceholders(string source)
    {
        var list = new List<Placeholder>();
        var i = 0;
        while (true)
        {
            var start = source.IndexOf("<adaptiveapi id=\"", i, StringComparison.Ordinal);
            if (start < 0) break;
            var idStart = start + "<adaptiveapi id=\"".Length;
            var idEnd = source.IndexOf('"', idStart);
            if (idEnd < 0) break;
            var end = source.IndexOf("/>", idEnd, StringComparison.Ordinal);
            if (end < 0) break;
            list.Add(new Placeholder(source[idStart..idEnd], source[start..(end + 2)]));
            i = end + 2;
        }
        return list;
    }

    private static string BuildSystemPrompt(TranslationRequest r)
    {
        var instructions = new List<string>
        {
            $"Translate each item in the input JSON from {r.Source.Value} to {r.Target.Value}.",
            "Return JSON of the shape {\"items\":[{\"id\":<int>,\"text\":\"<translated>\"}]}.",
            "Preserve every <adaptiveapi id=\"TAG_n\"/> tag exactly — do not translate, reorder, or remove them.",
            "Preserve JSON structure, code, URLs, and identifiers verbatim.",
        };
        if (r.CustomInstructions is { Count: > 0 })
        {
            instructions.Add("Custom instructions:");
            for (var i = 0; i < r.CustomInstructions.Count; i++)
                instructions.Add($"{i + 1}. {r.CustomInstructions[i]}");
        }
        if (r.DoNotTranslate is { Count: > 0 })
            instructions.Add("Do not translate: " + string.Join(", ", r.DoNotTranslate));
        return string.Join('\n', instructions);
    }

    private readonly record struct LlmBatchKey(
        string Source, string Target, string? Glossary, string? StyleId,
        string? CustomInstructions, Formality Formality)
    {
        public LlmBatchKey(TranslationRequest r) : this(
            r.Source.Value, r.Target.Value, r.GlossaryId, r.StyleRuleId,
            r.CustomInstructions is null ? null : string.Join("|", r.CustomInstructions),
            r.Formality) { }
    }
}
