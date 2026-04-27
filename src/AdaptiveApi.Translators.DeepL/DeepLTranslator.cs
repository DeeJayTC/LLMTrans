using DeepL;
using DeepL.Model;
using AdaptiveApi.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CoreFormality = AdaptiveApi.Core.Abstractions.Formality;
using CoreTagHandling = AdaptiveApi.Core.Abstractions.TagHandling;
using CoreTranslator = AdaptiveApi.Core.Abstractions.ITranslator;
using CoreTranslationRequest = AdaptiveApi.Core.Abstractions.TranslationRequest;
using CoreTranslationResult = AdaptiveApi.Core.Abstractions.TranslationResult;
using CoreLanguageCode = AdaptiveApi.Core.Model.LanguageCode;
using SdkFormality = DeepL.Formality;
using SdkModelType = DeepL.ModelType;

namespace AdaptiveApi.Translators.DeepL;

/// DeepL translator built on the official DeepL.net SDK (1.21+).
/// `TextTranslateOptions` carries glossary_id, style_id, custom_instructions,
/// model_type, formality, tag handling, and ignore_tags=adaptiveapi so our
/// placeholder tags pass through untouched.
///
/// When a request carries a <c>TranslationMemoryId</c>, the call is dispatched
/// via <see cref="DeepLApiClient"/> against the v2 HTTP endpoint instead of the
/// SDK, because the SDK does not yet expose translation_memory_id /
/// translation_memory_threshold. The SDK path is otherwise preferred (faster,
/// pooled, well-tested).
public sealed class DeepLTranslator : CoreTranslator, IDisposable
{
    private readonly IOptions<DeepLOptions> _options;
    private readonly ILogger<DeepLTranslator> _log;
    private readonly Lazy<DeepLClient> _client;
    private readonly DeepLApiClient? _httpClient;

    public DeepLTranslator(
        IOptions<DeepLOptions> options,
        ILogger<DeepLTranslator> log,
        DeepLApiClient? httpClient = null)
    {
        _options = options;
        _log = log;
        _client = new Lazy<DeepLClient>(CreateClient, isThreadSafe: true);
        _httpClient = httpClient;
    }

    public string TranslatorId => "deepl";

    public TranslatorCapabilities Capabilities =>
        TranslatorCapabilities.Glossary
        | TranslatorCapabilities.Formality
        | TranslatorCapabilities.TagHandling
        | TranslatorCapabilities.Batching
        | TranslatorCapabilities.AutoDetect
        | TranslatorCapabilities.DocumentApi
        | TranslatorCapabilities.StyleRules
        | TranslatorCapabilities.CustomInstructions
        | TranslatorCapabilities.MultilingualGlossary
        | TranslatorCapabilities.TranslationMemory;

    public async Task<IReadOnlyList<CoreTranslationResult>> TranslateBatchAsync(
        IReadOnlyList<CoreTranslationRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return Array.Empty<CoreTranslationResult>();

        var results = new CoreTranslationResult[requests.Count];

        // The translate call accepts one (source, target, options) triplet per batch,
        // so partition by the full request option set (which now includes TM id).
        var groups = requests
            .Select((r, i) => (Req: r, Index: i))
            .GroupBy(x => BatchKey.From(x.Req));

        foreach (var group in groups)
        {
            var items = group.ToList();
            var first = items[0].Req;

            if (!string.IsNullOrEmpty(first.TranslationMemoryId))
                await TranslateGroupViaHttpAsync(items, first, results, ct);
            else
                await TranslateGroupViaSdkAsync(items, first, results, ct);
        }

        return results;
    }

