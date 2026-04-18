using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Model;
using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Core.Proxy;
using AdaptiveApi.Core.Routing;
using AdaptiveApi.Core.Rules;
using AdaptiveApi.Core.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AdaptiveApi.Providers.OpenAI;

public sealed class OpenAiChatAdapter : IProviderAdapter
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ITranslatorRouter _translatorRouter;
    private readonly IRuleResolver _ruleResolver;
    private readonly IAuditSink _audit;
    private readonly IPiiRedactor _piiRedactor;
    private readonly ILogger<OpenAiChatAdapter> _log;

    public OpenAiChatAdapter(
        IHttpClientFactory httpFactory,
        ITranslatorRouter translatorRouter,
        IRuleResolver ruleResolver,
        IAuditSink audit,
        IPiiRedactor piiRedactor,
        ILogger<OpenAiChatAdapter> log)
    {
        _httpFactory = httpFactory;
        _translatorRouter = translatorRouter;
        _ruleResolver = ruleResolver;
        _audit = audit;
        _piiRedactor = piiRedactor;
        _log = log;
    }

    public string ProviderId => "openai.chat";
    public RouteKind RouteKind => RouteKind.OpenAiChat;

    public bool Matches(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        return request.Method == HttpMethods.Post
               && (path.EndsWith("/chat/completions", StringComparison.Ordinal)
                   || path.EndsWith("/responses", StringComparison.Ordinal)
                   || path.EndsWith("/embeddings", StringComparison.Ordinal));
    }

    public async Task HandleAsync(HttpContext context, RouteConfig route, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var timings = new PipelineTimings();
        var inbound = context.Request;
        var upstreamPath = ExtractUpstreamPath(inbound.Path.Value!);
        var upstreamUri = new Uri(route.UpstreamBaseUrl, upstreamPath + inbound.QueryString);

        var (bodyBytes, streaming) = await ReadBodyAsync(inbound, ct);

        var effective = ApplyHeaderOverrides(route, inbound);
        var translationActive = effective.Direction != DirectionMode.Off
                                && effective.UserLanguage.Value != effective.LlmLanguage.Value;

        // Debug mode is off unless the caller explicitly asks for it. In debug mode
        // the adapter captures the pre/post-translation bodies and the raw translator
        // I/O, then attaches them to the response so callers can show the full chain.
        // Never turn this on for production clients — payloads can include user input.
        var debugMode = DebugRequested(inbound);
        var debug = debugMode ? new DebugRecorder() : null;
        if (debug is not null && bodyBytes.Length > 0)
            debug.SetRequestPre(System.Text.Encoding.UTF8.GetString(bodyBytes));

        var planSw = Stopwatch.StartNew();
        var rules = translationActive
            ? await _ruleResolver.ResolveAsync(effective, ct)
            : EmptyRules(effective);
        planSw.Stop();
        timings.Record("plan", planSw.Elapsed, "resolve rules + planner");

        var forwardBody = bodyBytes;
        var requestChars = 0;
        // Request translation runs whether or not the response is streamed — the two
        // are orthogonal. Streaming only changes how the RESPONSE is emitted.
        if (translationActive && bodyBytes.Length > 0
            && effective.Direction is DirectionMode.Bidirectional or DirectionMode.RequestOnly)
        {
            var translateSw = Stopwatch.StartNew();
            (forwardBody, requestChars) = await TranslateJsonAsync(bodyBytes, rules.RequestAllowlist,
                source: effective.UserLanguage, target: effective.LlmLanguage, effective, rules, rules.RequestStyle, ct,
                debug: debug, debugDirection: "translate-request");
            translateSw.Stop();
            timings.Record("translate-request", translateSw.Elapsed,
                $"{effective.UserLanguage.Value} to {effective.LlmLanguage.Value}");
        }
        if (debug is not null && forwardBody.Length > 0)
            debug.SetRequestPost(System.Text.Encoding.UTF8.GetString(forwardBody));

        using var upstreamReq = new HttpRequestMessage(new HttpMethod(inbound.Method), upstreamUri);
        if (forwardBody.Length > 0)
        {
            upstreamReq.Content = new ByteArrayContent(forwardBody);
            upstreamReq.Content.Headers.TryAddWithoutValidation(
                "Content-Type", inbound.ContentType ?? "application/json");
        }
        HeaderForwarder.CopyInboundToUpstream(inbound.Headers, upstreamReq, upstreamReq.Content);

        var http = _httpFactory.CreateClient("openai-upstream");
        var upstreamSw = Stopwatch.StartNew();
        using var upstreamResp = await http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);
        upstreamSw.Stop();
        timings.Record("upstream", upstreamSw.Elapsed, "OpenAI round-trip");

        context.Response.StatusCode = (int)upstreamResp.StatusCode;
        context.Response.Headers.Remove("transfer-encoding");

        var responseTranslationActive = translationActive
            && upstreamResp.IsSuccessStatusCode
            && (effective.Direction is DirectionMode.Bidirectional or DirectionMode.ResponseOnly);

        var integrityFailures = 0;
        var responseChars = 0;

        if (streaming && responseTranslationActive)
        {
            HeaderForwarder.CopyUpstreamToClient(upstreamResp, context.Response.Headers);
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers.Remove("Content-Length");

            // Emit Server-Timing before the SSE body starts streaming. Response-translate
            // timings can't be captured under streaming; the client measures total stream
            // wall-time instead.
            timings.WriteTo(context);

            // Stream translators lack the debug-pipeline hook that the JSON path uses.
            // Capture the raw upstream stream bytes (capped) + the translated output
            // bytes we emit to the client, so debug clients see both sides.
            var strategy = StreamStrategyFor(inbound);
            var respTransSw = Stopwatch.StartNew();
            StreamTranslatorMetrics m;
            if (debug is not null)
            {
                var upstreamCapture = new MemoryStream();
                var clientCapture = new MemoryStream();
                await using (upstreamCapture)
                await using (clientCapture)
                await using (var sourceTap = await upstreamResp.Content.ReadAsStreamAsync(ct))
                {
                    using var sourceTee = new TappingStream(sourceTap, upstreamCapture, cap: 128 * 1024);
                    using var sinkTee = new TappingStream(context.Response.Body, clientCapture, cap: 128 * 1024);
                    var translator = _translatorRouter.Resolve(effective);
                    if (strategy == "progressive")
                    {
                        var progressive = new ProgressiveStreamTranslator(translator, systemContext: rules.SystemContext);
                        m = await progressive.TranslateAsync(sourceTee, sinkTee,
                            source: effective.LlmLanguage, target: effective.UserLanguage, ct);
                    }
                    else
                    {
                        var streamer = new OpenAiStreamTranslator(translator, systemContext: rules.SystemContext);
                        m = await streamer.TranslateAsync(sourceTee, sinkTee,
                            source: effective.LlmLanguage, target: effective.UserLanguage, ct);
                    }
                    debug.SetUpstreamResponse(System.Text.Encoding.UTF8.GetString(upstreamCapture.ToArray()));
                    debug.SetFinalResponse(System.Text.Encoding.UTF8.GetString(clientCapture.ToArray()));
                }
            }
            else
            {
                await using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);
                var translator = _translatorRouter.Resolve(effective);
                if (strategy == "progressive")
                {
                    var progressive = new ProgressiveStreamTranslator(translator, systemContext: rules.SystemContext);
                    m = await progressive.TranslateAsync(upstreamStream, context.Response.Body,
                        source: effective.LlmLanguage, target: effective.UserLanguage, ct);
                }
                else
                {
                    var streamer = new OpenAiStreamTranslator(translator, systemContext: rules.SystemContext);
                    m = await streamer.TranslateAsync(upstreamStream, context.Response.Body,
                        source: effective.LlmLanguage, target: effective.UserLanguage, ct);
                }
            }
            respTransSw.Stop();
            timings.Record("translate-response-stream", respTransSw.Elapsed,
                $"{strategy} stream, {m.CharsTranslated} chars");
            integrityFailures = m.IntegrityFailures;
            responseChars = m.CharsTranslated;

            // Emit a trailing SSE event with the final timings so downstream clients
            // can reconstruct the full pipeline timeline even though Server-Timing
            // had to be flushed before the body started.
            var trailingJson = BuildTrailingTimingsJson(timings, strategy, m);
            var trailingBytes = System.Text.Encoding.UTF8.GetBytes(
                $"event: x-adaptiveapi-timing\ndata: {trailingJson}\n\n");
            await context.Response.Body.WriteAsync(trailingBytes, ct);
            await context.Response.Body.FlushAsync(ct);

            // Mirror debug traces as their own trailing SSE event so clients get the
            // translator I/O and body samples without needing a second channel.
            if (debug is not null)
            {
                var debugJson = BuildDebugEventJson(debug);
                var debugBytes = System.Text.Encoding.UTF8.GetBytes(
                    $"event: x-adaptiveapi-debug\ndata: {debugJson}\n\n");
                await context.Response.Body.WriteAsync(debugBytes, ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        else if (!responseTranslationActive)
        {
            timings.WriteTo(context);
            HeaderForwarder.CopyUpstreamToClient(upstreamResp, context.Response.Headers);
            await using var upstreamStream = await upstreamResp.Content.ReadAsStreamAsync(ct);
            await upstreamStream.CopyToAsync(context.Response.Body, 8192, ct);
        }
        else
        {
            var respBytes = await upstreamResp.Content.ReadAsByteArrayAsync(ct);
            if (debug is not null)
                debug.SetUpstreamResponse(System.Text.Encoding.UTF8.GetString(respBytes));

            var respTransSw = Stopwatch.StartNew();
            var (translatedResp, chars) = await TranslateJsonAsync(respBytes, rules.ResponseAllowlist,
                source: effective.LlmLanguage, target: effective.UserLanguage, effective, rules, rules.ResponseStyle, ct,
                debug: debug, debugDirection: "translate-response");
            respTransSw.Stop();
            timings.Record("translate-response", respTransSw.Elapsed,
                $"{effective.LlmLanguage.Value} to {effective.UserLanguage.Value}");
            responseChars = chars;

            HeaderForwarder.CopyUpstreamToClient(upstreamResp, context.Response.Headers);

            // When debug is on, inject `_debug` into the response JSON so the demo can
            // display the translator I/O alongside the message. This mutates the OpenAI
            // response shape — that's acceptable in debug mode since the caller opted in.
            byte[] finalBytes;
            if (debug is not null)
            {
                finalBytes = InjectDebugIntoJsonBody(translatedResp, debug);
            }
            else
            {
                finalBytes = translatedResp;
            }

            if (debug is not null)
                debug.SetFinalResponse(System.Text.Encoding.UTF8.GetString(finalBytes));

            context.Response.Headers["Content-Length"] = finalBytes.Length.ToString();
            timings.WriteTo(context);
            await context.Response.Body.WriteAsync(finalBytes, ct);
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
            IntegrityFailures: integrityFailures,
            DurationMs: sw.ElapsedMilliseconds), ct);
    }

    private async Task<(byte[] Bytes, int Chars)> TranslateJsonAsync(
        byte[] bytes, Allowlist allowlist, LanguageCode source, LanguageCode target,
        RouteConfig route, ResolvedRules rules, StyleBinding style, CancellationToken ct,
        DebugRecorder? debug = null, string debugDirection = "translate")
    {
        JsonNode? root;
        try { root = JsonNode.Parse(bytes); }
        catch (JsonException) { return (bytes, 0); }
        if (root is null) return (bytes, 0);

        var translator = _translatorRouter.Resolve(route);
        var pipeline = new TranslationPipeline(translator, _piiRedactor);

        var pipelineOptions = new PipelineOptions
        {
            Source = source,
            Target = target,
            GlossaryId = rules.DeeplGlossaryId,
            StyleRuleId = style.DeeplStyleId,
            CustomInstructions = rules.SystemInstructionsFor(style, source.Value, target.Value),
            DoNotTranslateTerms = rules.DoNotTranslateFor(source.Value, target.Value),
            Formality = rules.Formality,
            RedactPii = rules.RedactPii,
            Context = rules.SystemContext,
            Debug = debug,
            DebugDirection = debugDirection,
        };

        var stats = await pipeline.TranslateInPlaceAsync(root, allowlist, pipelineOptions, ct);
        await ToolCallTranslator.TranslateAsync(
            root, ToolCallTranslator.RootShape.OpenAiChat,
            translator, source, target, rules.ToolArgsDenylist, ct);

        var outBytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        return (outBytes, bytes.Length); // chars approximates bytes for sizing audit
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
            ? new LanguageCode(tl.ToString().Trim().ToLowerInvariant())
            : route.UserLanguage;
        var llmLang = req.Headers.TryGetValue("X-AdaptiveApi-Source-Lang", out var sl)
            ? new LanguageCode(sl.ToString().Trim().ToLowerInvariant())
            : route.LlmLanguage;
        var mode = req.Headers.TryGetValue("X-AdaptiveApi-Mode", out var m)
            ? Enum.TryParse<DirectionMode>(m.ToString(), true, out var parsed) ? parsed : route.Direction
            : route.Direction;
        var translator = req.Headers.TryGetValue("X-AdaptiveApi-Translator", out var t)
            ? t.ToString() : route.TranslatorId;
        var glossary = req.Headers.TryGetValue("X-AdaptiveApi-Glossary", out var g) ? g.ToString() : route.GlossaryId;
        // Header precedence:
        //   X-AdaptiveApi-Request-Style-Rule / X-AdaptiveApi-Response-Style-Rule override per direction.
        //   X-AdaptiveApi-Style-Rule (legacy) overrides both directions unless a direction-specific
        //   header is also present.
        string? legacyStyle = req.Headers.TryGetValue("X-AdaptiveApi-Style-Rule", out var srv)
            ? srv.ToString() : null;
        var requestStyle = req.Headers.TryGetValue("X-AdaptiveApi-Request-Style-Rule", out var rsr)
            ? rsr.ToString()
            : legacyStyle ?? route.RequestStyleRuleId;
        var responseStyle = req.Headers.TryGetValue("X-AdaptiveApi-Response-Style-Rule", out var rspsr)
            ? rspsr.ToString()
            : legacyStyle ?? route.ResponseStyleRuleId;

        if (tl.Count > 0 && mode == DirectionMode.Off) mode = DirectionMode.Bidirectional;

        return route with
        {
            UserLanguage = userLang,
            LlmLanguage = llmLang,
            Direction = mode,
            TranslatorId = translator,
            GlossaryId = glossary,
            RequestStyleRuleId = requestStyle,
            ResponseStyleRuleId = responseStyle,
        };
    }

    private static async Task<(byte[] Body, bool Streaming)> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        if (req.ContentLength is null or 0 && !req.Headers.ContainsKey("Transfer-Encoding"))
            return (Array.Empty<byte>(), false);

        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var streaming = IsStreamRequested(bytes);
        return (bytes, streaming);
    }

    private static bool IsStreamRequested(byte[] bytes)
    {
        if (bytes.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.TryGetProperty("stream", out var s)
                   && s.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// Picks a streaming strategy. Request header `X-AdaptiveApi-Stream-Strategy: progressive`
    /// flips to the lower-latency progressive translator. Default stays on the
    /// sentence-boundary translator from M3.
    private static string StreamStrategyFor(HttpRequest req)
    {
        if (req.Headers.TryGetValue("X-AdaptiveApi-Stream-Strategy", out var v))
            return v.ToString().Trim().ToLowerInvariant();
        return "sentence";
    }

    private static bool DebugRequested(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("X-AdaptiveApi-Debug", out var v)) return false;
        var asked = v.ToString().Trim().ToLowerInvariant();
        return asked == "payloads" || asked == "1" || asked == "true" || asked == "on";
    }

    private static string BuildDebugEventJson(DebugRecorder debug)
    {
        var payload = BuildDebugNode(debug);
        return payload.ToJsonString();
    }

    private static byte[] InjectDebugIntoJsonBody(byte[] body, DebugRecorder debug)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return body; }
        if (root is not JsonObject obj) return body;

        obj["_debug"] = BuildDebugNode(debug);
        return System.Text.Encoding.UTF8.GetBytes(obj.ToJsonString());
    }

    private static JsonObject BuildDebugNode(DebugRecorder debug)
    {
        var traces = new JsonArray();
        foreach (var t in debug.Translations)
        {
            var pairs = new JsonArray();
            foreach (var p in t.Pairs)
            {
                pairs.Add(new JsonObject
                {
                    ["source"] = p.Source,
                    ["target"] = p.Target,
                });
            }
            traces.Add(new JsonObject
            {
                ["direction"] = t.Direction,
                ["sourceLanguage"] = t.SourceLanguage,
                ["targetLanguage"] = t.TargetLanguage,
                ["pairs"] = pairs,
            });
        }

        return new JsonObject
        {
            ["bodies"] = new JsonObject
            {
                ["requestPreTranslation"] = debug.RequestBodyPreTranslation,
                ["requestPostTranslation"] = debug.RequestBodyPostTranslation,
                ["upstreamResponse"] = debug.UpstreamResponseBody,
                ["finalResponse"] = debug.FinalResponseBody,
            },
            ["translatorCalls"] = traces,
        };
    }

    /// Serialises the full pipeline timings plus final stream stats so the trailing
    /// `x-adaptiveapi-timing` SSE event carries the same information Server-Timing
    /// would have, for cases where the client is parsing SSE anyway.
    private static string BuildTrailingTimingsJson(
        PipelineTimings timings, string strategy, StreamTranslatorMetrics m)
    {
        var entries = new JsonArray();
        foreach (var e in timings.Entries)
        {
            entries.Add(new JsonObject
            {
                ["name"] = e.Name,
                ["durationMs"] = e.DurationMs,
                ["desc"] = e.Description,
            });
        }

        var payload = new JsonObject
        {
            ["entries"] = entries,
            ["stream"] = new JsonObject
            {
                ["strategy"] = strategy,
                ["eventsIn"] = m.EventsIn,
                ["eventsOut"] = m.EventsOut,
                ["charsTranslated"] = m.CharsTranslated,
                ["integrityFailures"] = m.IntegrityFailures,
            },
        };
        return payload.ToJsonString();
    }

    private static string ExtractUpstreamPath(string inboundPath)
    {
        var segments = inboundPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments[0] != "v1")
            throw new InvalidOperationException($"unexpected inbound path shape: {inboundPath}");
        return "/v1/" + string.Join('/', segments[2..]);
    }
}
