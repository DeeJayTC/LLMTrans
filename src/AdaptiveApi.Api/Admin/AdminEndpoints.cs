using AdaptiveApi.Core.Routing;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Api.Admin;

public static class AdminEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin");
        admin.MapPost("/tenants", CreateTenant);
        admin.MapGet("/tenants", ListTenants);

        admin.MapPost("/routes", CreateRoute);
        admin.MapGet("/routes", ListRoutes);
        admin.MapPatch("/routes/{id}", UpdateRoute);
        admin.MapDelete("/routes/{id}", DeleteRoute);

        admin.MapPost("/routes/{id}/tokens", IssueToken);
        admin.MapGet("/routes/{id}/tokens", ListTokens);
        admin.MapPost("/tokens/{tokenId}/revoke", RevokeToken);
    }

    public sealed record CreateTenantDto(string Id, string Name);
    public sealed record TenantDto(string Id, string Name, DateTimeOffset CreatedAt);

    public sealed record CreateRouteDto(
        string Id, string TenantId, string Kind, string UpstreamBaseUrl,
        string UserLanguage, string LlmLanguage, string Direction,
        string? TranslatorId, string? GlossaryId,
        string? RequestStyleRuleId, string? ResponseStyleRuleId,
        string? ProxyRuleId, string? ConfigJson);

    public sealed record UpdateRouteDto(
        string? UpstreamBaseUrl, string? UserLanguage, string? LlmLanguage,
        string? Direction, string? TranslatorId, string? GlossaryId,
        string? RequestStyleRuleId, string? ResponseStyleRuleId,
        string? ProxyRuleId, string? ConfigJson);

    public sealed record RouteDto(
        string Id, string TenantId, string Kind, string UpstreamBaseUrl,
        string UserLanguage, string LlmLanguage, string Direction,
        string? TranslatorId, string? GlossaryId,
        string? RequestStyleRuleId, string? ResponseStyleRuleId,
        string? ProxyRuleId, string? ConfigJson);

    public sealed record IssueTokenResponse(string TokenId, string PlaintextToken);
    public sealed record TokenSummary(string Id, string RouteId, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

    private static async Task<IResult> CreateTenant(CreateTenantDto dto, AdaptiveApiDbContext db, CancellationToken ct)
    {
        if (await db.Tenants.AnyAsync(t => t.Id == dto.Id, ct))
            return Results.Conflict(new { error = "tenant_exists" });

        db.Tenants.Add(new TenantEntity { Id = dto.Id, Name = dto.Name, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/admin/tenants/{dto.Id}", new TenantDto(dto.Id, dto.Name, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> ListTenants(AdaptiveApiDbContext db, CancellationToken ct)
    {
        var tenants = await db.Tenants.AsNoTracking()
            .Select(t => new TenantDto(t.Id, t.Name, t.CreatedAt)).ToListAsync(ct);
        return Results.Ok(tenants);
    }

    private static async Task<IResult> CreateRoute(CreateRouteDto dto, AdaptiveApiDbContext db, CancellationToken ct)
    {
        if (!Enum.TryParse<RouteKind>(dto.Kind, true, out _))
            return Results.BadRequest(new { error = "unknown_route_kind", kind = dto.Kind });

        if (!await db.Tenants.AnyAsync(t => t.Id == dto.TenantId, ct))
            return Results.BadRequest(new { error = "tenant_missing" });

        db.Routes.Add(new RouteEntity
        {
            Id = dto.Id,
            TenantId = dto.TenantId,
            Kind = dto.Kind,
            UpstreamBaseUrl = dto.UpstreamBaseUrl,
            UserLanguage = dto.UserLanguage,
            LlmLanguage = dto.LlmLanguage,
            Direction = dto.Direction,
            TranslatorId = dto.TranslatorId,
            GlossaryId = dto.GlossaryId,
            RequestStyleRuleId = dto.RequestStyleRuleId,
            ResponseStyleRuleId = dto.ResponseStyleRuleId,
            ProxyRuleId = dto.ProxyRuleId,
            ConfigJson = dto.ConfigJson,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/admin/routes/{dto.Id}", ToDto(await db.Routes.FirstAsync(r => r.Id == dto.Id, ct)));
    }

    private static async Task<IResult> ListRoutes(AdaptiveApiDbContext db, CancellationToken ct)
    {
        var routes = await db.Routes.AsNoTracking().ToListAsync(ct);
        return Results.Ok(routes.Select(ToDto));
    }

    private static async Task<IResult> UpdateRoute(string id, UpdateRouteDto dto, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var r = await db.Routes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Results.NotFound();

        if (dto.UpstreamBaseUrl is not null) r.UpstreamBaseUrl = dto.UpstreamBaseUrl;
        if (dto.UserLanguage is not null) r.UserLanguage = dto.UserLanguage;
        if (dto.LlmLanguage is not null) r.LlmLanguage = dto.LlmLanguage;
        if (dto.Direction is not null) r.Direction = dto.Direction;
        if (dto.TranslatorId is not null) r.TranslatorId = dto.TranslatorId;
        if (dto.GlossaryId is not null) r.GlossaryId = dto.GlossaryId;
        if (dto.RequestStyleRuleId is not null) r.RequestStyleRuleId = dto.RequestStyleRuleId;
        if (dto.ResponseStyleRuleId is not null) r.ResponseStyleRuleId = dto.ResponseStyleRuleId;
        if (dto.ProxyRuleId is not null) r.ProxyRuleId = dto.ProxyRuleId;
        if (dto.ConfigJson is not null) r.ConfigJson = dto.ConfigJson;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(r));
    }

    private static async Task<IResult> DeleteRoute(string id, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var r = await db.Routes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Results.NotFound();
        db.Routes.Remove(r);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> IssueToken(string id, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var r = await db.Routes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Results.NotFound();

        var plaintext = RouteToken.Generate(r.TenantId);
        var tokenId = Guid.NewGuid().ToString("N");
        db.RouteTokens.Add(new RouteTokenEntity
        {
            Id = tokenId,
            TenantId = r.TenantId,
            RouteId = r.Id,
            Prefix = RouteToken.PrefixOf(plaintext),
            Hash = RouteToken.HashForStorage(plaintext),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/admin/tokens/{tokenId}", new IssueTokenResponse(tokenId, plaintext));
    }

    private static async Task<IResult> ListTokens(string id, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var list = await db.RouteTokens.AsNoTracking()
            .Where(t => t.RouteId == id)
            .Select(t => new TokenSummary(t.Id, t.RouteId, t.Prefix, t.CreatedAt, t.RevokedAt))
            .ToListAsync(ct);
        return Results.Ok(list);
    }

    private static async Task<IResult> RevokeToken(string tokenId, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var t = await db.RouteTokens.FirstOrDefaultAsync(x => x.Id == tokenId, ct);
        if (t is null) return Results.NotFound();
        if (t.RevokedAt is null)
        {
            t.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    private static RouteDto ToDto(RouteEntity r) => new(
        r.Id, r.TenantId, r.Kind, r.UpstreamBaseUrl, r.UserLanguage, r.LlmLanguage,
        r.Direction, r.TranslatorId, r.GlossaryId,
        r.RequestStyleRuleId, r.ResponseStyleRuleId, r.ProxyRuleId, r.ConfigJson);
}
