using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Api.Admin;

/// CRUD for <see cref="RouteProfileEntity"/>. Profiles bundle the
/// glossary + style + proxy bindings a route typically needs into a single
/// reusable row, so the routes form can pick a profile rather than four
/// independent FKs. Per-route bindings still override the profile when set.
public static class RouteProfileEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/route-profiles");
        g.MapGet("", List);
        g.MapPost("", Create);
        g.MapPatch("/{id}", Update);
        g.MapDelete("/{id}", Delete);
    }

    public sealed record ProfileDto(
        string Id, string TenantId, string Name, string? Description,
        string? GlossaryId, string? RequestStyleRuleId, string? ResponseStyleRuleId,
        string? ProxyRuleId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public sealed record CreateProfileDto(
        string Id, string TenantId, string Name, string? Description,
        string? GlossaryId, string? RequestStyleRuleId, string? ResponseStyleRuleId,
        string? ProxyRuleId);

    public sealed record UpdateProfileDto(
        string? Name, string? Description,
        string? GlossaryId, string? RequestStyleRuleId, string? ResponseStyleRuleId,
        string? ProxyRuleId);

    private static async Task<IResult> List(AdaptiveApiDbContext db, CancellationToken ct)
    {
        var rows = await db.RouteProfiles.AsNoTracking()
            .OrderBy(p => p.Name).ToListAsync(ct);
        return Results.Ok(rows.Select(ToDto));
    }

    private static async Task<IResult> Create([FromBody] CreateProfileDto dto, AdaptiveApiDbContext db, CancellationToken ct)
    {
        if (await db.RouteProfiles.AnyAsync(p => p.Id == dto.Id, ct))
            return Results.Conflict(new { error = "profile_exists" });
        if (!await db.Tenants.AnyAsync(t => t.Id == dto.TenantId, ct))
            return Results.BadRequest(new { error = "tenant_missing" });

        var now = DateTimeOffset.UtcNow;
        var row = new RouteProfileEntity
        {
            Id = dto.Id,
            TenantId = dto.TenantId,
            Name = dto.Name,
            Description = dto.Description,
            GlossaryId = NullIfEmpty(dto.GlossaryId),
            RequestStyleRuleId = NullIfEmpty(dto.RequestStyleRuleId),
            ResponseStyleRuleId = NullIfEmpty(dto.ResponseStyleRuleId),
            ProxyRuleId = NullIfEmpty(dto.ProxyRuleId),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.RouteProfiles.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/admin/route-profiles/{dto.Id}", ToDto(row));
    }

    private static async Task<IResult> Update(string id, [FromBody] UpdateProfileDto dto, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var row = await db.RouteProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return Results.NotFound();

        // Treat empty strings on FK fields as "clear the binding" so the UI
        // can null out a slot without a separate endpoint.
        if (dto.Name is not null) row.Name = dto.Name;
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.GlossaryId is not null) row.GlossaryId = NullIfEmpty(dto.GlossaryId);
        if (dto.RequestStyleRuleId is not null) row.RequestStyleRuleId = NullIfEmpty(dto.RequestStyleRuleId);
        if (dto.ResponseStyleRuleId is not null) row.ResponseStyleRuleId = NullIfEmpty(dto.ResponseStyleRuleId);
        if (dto.ProxyRuleId is not null) row.ProxyRuleId = NullIfEmpty(dto.ProxyRuleId);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(row));
    }

    private static async Task<IResult> Delete(string id, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var row = await db.RouteProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return Results.NotFound();

        // Detach any routes still pointing at this profile so the deletion
        // doesn't break their resolver lookups. The per-route bindings the
        // routes already had keep working.
        var inUse = await db.Routes.Where(r => r.ProfileId == id).ToListAsync(ct);
        foreach (var r in inUse) r.ProfileId = null;

        db.RouteProfiles.Remove(row);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static ProfileDto ToDto(RouteProfileEntity p) => new(
        p.Id, p.TenantId, p.Name, p.Description,
        p.GlossaryId, p.RequestStyleRuleId, p.ResponseStyleRuleId, p.ProxyRuleId,
        p.CreatedAt, p.UpdatedAt);
}
