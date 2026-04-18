using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Api.Admin;

public static class ProxyRuleEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var pr = app.MapGroup("/admin/proxy-rules");
        pr.MapPost("/", Create);
        pr.MapGet("/", List);
        pr.MapDelete("/{id}", Delete);
    }

    public sealed record CreateDto(
        string Id, string TenantId, string Name,
        string? ScopeJson, string? AllowlistJson, string? DenylistJson,
        string? PlaceholderPatternsJson, string? Formality, int Priority,
        bool RedactPii = false, string? SystemContext = null);

    public sealed record Dto(
        string Id, string TenantId, string Name,
        string ScopeJson, string? AllowlistJson, string? DenylistJson,
        string? Formality, int Priority, bool RedactPii, string? SystemContext);

    private static async Task<IResult> Create(CreateDto dto, AdaptiveApiDbContext db, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == dto.TenantId, ct))
            return Results.BadRequest(new { error = "tenant_missing" });
        if (await db.ProxyRules.AnyAsync(x => x.Id == dto.Id, ct))
            return Results.Conflict(new { error = "proxy_rule_exists" });

        db.ProxyRules.Add(new ProxyRuleEntity
        {
            Id = dto.Id, TenantId = dto.TenantId, Name = dto.Name,
            ScopeJson = dto.ScopeJson ?? "{}",
            AllowlistJson = dto.AllowlistJson,
            DenylistJson = dto.DenylistJson,
            PlaceholderPatternsJson = dto.PlaceholderPatternsJson,
            Formality = dto.Formality,
            Priority = dto.Priority,
            RedactPii = dto.RedactPii,
            SystemContext = dto.SystemContext is { Length: > 4000 } ? dto.SystemContext[..4000] : dto.SystemContext,
        });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/admin/proxy-rules/{dto.Id}",
            new Dto(dto.Id, dto.TenantId, dto.Name, dto.ScopeJson ?? "{}",
                dto.AllowlistJson, dto.DenylistJson, dto.Formality, dto.Priority, dto.RedactPii, dto.SystemContext));
    }

    private static async Task<IResult> List(AdaptiveApiDbContext db, CancellationToken ct) =>
        Results.Ok(await db.ProxyRules.AsNoTracking()
            .OrderByDescending(x => x.Priority)
            .Select(x => new Dto(x.Id, x.TenantId, x.Name, x.ScopeJson,
                x.AllowlistJson, x.DenylistJson, x.Formality, x.Priority, x.RedactPii, x.SystemContext))
            .ToListAsync(ct));

    private static async Task<IResult> Delete(string id, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var r = await db.ProxyRules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Results.NotFound();
        db.ProxyRules.Remove(r);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
