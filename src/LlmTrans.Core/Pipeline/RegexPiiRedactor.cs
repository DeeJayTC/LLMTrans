using LlmTrans.Core.Abstractions;

namespace LlmTrans.Core.Pipeline;

/// Default PII redactor: regex-based detectors validated with Luhn for credit cards.
/// Thin adapter around the static `PiiRedactor` so DI can swap in a different
/// implementation (e.g. the Presidio HTTP adapter).
public sealed class RegexPiiRedactor : IPiiRedactor
{
    public string RedactorId => "regex";

    public Task<PiiRedactor.Result> RedactAsync(string input, CancellationToken ct = default)
        => Task.FromResult(PiiRedactor.Redact(input));
}
