using LlmTrans.Core.Abstractions;
using LlmTrans.Core.Routing;
using LlmTrans.Providers.Anthropic;
using LlmTrans.Providers.Generic;
using LlmTrans.Providers.Mcp;
using LlmTrans.Providers.OpenAI;

namespace LlmTrans.Api.Proxy;

public static class ProxyEndpoints
{
    public static void Map(WebApplication app)
    {
        var openai = app.MapGroup("/v1/{routeToken}");
        openai.MapPost("/chat/completions", HandleOpenAi);
        openai.MapPost("/responses", HandleOpenAi);
        openai.MapPost("/embeddings", HandleOpenAi);

        var anthropic = app.MapGroup("/anthropic/v1/{routeToken}");
        anthropic.MapPost("/messages", HandleAnthropic);

        app.MapPost("/mcp/{routeToken}", HandleMcp);

        app.MapMethods("/generic/{routeToken}/{**catchall}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE" }, HandleGeneric);
        app.MapMethods("/generic/{routeToken}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE" }, HandleGeneric);
    }

    private static async Task HandleOpenAi(
        HttpContext ctx, string routeToken,
        IRouteResolver resolver, ITenantContext tenantCtx,
        OpenAiChatAdapter adapter, CancellationToken ct)
        => await DispatchAsync(ctx, routeToken, RouteKind.OpenAiChat, resolver, tenantCtx, adapter, ct);

    private static async Task HandleAnthropic(
        HttpContext ctx, string routeToken,
        IRouteResolver resolver, ITenantContext tenantCtx,
        AnthropicMessagesAdapter adapter, CancellationToken ct)
        => await DispatchAsync(ctx, routeToken, RouteKind.AnthropicMessages, resolver, tenantCtx, adapter, ct);

    private static async Task HandleMcp(
        HttpContext ctx, string routeToken,
        IRouteResolver resolver, ITenantContext tenantCtx,
        McpRouteAdapter adapter, CancellationToken ct)
        => await DispatchAsync(ctx, routeToken, RouteKind.Mcp, resolver, tenantCtx, adapter, ct);

    private static async Task HandleGeneric(
        HttpContext ctx, string routeToken,
        IRouteResolver resolver, ITenantContext tenantCtx,
        GenericAdapter adapter, CancellationToken ct)
        => await DispatchAsync(ctx, routeToken, RouteKind.Generic, resolver, tenantCtx, adapter, ct);

    private static async Task DispatchAsync(
        HttpContext ctx, string routeToken, RouteKind expected,
        IRouteResolver resolver, ITenantContext tenantCtx,
        IProviderAdapter adapter, CancellationToken ct)
    {
        var route = await resolver.ResolveByTokenAsync(routeToken, ct);
        if (route is null || route.Kind != expected)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = new { type = "invalid_route_token", message = "route token not recognized for this endpoint" }
            }, ct);
            return;
        }

        tenantCtx.Bind(route.TenantId, route.RouteId);

        if (!adapter.Matches(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await adapter.HandleAsync(ctx, route, ct);
    }
}
