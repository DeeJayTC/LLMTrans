using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaptiveApi.Translators.DeepL;

/// <summary>
/// Thin HTTP client for the DeepL API endpoints that the official .NET SDK
/// (1.21) does not yet expose:
///   • POST /v2/translate with `translation_memory_id` / `translation_memory_threshold`
///   • GET  /v3/translation_memories (list)
///
/// Once the SDK ships native TM support these calls should move back onto
/// <c>DeepLClient</c> and this class can be deleted.
/// </summary>
public sealed class DeepLApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<DeepLOptions> _options;
    private readonly ILogger<DeepLApiClient> _log;

    public DeepLApiClient(
        IHttpClientFactory httpFactory,
        IOptions<DeepLOptions> options,
        ILogger<DeepLApiClient> log)
    {
        _httpFactory = httpFactory;
        _options = options;
        _log = log;
    }

    public async Task<TranslateTextResponse?> TranslateTextAsync(TranslateTextRequest request, CancellationToken ct)
    {
        var http = CreateClient();
        var url = new Uri(BaseUri(), "v2/translate");

        using var resp = await http.PostAsJsonAsync(url, request, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("DeepL /v2/translate returned {Status}: {Body}", (int)resp.StatusCode, body);
            return null;
        }

        return await resp.Content.ReadFromJsonAsync<TranslateTextResponse>(Json, ct);
    }

    public async Task<TranslationMemoryList?> ListTranslationMemoriesAsync(CancellationToken ct)
    {
        var http = CreateClient();
        var url = new Uri(BaseUri(), "v3/translation_memories");

        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("DeepL /v3/translation_memories returned {Status}: {Body}", (int)resp.StatusCode, body);
            return null;
        }

        return await resp.Content.ReadFromJsonAsync<TranslationMemoryList>(Json, ct);
    }

    private HttpClient CreateClient()
    {
        var apiKey = _options.Value.ApiKey
            ?? throw new InvalidOperationException("DeepL API key not configured");

        var http = _httpFactory.CreateClient("deepl");
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
        return http;
    }

    private Uri BaseUri()
    {
        var baseUrl = _options.Value.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = "https://api.deepl.com/";
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        return new Uri(baseUrl);
    }
}

public sealed class TranslateTextRequest
{
    [JsonPropertyName("text")] public IReadOnlyList<string> Text { get; init; } = Array.Empty<string>();
    [JsonPropertyName("source_lang")] public string? SourceLang { get; init; }
    [JsonPropertyName("target_lang")] public string TargetLang { get; init; } = "EN-US";

    [JsonPropertyName("translation_memory_id")] public string? TranslationMemoryId { get; init; }
    [JsonPropertyName("translation_memory_threshold")] public int? TranslationMemoryThreshold { get; init; }

    [JsonPropertyName("glossary_id")] public string? GlossaryId { get; init; }
    [JsonPropertyName("style_id")] public string? StyleId { get; init; }
    [JsonPropertyName("formality")] public string? Formality { get; init; }
    [JsonPropertyName("model_type")] public string? ModelType { get; init; }
    [JsonPropertyName("tag_handling")] public string? TagHandling { get; init; }
    [JsonPropertyName("ignore_tags")] public IReadOnlyList<string>? IgnoreTags { get; init; }
    [JsonPropertyName("context")] public string? Context { get; init; }
    [JsonPropertyName("custom_instructions")] public IReadOnlyList<string>? CustomInstructions { get; init; }
}

public sealed class TranslateTextResponse
{
    [JsonPropertyName("translations")] public IReadOnlyList<Translation> Translations { get; init; } = Array.Empty<Translation>();
}

public sealed class Translation
{
    [JsonPropertyName("detected_source_language")] public string? DetectedSourceLanguage { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
}

public sealed class TranslationMemoryList
{
    [JsonPropertyName("translation_memories")] public IReadOnlyList<TranslationMemorySummary> TranslationMemories { get; init; } = Array.Empty<TranslationMemorySummary>();
    [JsonPropertyName("total_count")] public int? TotalCount { get; init; }
}

public sealed class TranslationMemorySummary
{
    [JsonPropertyName("translation_memory_id")] public string TranslationMemoryId { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("source_language")] public string? SourceLanguage { get; init; }
    [JsonPropertyName("target_languages")] public IReadOnlyList<string>? TargetLanguages { get; init; }
    [JsonPropertyName("segment_count")] public int? SegmentCount { get; init; }
}
