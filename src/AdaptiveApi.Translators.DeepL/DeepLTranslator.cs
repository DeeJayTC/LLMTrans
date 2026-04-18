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
public sealed class DeepLTranslator : CoreTranslator, IDisposable
{
    private readonly IOptions<DeepLOptions> _options;
    private readonly ILogger<DeepLTranslator> _log;
    private readonly Lazy<DeepLClient> _client;

    public DeepLTranslator(IOptions<DeepLOptions> options, ILogger<DeepLTranslator> log)
    {
        _options = options;
        _log = log;
        _client = new Lazy<DeepLClient>(CreateClient, isThreadSafe: true);
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
        | TranslatorCapabilities.MultilingualGlossary;

    public async Task<IReadOnlyList<CoreTranslationResult>> TranslateBatchAsync(
        IReadOnlyList<CoreTranslationRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0) return Array.Empty<CoreTranslationResult>();

        var client = _client.Value;
        var results = new CoreTranslationResult[requests.Count];

        // The SDK call accepts one (source, target, options) triplet per batch, so
        // partition by the full request option set.
        var groups = requests
            .Select((r, i) => (Req: r, Index: i))
            .GroupBy(x => BatchKey.From(x.Req));

        foreach (var group in groups)
        {
            var items = group.ToList();
            var first = items[0].Req;
            var texts = items.Select(x => x.Req.Text).ToArray();
            var options = BuildOptions(first);

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
                _log.LogError(ex, "DeepL call failed for source={Source} target={Target}; falling back to source text",
                    first.Source.Value, first.Target.Value);
                for (var i = 0; i < items.Count; i++)
                    results[items[i].Index] = new CoreTranslationResult(items[i].Req.Text);
            }
        }

        return results;
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

    private TextTranslateOptions BuildOptions(CoreTranslationRequest r)
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

    private static SdkModelType? MapModelType(string? value) => value switch
    {
        null or "" => null,
        "quality_optimized" => SdkModelType.QualityOptimized,
        "latency_optimized" => SdkModelType.LatencyOptimized,
        "prefer_quality_optimized" => SdkModelType.PreferQualityOptimized,
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
        string? StyleId, string? CustomInstructions, string? ModelType, CoreTagHandling TagHandling)
    {
        public static BatchKey From(CoreTranslationRequest r) => new(
            r.Source.Value, r.Target.Value, r.GlossaryId, r.Formality,
            r.StyleRuleId,
            r.CustomInstructions is null ? null : string.Join("|", r.CustomInstructions),
            r.ModelType, r.TagHandling);
    }
}
