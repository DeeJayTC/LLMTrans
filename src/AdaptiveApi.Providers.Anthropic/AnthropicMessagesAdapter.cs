using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Model;
using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Core.Plugins;
using AdaptiveApi.Core.Proxy;
using AdaptiveApi.Core.Routing;
using AdaptiveApi.Core.Rules;
using AdaptiveApi.Plugins.SDK.Hooks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AdaptiveApi.Providers.Anthropic;

public sealed class AnthropicMessagesAdapter : IProviderAdapter
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ITranslatorRouter _translatorRouter;
    private readonly IRuleResolver _ruleResolver;
    private readonly IAuditSink _audit;
    private readonly IPiiRedactor _piiRedactor;
    private readonly ITranslationCache _translationCache;
    private readonly IPluginHookDispatcher _hooks;
    private readonly ILogger<AnthropicMessagesAdapter> _log;

    public AnthropicMessagesAdapter(
        IHttpClientFactory httpFactory,
        ITranslatorRouter translatorRouter,
        IRuleResolver ruleResolver,
        IAuditSink audit,
        IPiiRedactor piiRedactor,
        ITranslationCache translationCache,
        IPluginHookDispatcher hooks,
        ILogger<AnthropicMessagesAdapter> log)
    {
        _httpFactory = httpFactory;
        _translatorRouter = translatorRouter;
        _ruleResolver = ruleResolver;
        _audit = audit;
        _piiRedactor = piiRedactor;
        _translationCache = translationCache;
        _hooks = hooks;
        _log = log;
    }

    public string ProviderId => "anthropic.messages";
    public RouteKind RouteKind => RouteKind.AnthropicMessages;

    public bool Matches(HttpRequest request) =>
        request.Method == HttpMethods.Post
        && (request.Path.Value ?? string.Empty).EndsWith("/messages", StringComparison.Ordinal);

    public async Task HandleAsync(HttpContext context, RouteConfig route, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var inbound = context.Request;
        var upstreamPath = ExtractUpstreamPath(inbound.Path.Value!);
        var upstreamUri = new Uri(route.UpstreamBaseUrl, upstreamPath + inbound.QueryString);

        var (bodyBytes, streaming) = await ReadBodyAsync(inbound, ct);

        var effective = ApplyHeaderOverrides(route, inbound);
        var translationActive = effective.Direction != DirectionMode.Off
                                && effective.UserLanguage.Value != effective.LlmLanguage.Value;

        var rules = translationActive
            ? await _ruleResolver.ResolveAsync(effective, ct)
            : EmptyRules(effective);

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

        var forwardBody = bodyBytes;
        var requestChars = 0;
        if (translationActive && bodyBytes.Length > 0 && !streaming
            && effective.Direction is DirectionMode.Bidirectional or DirectionMode.RequestOnly)
        {
            (forwardBody, requestChars) = await TranslateAsync(bodyBytes, rules.RequestAllowlist,
                source: effective.UserLanguage, target: effective.LlmLanguage, effective, rules, rules.RequestStyle, ct);
        }

        // Hook 2/6 — after request translation
        hookResult = await _hooks.RunAfterRequestTranslationAsync(hookCtx, forwardBody, ct);
        if (await ApplyShortCircuitAsync(context, hookResult, ct)) return;
        if (hookResult.ModifiedBody is { } postreqBody) forwardBody = postreqBody;

        using var upstreamReq = new HttpRequestMessage(new HttpMethod(inbound.Method), upstreamUri);
        if (forwardBody.Length > 0)
        {
            upstreamReq.Content = new ByteArrayContent(forwardBody);
            upstreamReq.Content.Headers.TryAddWithoutValidation(
                "Content-Type", inbound.ContentType ?? "application/json");
        }
        HeaderForwarder.CopyInboundToUpstream(inbound.Headers, upstreamReq, upstreamReq.Content);

        // Hook 3/6 — before AI call
        hookResult = await _hooks.RunBeforeAiAsync(hookCtx, upstreamReq, ct);
        if (await ApplyShortCircuitAsync(context, hookResult, ct)) return;

        var http = _httpFactory.CreateClient("anthropic-upstream");
        using var upstreamResp = await http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);

        // Hook 4/6 — after AI call
        hookResult = await _hooks.RunAfterAiAsync(hookCtx, upstreamResp, ct);
        if (await ApplyShortCircuitAsync(context, hookResult, ct)) return;

        context.Response.StatusCode = (int)upstreamResp.StatusCode;
        context.Response.Headers.Remove("transfer-encoding");

        var respTranslate = translationActive
            && upstreamResp.IsSuccessStatusCode
            && !streaming
            && effective.Direction is DirectionMode.Bidirectional or DirectionMode.ResponseOnly;

        var responseChars = 0;

        if (!respTranslate)
        {
            HeaderForwarder.CopyUpstreamToClient(upstreamResp, context.Response.Headers);
            await using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);
            await upstreamStream.CopyToAsync(context.Response.Body, 8192, ct);
        }
        else
        {
            var respBytes = await upstreamResp.Content.ReadAsByteArrayAsync(ct);

            // Hook 5/6 — before response translation
            hookResult = await _hooks.RunBeforeResponseTranslationAsync(hookCtx, respBytes, ct);
            if (await ApplyShortCircuitAsync(context, hookResult, ct)) return;
            if (hookResult.ModifiedBody is { } preRespBody) respBytes = preRespBody;

            var (translatedResp, chars) = await TranslateAsync(respBytes, rules.ResponseAllowlist,
                source: effective.LlmLanguage, target: effective.UserLanguage, effective, rules, rules.ResponseStyle, ct);
            responseChars = chars;

            // Hook 6/6 — after response translation
            hookResult = await _hooks.RunAfterResponseTranslationAsync(hookCtx, translatedResp, ct);
            if (await ApplyShortCircuitAsync(context, hookResult, ct)) return;
            if (hookResult.ModifiedBody is { } postRespBody) translatedResp = postRespBody;

            HeaderForwarder.CopyUpstreamToClient(upstreamResp, context.Response.Headers);
            context.Response.Headers["Content-Length"] = translatedResp.Length.ToString();
            await context.Response.Body.WriteAsync(translatedResp, ct);
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

    private async Task<(byte[] Bytes, int Chars)> TranslateAsync(
        byte[] bytes, Allowlist allowlist, LanguageCode source, LanguageCode target,
        RouteConfig route, ResolvedRules rules, StyleBinding style, CancellationToken ct)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(bytes); }
        catch (JsonException) { return (bytes, 0); }
        if (root is null) return (bytes, 0);

        var translator = _translatorRouter.Resolve(route);
        var pipeline = new TranslationPipeline(translator, _piiRedactor, _translationCache);
        await pipeline.TranslateInPlaceAsync(root, allowlist, new PipelineOptions
        {
            Source = source,
            Target = target,
            GlossaryId = rules.DeeplGlossaryId,
            StyleRuleId = style.DeeplStyleId,
            CustomInstructions = rules.SystemInstructionsFor(style, source.Value, target.Value),
            DoNotTranslateTerms = rules.DoNotTranslateFor(source.Value, target.Value),
            Formality = rules.Formality,
            RedactPii = rules.RedactPii,
            PiiDetectors = rules.PiiDetectors,
            Context = rules.SystemContext,
            TranslationMemoryId = route.TranslationMemoryId,
            TranslationMemoryThreshold = route.TranslationMemoryThreshold,
        }, ct);

        await ToolCallTranslator.TranslateAsync(
            root, ToolCallTranslator.RootShape.AnthropicMessages,
            translator, source, target, rules.ToolArgsDenylist, ct);

        var outBytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        return (outBytes, bytes.Length);
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

    private static RouteConfig ApplyHeaderOverrides(RouteConfig route, HttpRequest req)
    {
        var userLang = req.Headers.TryGetValue("X-AdaptiveApi-Target-Lang", out var tl)
            ? new LanguageCode(tl.ToString().Trim().ToLowerInvariant()) : route.UserLanguage;
        var llmLang = req.Headers.TryGetValue("X-AdaptiveApi-Source-Lang", out var sl)
            ? new LanguageCode(sl.ToString().Trim().ToLowerInvariant()) : route.LlmLanguage;
        var mode = req.Headers.TryGetValue("X-AdaptiveApi-Mode", out var m)
            ? Enum.TryParse<DirectionMode>(m.ToString(), true, out var parsed) ? parsed : route.Direction
            : route.Direction;
        var tmId = req.Headers.TryGetValue("X-AdaptiveApi-Translation-Memory", out var tm)
            ? tm.ToString() : route.TranslationMemoryId;
        int? tmThreshold = req.Headers.TryGetValue("X-AdaptiveApi-Tm-Threshold", out var tmt)
                           && int.TryParse(tmt.ToString(), out var parsedThreshold)
            ? parsedThreshold : route.TranslationMemoryThreshold;
        if (tl.Count > 0 && mode == DirectionMode.Off) mode = DirectionMode.Bidirectional;
        return route with
        {
            UserLanguage = userLang,
            LlmLanguage = llmLang,
            Direction = mode,
            TranslationMemoryId = string.IsNullOrEmpty(tmId) ? null : tmId,
            TranslationMemoryThreshold = tmThreshold,
        };
    }

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

    private static async Task<(byte[], bool)> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength is null or 0 && !req.Headers.ContainsKey("Transfer-Encoding"))
            return (Array.Empty<byte>(), false);

        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        var stream = false;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            stream = doc.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { }

        return (bytes, stream);
    }

    private static string ExtractUpstreamPath(string inboundPath)
    {
        var segments = inboundPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || segments[0] != "anthropic" || segments[1] != "v1")
            throw new InvalidOperationException($"unexpected inbound path shape: {inboundPath}");
        return "/v1/" + string.Join('/', segments[3..]);
    }
}