    private async Task TranslateGroupViaSdkAsync(
        List<(CoreTranslationRequest Req, int Index)> items,
        CoreTranslationRequest first,
        CoreTranslationResult[] results,
        CancellationToken ct)
    {
        var client = _client.Value;
        var texts = items.Select(x => x.Req.Text).ToArray();
        var options = BuildSdkOptions(first);

        try
        {
            var sourceLang = NormalizeSource(first.Source.Value);
            var targetLang = NormalizeTarget(first.Target.Value);

            var translated = await client.TranslateTextAsync(texts, sourceLang, targetLang, options, ct);
            for (var i = 0; i < items.Count; i++)
            {
                var t = translated[i];
                results[items[i].Index] = new CoreTranslationResult(
                    t.Text,
                    string.IsNullOrEmpty(t.DetectedSourceLanguageCode) ? null
                        : new CoreLanguageCode(t.DetectedSourceLanguageCode.ToLowerInvariant()));
            }
        }
        catch (DeepLException ex)
        {
            _log.LogError(ex, "DeepL SDK call failed for source={Source} target={Target}; falling back to source text",
                first.Source.Value, first.Target.Value);
            for (var i = 0; i < items.Count; i++)
                results[items[i].Index] = new CoreTranslationResult(items[i].Req.Text);
        }
    }

    private async Task TranslateGroupViaHttpAsync(
        List<(CoreTranslationRequest Req, int Index)> items,
        CoreTranslationRequest first,
        CoreTranslationResult[] results,
        CancellationToken ct)
    {
        if (_httpClient is null)
        {
            _log.LogError("DeepL Translation Memory request received but DeepLApiClient is not registered; falling back to source text");
            for (var i = 0; i < items.Count; i++)
                results[items[i].Index] = new CoreTranslationResult(items[i].Req.Text);
            return;
        }

        var texts = items.Select(x => x.Req.Text).ToArray();

        // DeepL rejects TM requests against latency_optimized — coerce to quality_optimized
        // (the only TM-compatible model) regardless of the caller's preference.
        var modelType = MapModelTypeWire(first.ModelType);
        if (modelType == "latency_optimized") modelType = "quality_optimized";
        if (string.IsNullOrEmpty(modelType)) modelType = "quality_optimized";

        var context = !string.IsNullOrEmpty(first.Context) ? first.Context : _options.Value.SystemContext;
        if (!string.IsNullOrEmpty(context) && context.Length > 4000) context = context[..4000];

        var request = new TranslateTextRequest
        {
            Text = texts,
            SourceLang = NormalizeSource(first.Source.Value),
            TargetLang = NormalizeTarget(first.Target.Value),
            TranslationMemoryId = first.TranslationMemoryId,
            TranslationMemoryThreshold = first.TranslationMemoryThreshold,
            GlossaryId = string.IsNullOrEmpty(first.GlossaryId) ? null : first.GlossaryId,
            StyleId = string.IsNullOrEmpty(first.StyleRuleId) ? null : first.StyleRuleId,
            Formality = MapFormalityWire(first.Formality),
            ModelType = modelType,
            TagHandling = MapTagHandlingWire(first.TagHandling),
            IgnoreTags = new[] { "adaptiveapi" },
            Context = context,
            CustomInstructions = first.CustomInstructions is { Count: > 0 }
                ? first.CustomInstructions
                : null,
        };

        var response = await _httpClient.TranslateTextAsync(request, ct);
        if (response is null || response.Translations.Count < items.Count)
        {
            _log.LogError("DeepL HTTP TM call returned no/short payload for source={Source} target={Target} tm={Tm}; falling back",
                first.Source.Value, first.Target.Value, first.TranslationMemoryId);
            for (var i = 0; i < items.Count; i++)
                results[items[i].Index] = new CoreTranslationResult(items[i].Req.Text);
            return;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var t = response.Translations[i];
            results[items[i].Index] = new CoreTranslationResult(
                t.Text,
                string.IsNullOrEmpty(t.DetectedSourceLanguage) ? null
                    : new CoreLanguageCode(t.DetectedSourceLanguage.ToLowerInvariant()));
        }
    }

    private DeepLClient CreateClient()
    {
        var apiKey = _options.Value.ApiKey
            ?? throw new InvalidOperationException("DeepL API key not configured");

        if (!string.IsNullOrEmpty(_options.Value.BaseUrl))
        {
            var clientOptions = new DeepLClientOptions { ServerUrl = _options.Value.BaseUrl };
            return new DeepLClient(apiKey, clientOptions);
        }
        return new DeepLClient(apiKey);
    }

