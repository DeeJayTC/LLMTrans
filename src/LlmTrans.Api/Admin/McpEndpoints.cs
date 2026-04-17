using LlmTrans.Core.Routing;
using LlmTrans.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlmTrans.Api.Admin;

public static class McpEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var servers = app.MapGroup("/admin/mcp/servers");
        servers.MapPost("/", CreateServer);
        servers.MapGet("/", ListServers);
        servers.MapGet("/{id}", GetServer);
        servers.MapDelete("/{id}", DeleteServer);
        servers.MapGet("/{id}/snippet", GetSnippet);

        var catalog = app.MapGroup("/admin/mcp/catalog");
        catalog.MapGet("/", ListCatalog);
        catalog.MapGet("/{slug}", GetCatalogEntry);
    }

    public sealed record CreateServerDto(
        string Id, string TenantId, string Name,
        string Transport,
        string? RemoteUpstreamUrl,
        string UserLanguage,
        string LlmLanguage,
        string? TranslatorId,
        string? GlossaryId,
        string? StyleRuleId,
        string? ProxyRuleId,
        string? CatalogEntryId);

    public sealed record ServerDto(
        string Id, string TenantId, string Name,
        string Transport, string? RemoteUpstreamUrl,
        string UserLanguage, string LlmLanguage,
        string? TranslatorId, string? GlossaryId, string? StyleRuleId, string? ProxyRuleId,
        string? CatalogEntryId, DateTimeOffset CreatedAt, DateTimeOffset? DisabledAt);

    public sealed record CreateServerResponse(ServerDto Server, string RouteToken);

    public sealed record CatalogDto(
        string Id, string Slug, string DisplayName, string Description,
        string Transport, string? UpstreamUrl, string? UpstreamCommandHint,
        string? DocsUrl, string? IconUrl, string Publisher, bool Verified);

    private static async Task<IResult> CreateServer(CreateServerDto dto, LlmTransDbContext db, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == dto.TenantId, ct))
            return Results.BadRequest(new { error = "tenant_missing" });
        if (dto.Transport is not ("remote" or "stdio-local"))
            return Results.BadRequest(new { error = "invalid_transport", got = dto.Transport });
        if (dto.Transport == "remote" && string.IsNullOrWhiteSpace(dto.RemoteUpstreamUrl))
            return Results.BadRequest(new { error = "remote_upstream_url_required" });
        if (await db.McpServers.AnyAsync(x => x.Id == dto.Id, ct))
            return Results.Conflict(new { error = "server_exists" });

        var plaintextToken = RouteToken.Generate(dto.TenantId);
        var tokenId = Guid.NewGuid().ToString("N");
        db.RouteTokens.Add(new RouteTokenEntity
        {
            Id = tokenId,
            TenantId = dto.TenantId,
            RouteId = dto.Id,
            Scope = "mcp-server",
            Prefix = RouteToken.PrefixOf(plaintextToken),
            Hash = RouteToken.HashForStorage(plaintextToken),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var now = DateTimeOffset.UtcNow;
        db.McpServers.Add(new McpServerEntity
        {
            Id = dto.Id,
            TenantId = dto.TenantId,
            Name = dto.Name,
            Transport = dto.Transport,
            RemoteUpstreamUrl = dto.RemoteUpstreamUrl,
            UserLanguage = dto.UserLanguage,
            LlmLanguage = dto.LlmLanguage,
            TranslatorId = dto.TranslatorId,
            GlossaryId = dto.GlossaryId,
            StyleRuleId = dto.StyleRuleId,
            ProxyRuleId = dto.ProxyRuleId,
            RouteTokenId = tokenId,
            CatalogEntryId = dto.CatalogEntryId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        return Results.Created($"/admin/mcp/servers/{dto.Id}",
            new CreateServerResponse(ToDto(await db.McpServers.AsNoTracking().FirstAsync(s => s.Id == dto.Id, ct)),
                plaintextToken));
    }

    private static async Task<IResult> ListServers(LlmTransDbContext db, CancellationToken ct) =>
        Results.Ok(await db.McpServers.AsNoTracking().Select(ToSelectDto).ToListAsync(ct));

    private static async Task<IResult> GetServer(string id, LlmTransDbContext db, CancellationToken ct)
    {
        var s = await db.McpServers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return s is null ? Results.NotFound() : Results.Ok(ToDto(s));
    }

    private static async Task<IResult> DeleteServer(string id, LlmTransDbContext db, CancellationToken ct)
    {
        var s = await db.McpServers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Results.NotFound();
        var tok = await db.RouteTokens.FirstOrDefaultAsync(t => t.Id == s.RouteTokenId, ct);
        if (tok is not null) tok.RevokedAt = DateTimeOffset.UtcNow;
        s.DisabledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    public sealed record SnippetQuery(string? Client);

    private static async Task<IResult> GetSnippet(string id, string? client, LlmTransDbContext db, HttpContext ctx, CancellationToken ct)
    {
        var s = await db.McpServers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Results.NotFound();

        var tok = await db.RouteTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == s.RouteTokenId, ct);
        if (tok is null) return Results.NotFound();

        // Public base URL — use configured value or the request's scheme+host.
        var publicBase = ctx.RequestServices.GetRequiredService<IConfiguration>()
                            .GetValue<string>("PublicBaseUrl")
                         ?? $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}";

        // We don't store the plaintext token — snippet uses a placeholder the user replaces.
        const string PlaceholderToken = "<your-route-token>";
        var clientKind = (client ?? "raw").ToLowerInvariant();

        var snippet = McpSnippetBuilder.Build(s, publicBase, PlaceholderToken, clientKind);
        return Results.Ok(new { client = clientKind, snippet });
    }

    private static async Task<IResult> ListCatalog(LlmTransDbContext db, CancellationToken ct) =>
        Results.Ok(await db.McpCatalog.AsNoTracking()
            .OrderBy(c => c.Slug)
            .Select(c => new CatalogDto(
                c.Id, c.Slug, c.DisplayName, c.Description,
                c.Transport, c.UpstreamUrl, c.UpstreamCommandHint,
                c.DocsUrl, c.IconUrl, c.Publisher, c.Verified))
            .ToListAsync(ct));

    private static async Task<IResult> GetCatalogEntry(string slug, LlmTransDbContext db, CancellationToken ct)
    {
        var e = await db.McpCatalog.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug, ct);
        return e is null ? Results.NotFound()
            : Results.Ok(new CatalogDto(
                e.Id, e.Slug, e.DisplayName, e.Description, e.Transport,
                e.UpstreamUrl, e.UpstreamCommandHint, e.DocsUrl, e.IconUrl,
                e.Publisher, e.Verified));
    }

    private static readonly System.Linq.Expressions.Expression<Func<McpServerEntity, ServerDto>> ToSelectDto =
        s => new ServerDto(
            s.Id, s.TenantId, s.Name, s.Transport, s.RemoteUpstreamUrl,
            s.UserLanguage, s.LlmLanguage,
            s.TranslatorId, s.GlossaryId, s.StyleRuleId, s.ProxyRuleId,
            s.CatalogEntryId, s.CreatedAt, s.DisabledAt);

    private static ServerDto ToDto(McpServerEntity s) => new(
        s.Id, s.TenantId, s.Name, s.Transport, s.RemoteUpstreamUrl,
        s.UserLanguage, s.LlmLanguage,
        s.TranslatorId, s.GlossaryId, s.StyleRuleId, s.ProxyRuleId,
        s.CatalogEntryId, s.CreatedAt, s.DisabledAt);
}
