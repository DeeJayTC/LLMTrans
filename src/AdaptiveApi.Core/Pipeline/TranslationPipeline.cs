using System.Text.Json.Nodes;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Model;

namespace AdaptiveApi.Core.Pipeline;

public sealed class PipelineOptions
{
    public LanguageCode Source { get; init; } = LanguageCode.English;
    public LanguageCode Target { get; init; } = LanguageCode.English;
    public string? GlossaryId { get; init; }
    public string? StyleRuleId { get; init; }
    public IReadOnlyList<string>? CustomInstructions { get; init; }
    public IReadOnlyCollection<string>? DoNotTranslateTerms { get; init; }
    public Formality Formality { get; init; } = Formality.Default;
    public string? ModelType { get; init; }
    /// When true, detected PII is redacted to opaque substitutes before the text
    /// is sent to the translator (and therefore before it reaches the upstream).
    public bool RedactPii { get; init; }
    /// Optional override for the PII detector. If null and `RedactPii` is true, the
    /// pipeline uses whatever `IPiiRedactor` the TranslationPipeline was constructed with,
    /// or a process-local RegexPiiRedactor fallback.
    public IPiiRedactor? PiiRedactor { get; init; }
    /// Per-route detector set composed by the rule resolver. When set, regex-based
    /// redactors (RegexPiiRedactor) honour it instead of their default detector list.
    /// Presidio and other non-regex providers ignore this and use their own configuration.
    public PiiDetectorSet? PiiDetectors { get; init; }
    /// Opt-in per-request capture of the raw translator inputs and outputs. When set,
    /// `TranslationPipeline` appends a `TranslationTrace` for each batch it runs.
    /// Non-null means debugging is active for this call.
    public DebugRecorder? Debug { get; init; }
    /// Free-form label the pipeline stamps onto debug traces so UIs can group them
    /// into "request translation" vs. "response translation" buckets.
    public string DebugDirection { get; init; } = "translate";
    /// Optional context string passed to translation requests (e.g. DeepL's `context`
    /// parameter). Combined system context + accumulated conversation history, capped
    /// at 4 000 characters.
    public string? Context { get; init; }
}

public sealed record PipelineStats(int SitesPlanned, int SitesTranslated, int CacheHits, int IntegrityFailures, int PiiRedactions);

public sealed class TranslationPipeline
{
    private static readonly IPiiRedactor FallbackRedactor = new RegexPiiRedactor();

    private readonly ITranslator _translator;
    private readonly IPiiRedactor _defaultRedactor;

    public TranslationPipeline(ITranslator translator, IPiiRedactor? piiRedactor = null)
    {
        _translator = translator;
        _defaultRedactor = piiRedactor ?? FallbackRedactor;
    }

    public async Task<PipelineStats> TranslateInPlaceAsync(
        JsonNode? root,
        Allowlist allowlist,
        PipelineOptions options,
        CancellationToken ct)
    {
        if (root is null) return new PipelineStats(0, 0, 0, 0, 0);
        if (options.Source.Value == options.Target.Value) return new PipelineStats(0, 0, 0, 0, 0);

        var sites = JsonTranslationPlanner.Plan(root, allowlist);
        if (sites.Count == 0) return new PipelineStats(0, 0, 0, 0, 0);

        var redactor = options.PiiRedactor ?? _defaultRedactor;

        var prepared = new List<PreparedSite>(sites.Count);
        var piiCount = 0;
        foreach (var site in sites)
        {
            var redaction = options.RedactPii
                ? await redactor.RedactAsync(site.Source, options.PiiDetectors, ct)
                : new PiiRedactor.Result(site.Source, Array.Empty<Placeholder>());
            piiCount += redaction.Redactions.Count;

            var tokenized = PlaceholderTokenizer.Tokenize(redaction.Text, options.DoNotTranslateTerms);

            // Combined placeholder list: tokenizer-extracted patterns + PII substitutes.
            // Order matters only insofar as IDs collide; both use disjoint namespaces (TAG_n vs PII_*_n).
            var combined = tokenized.Placeholders.Concat(redaction.Redactions).ToList();
            prepared.Add(new PreparedSite(site, tokenized.Text, combined));
        }

        var requests = prepared
            .Select(p => new TranslationRequest(
                Text: p.TokenizedText,
                Source: options.Source,
                Target: options.Target,
                GlossaryId: options.GlossaryId,
                Formality: options.Formality,
                TagHandling: TagHandling.Xml,
                DoNotTranslate: options.DoNotTranslateTerms,
                Context: options.Context,
                StyleRuleId: options.StyleRuleId,
                CustomInstructions: options.CustomInstructions,
                ModelType: options.ModelType))
            .ToList();

        var results = await _translator.TranslateBatchAsync(requests, ct);

        options.Debug?.RecordTranslation(
            direction: options.DebugDirection,
            source: options.Source,
            target: options.Target,
            requests: requests,
            results: results);

        var integrityFailures = 0;
        for (var i = 0; i < prepared.Count; i++)
        {
            var p = prepared[i];
            var translatedRaw = i < results.Count ? results[i].Text : p.TokenizedText;
            var validation = PlaceholderValidator.Validate(translatedRaw, p.Placeholders);
            if (!validation.Ok)
            {
                integrityFailures++;
                continue;
            }
            p.Site.Apply(PlaceholderTokenizer.Reinject(translatedRaw, p.Placeholders));
        }

        return new PipelineStats(sites.Count, sites.Count - integrityFailures, 0, integrityFailures, piiCount);
    }

    private sealed record PreparedSite(TranslationSite Site, string TokenizedText, IReadOnlyList<Placeholder> Placeholders);
}
