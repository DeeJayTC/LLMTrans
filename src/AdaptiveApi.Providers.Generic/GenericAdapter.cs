using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Model;
using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Core.Proxy;
using AdaptiveApi.Core.Routing;
using AdaptiveApi.Core.Rules;
using AdaptiveApi.Core.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AdaptiveApi.Providers.Generic;

public sealed class GenericAdapter : IProviderAdapter
{
    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ITranslatorRouter _translatorRouter;
    private readonly IRuleResolver _ruleResolver;
    private readonly IAuditSink _audit;
    private readonly IPiiRedactor _piiRedactor;
    private readonly ITranslationCache _translationCache;
    private readonly ILogger<GenericAdapter> _log;

    public GenericAdapter(
        IHttpClientFactory httpFactory,
        ITranslatorRouter translatorRouter,
        IRuleResolver ruleResolver,
        IAuditSink audit,
        IPiiRedactor piiRedactor,
        ITranslationCache translationCache,
        ILogger<GenericAdapter> log)
    {
        _httpFactory = httpFactory;
        _translatorRouter = translatorRouter;
        _ruleResolver = ruleResolver;
        _audit = audit;
        _piiRedactor = piiRedactor;
        _translationCache = translationCache;
        _log = log;
    }

    public string ProviderId => "generic";
    public RouteKind RouteKind => RouteKind.Generic;

    public bool Matches(HttpRequest request) => true; // scoped by endpoint mapping

