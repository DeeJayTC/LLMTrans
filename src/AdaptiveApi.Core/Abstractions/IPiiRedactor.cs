using AdaptiveApi.Core.Pipeline;

namespace AdaptiveApi.Core.Abstractions;

public interface IPiiRedactor
{
    /// Stable identifier surfaced in audit logs and metrics.
    string RedactorId { get; }

    /// Detect + replace PII spans in `input`. Returns the redacted text plus a list of
    /// `Placeholder` entries whose `Original` is the opaque substitute
    /// (`[redacted-email]`, …) to be reinjected post-translation.
    Task<PiiRedactor.Result> RedactAsync(string input, CancellationToken ct = default);
}
