using AdaptiveApi.Core.Abstractions;

namespace AdaptiveApi.Core.Pipeline;

/// Default PII redactor: regex-based detectors validated with Luhn for credit cards.
/// Honours per-call detector sets so routes with tenant-specific packs and custom
/// rules pick the right pattern bundle without rebuilding the redactor.
public sealed class RegexPiiRedactor : IPiiRedactor
{
    public string RedactorId => "regex";

    public Task<PiiRedactor.Result> RedactAsync(string input, CancellationToken ct = default)
        => Task.FromResult(PiiRedactor.Redact(input, PiiDetectorSet.Default));

    public Task<PiiRedactor.Result> RedactAsync(string input, PiiDetectorSet? detectors, CancellationToken ct = default)
        => Task.FromResult(PiiRedactor.Redact(input, detectors ?? PiiDetectorSet.Default));
}