    public async Task HandleAsync(HttpContext context, RouteConfig route, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var inbound = context.Request;

        GenericRouteConfig? config = null;
        if (!string.IsNullOrWhiteSpace(route.ConfigJson))
        {
            try { config = JsonSerializer.Deserialize<GenericRouteConfig>(route.ConfigJson, ConfigJson); }
            catch (JsonException ex)
            {
                _log.LogError(ex, "invalid generic route config for {RouteId}", route.RouteId);
            }
        }

        if (config is null || string.IsNullOrEmpty(config.Upstream.UrlTemplate))
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { type = "route_misconfigured", message = "generic route has no upstream URL" },
            }, ct);
            return;
        }

        var tail = ExtractTailPath(inbound.Path.Value!);
        var upstreamUri = BuildUpstreamUri(config.Upstream.UrlTemplate, tail, inbound.QueryString);

        var (bodyBytes, _) = await ReadBodyAsync(inbound, ct);

        var direction = ParseDirection(config.Direction);
        var translationActive = direction != DirectionMode.Off
                                && route.UserLanguage.Value != route.LlmLanguage.Value;

        var rules = translationActive
            ? await _ruleResolver.ResolveAsync(route, ct)
            : EmptyRules(route);

        var translator = _translatorRouter.Resolve(route);

        var requestAllowlist = JsonPathConverter.ToAllowlist(config.Request.TranslateJsonPaths);
        var responseFinalAllowlist = JsonPathConverter.ToAllowlist(config.Response.FinalPaths);

        var forwardBody = bodyBytes;
        var requestChars = 0;
        if (translationActive && bodyBytes.Length > 0
            && direction is DirectionMode.Bidirectional or DirectionMode.RequestOnly
            && config.Request.TranslateJsonPaths.Count > 0)
        {
            (forwardBody, requestChars) = await TranslateJsonAsync(
                bodyBytes, requestAllowlist,
                source: route.UserLanguage, target: route.LlmLanguage,
                translator, rules, rules.RequestStyle, ct);
        }

        var method = string.IsNullOrEmpty(config.Upstream.Method)
            ? new HttpMethod(inbound.Method)
            : new HttpMethod(config.Upstream.Method!);

        using var upstreamReq = new HttpRequestMessage(method, upstreamUri);
        if (forwardBody.Length > 0)
        {
            upstreamReq.Content = new ByteArrayContent(forwardBody);
            upstreamReq.Content.Headers.TryAddWithoutValidation(
                "Content-Type", inbound.ContentType ?? "application/json");
        }

        HeaderForwarder.CopyInboundToUpstream(inbound.Headers, upstreamReq, upstreamReq.Content);
        if (config.Upstream.AdditionalHeaders is { Count: > 0 })
        {
            foreach (var (k, v) in config.Upstream.AdditionalHeaders)
            {
                if (!upstreamReq.Headers.TryAddWithoutValidation(k, v))
                    upstreamReq.Content?.Headers.TryAddWithoutValidation(k, v);
            }
        }

        var http = _httpFactory.CreateClient("generic-upstream");
        using var upstreamResp = await http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);

        context.Response.StatusCode = (int)upstreamResp.StatusCode;
        context.Response.Headers.Remove("transfer-encoding");
        HeaderForwarder.CopyUpstreamToClient(upstreamResp, context.Response.Headers);

        var respTranslate = translationActive
            && upstreamResp.IsSuccessStatusCode
            && direction is DirectionMode.Bidirectional or DirectionMode.ResponseOnly;

        var responseChars = 0;
        var integrityFailures = 0;

        if (respTranslate && config.Response.Streaming == "sse")
        {
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers.Remove("Content-Length");
            await using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);

            var eventAllowlist = !string.IsNullOrEmpty(config.Response.EventPath)
                ? JsonPathConverter.ToAllowlist(new[] { config.Response.EventPath! })
                : responseFinalAllowlist;

            await foreach (var ev in SseParser.ReadAsync(upstreamStream, ct))
            {
                var (translatedData, chars) = await TranslateEventDataAsync(
                    ev.Data, eventAllowlist,
                    source: route.LlmLanguage, target: route.UserLanguage,
                    translator, rules, rules.ResponseStyle, ct);
                responseChars += chars;
                await context.Response.Body.WriteAsync(
                    SseParser.SerializeEvent(new SseEvent(ev.Event, translatedData)), ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        else if (respTranslate && config.Response.FinalPaths.Count > 0)
        {
            var respBytes = await upstreamResp.Content.ReadAsByteArrayAsync(ct);
            var (translatedResp, chars) = await TranslateJsonAsync(
                respBytes, responseFinalAllowlist,
                source: route.LlmLanguage, target: route.UserLanguage,
                translator, rules, rules.ResponseStyle, ct);
            responseChars = chars;

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
            TenantId: route.TenantId,
            RouteId: route.RouteId,
            Method: inbound.Method,
            Path: inbound.Path.Value ?? string.Empty,
            Status: (int)upstreamResp.StatusCode,
            UserLanguage: route.UserLanguage.Value,
            LlmLanguage: route.LlmLanguage.Value,
            Direction: direction.ToString(),
            TranslatorId: route.TranslatorId,
            GlossaryId: route.GlossaryId,
            RequestStyleRuleId: route.RequestStyleRuleId,
            ResponseStyleRuleId: route.ResponseStyleRuleId,
            RequestChars: requestChars,
            ResponseChars: responseChars,
            IntegrityFailures: integrityFailures,
            DurationMs: sw.ElapsedMilliseconds), ct);
    }

    private async Task<(byte[] Bytes, int Chars)> TranslateJsonAsync(
        byte[] bytes, Allowlist allowlist, LanguageCode source, LanguageCode target,
        ITranslator translator, ResolvedRules rules, StyleBinding style, CancellationToken ct)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(bytes); }
        catch (JsonException) { return (bytes, 0); }
        if (root is null) return (bytes, 0);

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
        }, ct);

        return (Encoding.UTF8.GetBytes(root.ToJsonString()), bytes.Length);
    }

    private async Task<(string Data, int Chars)> TranslateEventDataAsync(
        string data, Allowlist allowlist, LanguageCode source, LanguageCode target,
        ITranslator translator, ResolvedRules rules, StyleBinding style, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(data) || data == "[DONE]") return (data, 0);
        JsonNode? root;
        try { root = JsonNode.Parse(data); }
        catch (JsonException) { return (data, 0); }
        if (root is null) return (data, 0);

        var pipeline = new TranslationPipeline(translator, _piiRedactor, _translationCache);
        var stats = await pipeline.TranslateInPlaceAsync(root, allowlist, new PipelineOptions
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
        }, ct);

        return stats.SitesTranslated == 0 ? (data, 0) : (root.ToJsonString(), data.Length);
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

    private static DirectionMode ParseDirection(string raw) => raw.ToLowerInvariant() switch
    {
        "bidirectional" => DirectionMode.Bidirectional,
        "request-only" => DirectionMode.RequestOnly,
        "response-only" => DirectionMode.ResponseOnly,
        "off" => DirectionMode.Off,
        _ => DirectionMode.Bidirectional,
    };

    private static string ExtractTailPath(string inboundPath)
    {
        // /generic/<token>/<tail...>  →  <tail...>
        var segments = inboundPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length < 3 ? string.Empty : string.Join('/', segments[2..]);
    }

    private static Uri BuildUpstreamUri(string urlTemplate, string tail, QueryString query)
    {
        var baseUri = new Uri(urlTemplate, UriKind.Absolute);
        if (string.IsNullOrEmpty(tail) && !query.HasValue) return baseUri;

        var builder = new UriBuilder(baseUri);
        if (!string.IsNullOrEmpty(tail))
        {
            var currentPath = builder.Path.TrimEnd('/');
            builder.Path = currentPath + "/" + tail;
        }
        if (query.HasValue)
        {
            var existing = builder.Query.TrimStart('?');
            builder.Query = string.IsNullOrEmpty(existing)
                ? query.Value!.TrimStart('?')
                : existing + "&" + query.Value!.TrimStart('?');
        }
        return builder.Uri;
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
