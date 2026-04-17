using System.Diagnostics;
using System.Text.Json.Nodes;
using LlmTrans.Core.Abstractions;
using LlmTrans.Core.Pipeline;
using LlmTrans.Core.Rules;
using LlmTrans.Providers.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LlmTrans.Mcp.TranslateApi;

/// Flow B (§2.5.8): stateless translation API called by the local mcp-bridge.
/// `POST /mcp-translate/{routeToken}` — the bridge sends each JSON-RPC message it
/// observes (in either direction) and we return the translated message.
/// No upstream credentials ever touch this endpoint — only message bodies.
public static class TranslateEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/mcp-translate/{routeToken}", Handle);
    }

    public sealed record Request(string Direction, JsonObject Message);

    public sealed record Diagnostics(int Chars, bool CacheHit, string Translator);

    public sealed record Response(JsonObject Message, Diagnostics Diagnostics);

    private static async Task<IResult> Handle(
        string routeToken,
        Request body,
        IRouteResolver routeResolver,
        IRuleResolver ruleResolver,
        ITranslatorRouter translatorRouter,
        IAuditSink audit,
        HttpContext ctx,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var route = await routeResolver.ResolveByTokenAsync(routeToken, ct);
        if (route is null || route.Kind != Core.Routing.RouteKind.Mcp)
            return Results.Json(new { error = new { type = "invalid_route_token" } }, statusCode: 401);

        var direction = body.Direction switch
        {
            "client-to-server" or "request" => McpDirection.ClientToServer,
            "server-to-client" or "response" => McpDirection.ServerToClient,
            _ => (McpDirection?)null,
        };
        if (direction is null)
            return Results.BadRequest(new { error = new { type = "invalid_direction", got = body.Direction } });

        var translator = translatorRouter.Resolve(route);
        var rules = await ruleResolver.ResolveAsync(route, ct);

        // For the client-to-server direction translate USER → LLM; reverse for server-to-client.
        var (source, target) = direction == McpDirection.ClientToServer
            ? (route.UserLanguage, route.LlmLanguage)
            : (route.LlmLanguage, route.UserLanguage);

        // Mutate in place so the caller always gets a well-formed JSON-RPC message back.
        var message = body.Message;
        var translated = await JsonRpcMessageTranslator.TranslateAsync(
            message, direction.Value, translator, source, target,
            rules.ToolArgsDenylist, ct);

        sw.Stop();
        await audit.RecordAsync(new AuditRecord(
            TenantId: route.TenantId,
            RouteId: route.RouteId,
            Method: "POST",
            Path: ctx.Request.Path.Value ?? string.Empty,
            Status: 200,
            UserLanguage: route.UserLanguage.Value,
            LlmLanguage: route.LlmLanguage.Value,
            Direction: direction.Value.ToString(),
            TranslatorId: route.TranslatorId,
            GlossaryId: route.GlossaryId,
            RequestStyleRuleId: route.RequestStyleRuleId,
            ResponseStyleRuleId: route.ResponseStyleRuleId,
            RequestChars: 0,
            ResponseChars: EstimateChars(message),
            IntegrityFailures: 0,
            DurationMs: sw.ElapsedMilliseconds), ct);

        return Results.Ok(new Response(
            Message: message,
            Diagnostics: new Diagnostics(
                Chars: EstimateChars(message),
                CacheHit: false,
                Translator: translator.TranslatorId)));
    }

    private static int EstimateChars(JsonObject msg) =>
        msg.ToJsonString().Length;
}