    private TextTranslateOptions BuildSdkOptions(CoreTranslationRequest r)
    {
        var opts = new TextTranslateOptions
        {
            TagHandling = r.TagHandling == CoreTagHandling.Xml ? "xml"
                        : r.TagHandling == CoreTagHandling.Html ? "html"
                        : null,
        };

        opts.IgnoreTags.Add("adaptiveapi");

        if (!string.IsNullOrEmpty(r.GlossaryId)) opts.GlossaryId = r.GlossaryId;
        if (!string.IsNullOrEmpty(r.StyleRuleId)) opts.StyleId = r.StyleRuleId;

        // Context: use request-level context (from pipeline/streaming accumulation)
        // falling back to the global DeepLOptions.SystemContext. Cap to 4 000 chars.
        var context = !string.IsNullOrEmpty(r.Context) ? r.Context : _options.Value.SystemContext;
        if (!string.IsNullOrEmpty(context))
            opts.Context = context.Length > 4000 ? context[..4000] : context;

        var formality = MapFormality(r.Formality);
        if (formality is not null) opts.Formality = formality.Value;

        var modelType = MapModelType(r.ModelType);
        if (modelType is not null) opts.ModelType = modelType.Value;

        if (r.CustomInstructions is { Count: > 0 })
        {
            foreach (var instruction in r.CustomInstructions)
                opts.CustomInstructions.Add(instruction);
        }

        return opts;
    }

    private static SdkFormality? MapFormality(CoreFormality f) => f switch
    {
        CoreFormality.Default => null,
        CoreFormality.More => SdkFormality.More,
        CoreFormality.Less => SdkFormality.Less,
        CoreFormality.PreferMore => SdkFormality.PreferMore,
        CoreFormality.PreferLess => SdkFormality.PreferLess,
        _ => null,
    };

    private static string? MapFormalityWire(CoreFormality f) => f switch
    {
        CoreFormality.Default => null,
        CoreFormality.More => "more",
        CoreFormality.Less => "less",
        CoreFormality.PreferMore => "prefer_more",
        CoreFormality.PreferLess => "prefer_less",
        _ => null,
    };

    private static SdkModelType? MapModelType(string? value) => value switch
    {
        null or "" => null,
        "quality_optimized" => SdkModelType.QualityOptimized,
        "latency_optimized" => SdkModelType.LatencyOptimized,
        "prefer_quality_optimized" => SdkModelType.PreferQualityOptimized,
        _ => null,
    };

    private static string? MapModelTypeWire(string? value) => value switch
    {
        null or "" => null,
        "quality_optimized" => "quality_optimized",
        "latency_optimized" => "latency_optimized",
        "prefer_quality_optimized" => "prefer_quality_optimized",
        _ => null,
    };

    private static string? MapTagHandlingWire(CoreTagHandling t) => t switch
    {
        CoreTagHandling.Xml => "xml",
        CoreTagHandling.Html => "html",
        _ => null,
    };

    private static string? NormalizeSource(string value) =>
        string.IsNullOrEmpty(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.ToUpperInvariant();

    /// DeepL deprecated plain two-letter codes for target languages where a regional
    /// variant is ambiguous (EN, PT). Map bare codes to the preferred variant so
    /// operators don't get runtime deprecation warnings. LLMs mostly work best in
    /// American English, so EN defaults to EN-US.
    private static string NormalizeTarget(string value)
    {
        if (string.IsNullOrEmpty(value)) return "EN-US";
        var upper = value.ToUpperInvariant();
        return upper switch
        {
            "EN" => "EN-US",
            "PT" => "PT-BR",
            _ => upper,
        };
    }

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }

    private readonly record struct BatchKey(
        string Source, string Target, string? GlossaryId, CoreFormality Formality,
        string? StyleId, string? CustomInstructions, string? ModelType, CoreTagHandling TagHandling,
        string? TranslationMemoryId, int? TranslationMemoryThreshold)
    {
        public static BatchKey From(CoreTranslationRequest r) => new(
            r.Source.Value, r.Target.Value, r.GlossaryId, r.Formality,
            r.StyleRuleId,
            r.CustomInstructions is null ? null : string.Join("|", r.CustomInstructions),
            r.ModelType, r.TagHandling,
            r.TranslationMemoryId, r.TranslationMemoryThreshold);
    }
}
