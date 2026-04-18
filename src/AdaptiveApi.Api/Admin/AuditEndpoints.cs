using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Api.Admin;

public static class AuditEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/logs", ListEvents);
    }

    public sealed record AuditDto(
        long Id, string TenantId, string? RouteId, string Method, string Path, int Status,
        string UserLanguage, string LlmLanguage, string Direction,
        string? TranslatorId, string? GlossaryId,
        string? RequestStyleRuleId, string? ResponseStyleRuleId,
        int RequestChars, int ResponseChars, int IntegrityFailures,
        long DurationMs, DateTimeOffset CreatedAt);

    public sealed record PageDto(IReadOnlyList<AuditDto> Items, long NextBefore);

    private static async Task<IResult> ListEvents(
        string? tenantId, string? routeId, int? status, long? before, int? limit,
        AdaptiveApiDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);

        IQueryable<AuditEventEntity> q = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrEmpty(tenantId)) q = q.Where(e => e.TenantId == tenantId);
        if (!string.IsNullOrEmpty(routeId))  q = q.Where(e => e.RouteId == routeId);
        if (status is not null)              q = q.Where(e => e.Status == status);
        if (before is not null)              q = q.Where(e => e.Id < before);

        var rows = await q.OrderByDescending(e => e.Id).Take(take)
            .Select(e => new AuditDto(
                e.Id, e.TenantId, e.RouteId, e.Method, e.Path, e.Status,
                e.UserLanguage, e.LlmLanguage, e.Direction,
                e.TranslatorId, e.GlossaryId,
                e.RequestStyleRuleId, e.ResponseStyleRuleId,
                e.RequestChars, e.ResponseChars, e.IntegrityFailures,
                e.DurationMs, e.CreatedAt))
            .ToListAsync(ct);

        var nextBefore = rows.Count == take ? rows[^1].Id : 0;
        return Results.Ok(new PageDto(rows, nextBefore));
    }
}
