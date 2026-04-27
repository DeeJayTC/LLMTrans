using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Model;
using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Core.Plugins;
using AdaptiveApi.Core.Proxy;
using AdaptiveApi.Core.Routing;
using AdaptiveApi.Core.Rules;
using AdaptiveApi.Core.Streaming;
using AdaptiveApi.Plugins.SDK.Hooks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AdaptiveApi.Providers.Mcp;

/// Flow A (§2.5.1): route-proxy for remote MCP upstreams.
/// Forwards JSON-RPC POST bodies to the configured upstream, translates
/// translatable string leaves based on method + direction. Upstream auth
/// headers pass through byte-identical and are never persisted.
public sealed class McpRouteAdapter : IProviderAdapter
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ITranslatorRouter _translatorRouter;
    private readonly IRuleResolver _ruleResolver;
    private readonly IAuditSink _audit;
    private readonly IPluginHookDispatcher _hooks;
    private readonly ILogger<McpRouteAdapter> _log;

    public McpRouteAdapter(
        IHttpClientFactory httpFactory,
        ITranslatorRouter translatorRouter,
        IRuleResolver ruleResolver,
        IAuditSink audit,
        IPluginHookDispatcher hooks,
        ILogger<McpRouteAdapter> log)
    {
        _httpFactory = httpFactory;
        _translatorRouter = translatorRouter;
        _ruleResolver = ruleResolver;
        _audit = audit;
        _hooks = hooks;
        _log = log;
    }

    public string ProviderId => "mcp";
    public RouteKind RouteKind => RouteKind.Mcp;

    public bool Matches(HttpRequest request) => request.Method == HttpMethods.Post;

    public async Task HandleAsync(HttpContext context, RouteConfig route, CancellationToken ct)
    {
        if (route.UpstreamBaseUrl.Scheme == "about")
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                jsonrpc = "2.0",
                error = new
                {
                    code = -32601,
                    message = "mcp server is stdio-local; use the /mcp-translate endpoint via the local bridge",
                },
                id = (object?)null,
            }, ct);
            return;
        }

        var sw = Stopwatch.StartNew();
        var inbound = context.Request;
        var (bodyBytes, _) = await ReadBodyAsync(inbound, ct);

        var effective = route; // no header overrides for MCP; server-lang is fixed via admin
        var translationActive = effective.Direction != DirectionMode.Off
                                && effective.UserLanguage.Value != effective.LlmLanguage.Value;

        var rules = translationActive
            ? await _ruleResolver.ResolveAsync(effective, ct)
            : EmptyRules(effective);

        var translator = _translatorRouter.Resolve(effective);

        var hookCtx = new PipelineHookContext(
            HttpContext: context,
            RouteId: effective.RouteId,
            TenantId: effective.TenantId,
            ProviderId: ProviderId,
            UserLanguage: effective.UserLanguage.Value,
            LlmLanguage: effective.LlmLanguage.Value,
            Direction: effective.Direction.ToString(),
            Properties: new Dictionary<string, object?>(StringComparer.Ordinal));

        // Hook 1/6 — before request translation
        var hookResult = await _hooks.RunBeforeRequestTranslationAsync(hookCtx, bodyBytes, ct);
        if (await ApplyShortCircuitAsync(context, hookResult, ct)) return;
        if (hookResult.ModifiedBody is { } prereqBody) bodyBytes = prereqBody;

        // Translate the inbound JSON-RPC message (client → upstream). User writes in their language.
        var forwardBody = bodyBytes;
        var requestChars = 0;
        if (translationActive && bodyBytes.Length > 0)
        {
            (forwardBody, requestChars) = await TranslateMessageBytesAsync(
                bodyBytes, McpDirection.ClientToServer, translator,
                source: effective.UserLanguage, target: effective.LlmLanguage,
                rules.ToolArgsDenylist, ct);
        }

        // Hook 2/6 — after request translation
        hookResult = await _hooks.RunAfterRequestTranslationAsync(hookCtx, forwardBody, ct);
        if (await ApplyShortCircuitAsync(context, hookResult, ct)) return;
        if (hookResult.ModifiedBody is { } postreqBody) forwardBody = postreqBody;

        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, effective.UpstreamBaseUrl);
        upstreamReq.Content = new ByteArrayContent(forwardBody);
        upstreamReq.Content.Headers.TryAddWithoutValidation(
            "Content-Type", inbound.ContentType ?? "application/json");
        HeaderForwarder.CopyInboundToUpstream(inbound.Headers, upstreamReq, upstreamReq.Content);

        // Hook 3/6 — before AI call
        hookResult = await _hooks.RunBeforeAiAsync(hookCtx, upstreamReq, ct);
        if (await ApplyShortCircuitAsync(context, hookResult, ct)) return;

        var http = _httpFactory.CreateClient("mcp-upstream");
        using var upstreamResp = await http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);

        // Hook 4/6 — after AI call
        hookResult = await _hooks.RunAfterAiAsync(hookCtx, upstreamResp, ct);
        if (await ApplyShortCircuitAsync(context, hookResult, ct)) return;

        context.Response.StatusCode = (int)upstreamResp.StatusCode;
        context.Response.Headers.Remove("transfer-encoding");

        var contentType = upstreamResp.Content.Headers.ContentType?.MediaType ?? "application/json";
        HeaderForwarder.CopyUpstreamToClient(upstreamResp, context.Response.Headers);

        var responseTranslationActive = translationActive && upstreamResp.IsSuccessStatusCode;
        var responseChars = 0;

        if (contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) && responseTranslationActive)
        {
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers.Remove("Content-Length");
            await using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);

            await foreach (var ev in SseParser.ReadAsync(upstreamStream, ct))
            {
                var translated = await TranslateEventDataAsync(
                    ev.Data, McpDirection.ServerToClient, translator,
                    source: effective.LlmLanguage, target: effective.UserLanguage,
                    rules.ToolArgsDenylist, ct);
                responseChars += translated.Chars;
                await context.Response.Body.WriteAsync(
                    SseParser.SerializeEvent(new SseEvent(ev.Event, translated.Data)), ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        else if (responseTranslationActive)
        {
            var respBytes = await upstreamResp.Content.ReadAsByteArrayAsync(ct);

            // Hook 5/6 — before response translation
            hookResult = await _hooks.RunBeforeResponseTranslationAsync(hookCtx, respBytes, ct);
            if (await ApplyShortCircuitAsync(context, hookResult, ct)) return;
            if (hookResult.ModifiedBody is { } preRespBody) respBytes = preRespBody;

            var (translatedResp, chars) = await TranslateMessageBytesAsync(
                respBytes, McpDirection.ServerToClient, translator,
                source: effective.LlmLanguage, target: effective.UserLanguage,
                rules.ToolArgsDenylist, ct);
            responseChars = chars;

            // Hook 6/6 — after response translation
            hookResult = await _hooks.RunAfterResponseTranslationAsync(hookCtx, translatedResp, ct);
            if (await ApplyShortCircuitAsync(context, hookResult, ct)) return;
            if (hookResult.ModifiedBody is { } postRespBody) translatedResp = postRespBody;

            context.Response.Headers["Content-Length"] = translatedResp.Length.ToString();
            await context.Response.Body.WriteAsync(translatedResp, ct);
        }
        else
        {
            await using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);
            await upstreamStream.CopyToAsync(context.Response.Body, 8192, ct);
        }

        sw.Stop();
        await _audit.RecordAsync(new AuditRecord(
            TenantId: effective.TenantId,
            RouteId: effective.RouteId,
            Method: inbound.Method,
            Path: inbound.Path.Value ?? string.Empty,
            Status: (int)upstreamResp.StatusCode,
            UserLanguage: effective.UserLanguage.Value,
            LlmLanguage: effective.LlmLanguage.Value,
            Direction: effective.Direction.ToString(),
            TranslatorId: effective.TranslatorId,
            GlossaryId: effective.GlossaryId,
            RequestStyleRuleId: effective.RequestStyleRuleId,
            ResponseStyleRuleId: effective.ResponseStyleRuleId,
            RequestChars: requestChars,
            ResponseChars: responseChars,
            IntegrityFailures: 0,
            DurationMs: sw.ElapsedMilliseconds), ct);
    }

    private static async Task<(byte[] Bytes, int Chars)> TranslateMessageBytesAsync(
        byte[] bytes, McpDirection direction, ITranslator translator,
        LanguageCode source, LanguageCode target,
        ToolArgsDenylist denylist, CancellationToken ct)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(bytes); }
        catch (JsonException) { return (bytes, 0); }
        if (root is null) return (bytes, 0);

        var n = await JsonRpcMessageTranslator.TranslateAsync(
            root, direction, translator, source, target, denylist, ct);
        if (n == 0) return (bytes, 0);

        var outBytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        return (outBytes, bytes.Length);
    }

    private static async Task<(string Data, int Chars)> TranslateEventDataAsync(
        string data, McpDirection direction, ITranslator translator,
        LanguageCode source, LanguageCode target,
        ToolArgsDenylist denylist, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(data) || data == "[DONE]") return (data, 0);

        JsonNode? root;
        try { root = JsonNode.Parse(data); }
        catch (JsonException) { return (data, 0); }
        if (root is null) return (data, 0);

        var n = await JsonRpcMessageTranslator.TranslateAsync(
            root, direction, translator, source, target, denylist, ct);
        return n == 0 ? (data, 0) : (root.ToJsonString(), data.Length);
    }

    private static ResolvedRules EmptyRules(RouteConfig route) => new(
        Glossary: Array.Empty<GlossaryTerm>(),
        DeeplGlossaryId: null,
        RequestStyle: StyleBinding.Empty,
        ResponseStyle: StyleBinding.Empty,
        RequestAllowlist: AllowlistCatalog.Request(route.Kind),
        ResponseAllowlist: AllowlistCatalog.Response(route.Kind),
        ToolArgsDenylist: ToolArgsDenylist.Default,
        Formality: Formality.Default);

    private static async Task<bool> ApplyShortCircuitAsync(HttpContext context, HookResult result, CancellationToken ct)
    {
        if (result.ContinuePipeline) return false;
        context.Response.StatusCode = result.ShortCircuitStatus ?? StatusCodes.Status403Forbidden;
        if (!string.IsNullOrEmpty(result.ShortCircuitContentType))
            context.Response.Headers["Content-Type"] = result.ShortCircuitContentType;
        if (result.ShortCircuitBody is { Length: > 0 } body)
            await context.Response.Body.WriteAsync(body, ct);
        return true;
    }

    private static async Task<(byte[] Body, bool Streaming)> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength is null or 0 && !req.Headers.ContainsKey("Transfer-Encoding"))
            return (Array.Empty<byte>(), false);
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ct);
        return (ms.ToArray(), false);
    }
}
