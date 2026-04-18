using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Model;
using AdaptiveApi.Core.Routing;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Infrastructure.Routing;

public sealed class DbRouteResolver : IRouteResolver
{
    private readonly AdaptiveApiDbContext _db;

    public DbRouteResolver(AdaptiveApiDbContext db) => _db = db;

    public async Task<RouteConfig?> ResolveByTokenAsync(string token, CancellationToken ct)
    {
        var prefix = RouteToken.PrefixOf(token);
        var hash = RouteToken.HashForStorage(token);

        var row = await _db.RouteTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Prefix == prefix && t.Hash == hash && t.RevokedAt == null, ct);

        if (row is null) return null;

        return row.Scope switch
        {
            "route" => await ResolveLlmRouteAsync(row, ct),
            "mcp-server" => await ResolveMcpServerAsync(row, ct),
            _ => null,
        };
    }

    private async Task<RouteConfig?> ResolveLlmRouteAsync(RouteTokenEntity token, CancellationToken ct)
    {
        var route = await _db.Routes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == token.RouteId, ct);
        if (route is null) return null;

        return new RouteConfig(
            RouteId: route.Id,
            TenantId: route.TenantId,
            Kind: Enum.Parse<RouteKind>(route.Kind),
            UpstreamBaseUrl: new Uri(route.UpstreamBaseUrl),
            UserLanguage: new LanguageCode(route.UserLanguage),
            LlmLanguage: new LanguageCode(route.LlmLanguage),
            Direction: Enum.Parse<DirectionMode>(route.Direction),
            TranslatorId: route.TranslatorId,
            GlossaryId: route.GlossaryId,
            RequestStyleRuleId: route.RequestStyleRuleId,
            ResponseStyleRuleId: route.ResponseStyleRuleId,
            ProxyRuleId: route.ProxyRuleId,
            ConfigJson: route.ConfigJson);
    }

    private async Task<RouteConfig?> ResolveMcpServerAsync(RouteTokenEntity token, CancellationToken ct)
    {
        var mcp = await _db.McpServers.AsNoTracking().FirstOrDefaultAsync(m => m.Id == token.RouteId, ct);
        if (mcp is null || mcp.DisabledAt is not null) return null;

        // stdio-local servers don't have an upstream URL (the bridge translates via Flow B).
        // Routing the Flow A proxy to such a server is an error — caller handles that.
        var upstream = string.IsNullOrEmpty(mcp.RemoteUpstreamUrl)
            ? new Uri("about:blank")
            : new Uri(mcp.RemoteUpstreamUrl);

        return new RouteConfig(
            RouteId: mcp.Id,
            TenantId: mcp.TenantId,
            Kind: RouteKind.Mcp,
            UpstreamBaseUrl: upstream,
            UserLanguage: new LanguageCode(mcp.UserLanguage),
            LlmLanguage: new LanguageCode(mcp.LlmLanguage),
            Direction: DirectionMode.Bidirectional,
            TranslatorId: mcp.TranslatorId,
            GlossaryId: mcp.GlossaryId,
            RequestStyleRuleId: mcp.StyleRuleId,
            ResponseStyleRuleId: mcp.StyleRuleId,
            ProxyRuleId: mcp.ProxyRuleId);
    }
}
