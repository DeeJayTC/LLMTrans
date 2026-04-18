using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveApi.Infrastructure.Audit;

public sealed class DbAuditSink : IAuditSink
{
    private readonly IServiceProvider _services;

    public DbAuditSink(IServiceProvider services) => _services = services;

    public async Task RecordAsync(AuditRecord r, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdaptiveApiDbContext>();

        db.AuditEvents.Add(new AuditEventEntity
        {
            TenantId = r.TenantId,
            RouteId = r.RouteId,
            Method = r.Method,
            Path = r.Path,
            Status = r.Status,
            UserLanguage = r.UserLanguage,
            LlmLanguage = r.LlmLanguage,
            Direction = r.Direction,
            TranslatorId = r.TranslatorId,
            GlossaryId = r.GlossaryId,
            RequestStyleRuleId = r.RequestStyleRuleId,
            ResponseStyleRuleId = r.ResponseStyleRuleId,
            RequestChars = r.RequestChars,
            ResponseChars = r.ResponseChars,
            IntegrityFailures = r.IntegrityFailures,
            DurationMs = r.DurationMs,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
