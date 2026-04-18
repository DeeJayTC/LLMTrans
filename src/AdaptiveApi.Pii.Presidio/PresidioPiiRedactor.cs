using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdaptiveApi.Pii.Presidio;

/// `IPiiRedactor` backed by Microsoft Presidio Analyzer running as an HTTP sidecar.
/// On any failure (network, timeout, 4xx/5xx) the redactor falls through to the local
/// `RegexPiiRedactor` so we fail-open on detection coverage instead of letting PII
/// flow upstream untransformed.
public sealed class PresidioPiiRedactor : IPiiRedactor
{
    private const string TagPrefix = "adaptiveapi";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<PresidioOptions> _options;
    private readonly ILogger<PresidioPiiRedactor> _log;
    private readonly RegexPiiRedactor _fallback = new();

    public PresidioPiiRedactor(
        IHttpClientFactory httpFactory,
        IOptions<PresidioOptions> options,
        ILogger<PresidioPiiRedactor> log)
    {
        _httpFactory = httpFactory;
        _options = options;
        _log = log;
    }

    public string RedactorId => "presidio";

    public async Task<PiiRedactor.Result> RedactAsync(string input, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(input))
            return new PiiRedactor.Result(input, Array.Empty<Placeholder>());

        var opts = _options.Value;
        var http = _httpFactory.CreateClient("presidio");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(opts.TimeoutMs);

        List<AnalyzerHit>? hits;
        try
        {
            var resp = await http.PostAsJsonAsync(
                new Uri(new Uri(opts.AnalyzerUrl.TrimEnd('/') + "/"), "analyze"),
                new AnalyzerRequest { Text = input, Language = opts.Language, ScoreThreshold = opts.MinScore },
                cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Presidio analyzer HTTP {Status}; falling back to regex detector",
                    (int)resp.StatusCode);
                return await _fallback.RedactAsync(input, ct);
            }
            hits = await resp.Content.ReadFromJsonAsync<List<AnalyzerHit>>(cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Presidio analyzer call failed; falling back to regex detector");
            return await _fallback.RedactAsync(input, ct);
        }

        if (hits is null || hits.Count == 0)
            return new PiiRedactor.Result(input, Array.Empty<Placeholder>());

        return ApplyRedactions(input, hits);
    }

    private static PiiRedactor.Result ApplyRedactions(string input, IEnumerable<AnalyzerHit> hits)
    {
        // Sort by start descending so index-based substring replacement doesn't invalidate later offsets.
        var sorted = hits
            .Where(h => h.End > h.Start && h.Start >= 0 && h.End <= input.Length)
            .OrderByDescending(h => h.Start)
            .ToList();

        if (sorted.Count == 0) return new PiiRedactor.Result(input, Array.Empty<Placeholder>());

        var working = input;
        var redactions = new List<Placeholder>();
        var idxByKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var hit in sorted)
        {
            var kind = hit.EntityType.ToUpperInvariant();
            var replacement = ReplacementForKind(kind);
            idxByKind.TryGetValue(kind, out var i);
            var id = $"PII_{kind}_{i}";
            idxByKind[kind] = i + 1;

            redactions.Add(new Placeholder(id, replacement));
            working = working[..hit.Start] + $"<{TagPrefix} id=\"{id}\"/>" + working[hit.End..];
        }

        // Renumber so the earliest PII span ends up as `PII_KIND_0` (matches RegexPiiRedactor semantics).
        redactions.Reverse();
        for (var i = 0; i < redactions.Count; i++)
        {
            var ph = redactions[i];
            var kind = ph.Id[4..ph.Id.LastIndexOf('_')];
            redactions[i] = new Placeholder($"PII_{kind}_{i}", ph.Original);
        }

        return new PiiRedactor.Result(working, redactions);
    }

    private static string ReplacementForKind(string kind) => kind switch
    {
        "EMAIL_ADDRESS" => "[redacted-email]",
        "PHONE_NUMBER" => "[redacted-phone]",
        "CREDIT_CARD" => "[redacted-card]",
        "US_SSN" => "[redacted-ssn]",
        "IBAN_CODE" => "[redacted-iban]",
        "IP_ADDRESS" => "[redacted-ip]",
        "PERSON" => "[redacted-name]",
        "LOCATION" => "[redacted-location]",
        "DATE_TIME" => "[redacted-date]",
        "URL" => "[redacted-url]",
        "US_DRIVER_LICENSE" => "[redacted-license]",
        "US_PASSPORT" => "[redacted-passport]",
        "CRYPTO" => "[redacted-crypto]",
        "NRP" => "[redacted-nrp]",
        "MEDICAL_LICENSE" => "[redacted-license]",
        _ => "[redacted]",
    };

    private sealed class AnalyzerRequest
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("language")] public string Language { get; set; } = "en";
        [JsonPropertyName("score_threshold")] public double ScoreThreshold { get; set; }
    }

    private sealed class AnalyzerHit
    {
        [JsonPropertyName("entity_type")] public string EntityType { get; set; } = "";
        [JsonPropertyName("start")] public int Start { get; set; }
        [JsonPropertyName("end")] public int End { get; set; }
        [JsonPropertyName("score")] public double Score { get; set; }
    }
}
