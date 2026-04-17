namespace LlmTrans.Core.Abstractions;

public interface IAuditSink
{
    Task RecordAsync(AuditRecord record, CancellationToken ct);
}

public sealed record AuditRecord(
    string TenantId,
    string? RouteId,
    string Method,
    string Path,
    int Status,
    string UserLanguage,
    string LlmLanguage,
    string Direction,
    string? TranslatorId,
    string? GlossaryId,
    string? RequestStyleRuleId,
    string? ResponseStyleRuleId,
    int RequestChars,
    int ResponseChars,
    int IntegrityFailures,
    long DurationMs);
