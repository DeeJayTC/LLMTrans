// ChatDemo — a minimal sample app demonstrating how to use adaptiveapi from a .NET service.
//
// The shape is deliberately tiny so the integration is legible: the only special thing
// this backend does is set the OpenAI base URL to `http://adaptiveapi/v1/<route-token>` and
// add `X-AdaptiveApi-Target-Lang` to each request. Everything else — streaming, tool calls,
// error handling — passes through adaptiveapi unchanged.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

var demoCfg = builder.Configuration.GetSection("Demo").Get<DemoOptions>() ?? new DemoOptions();
builder.Services.AddSingleton(demoCfg);

// HttpClient pointed at adaptiveapi's admin API. Used to discover routes and issue
// tokens. Never exposes an admin credential to the browser.
builder.Services.AddHttpClient("admin")
    .ConfigureHttpClient(client =>
    {
        client.BaseAddress = new Uri(demoCfg.LlmtransBaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(10);
    });

// HttpClient used for the actual chat calls. Base URL is set per-request because it
// varies by route (it embeds the route token in the path).
builder.Services.AddHttpClient("adaptiveapi")
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(120));

builder.Services.AddSingleton<RouteDirectory>();

builder.Services.AddCors(options => options.AddPolicy("demo-ui", p =>
    p.WithOrigins(demoCfg.AllowedOrigins)
     .AllowAnyHeader()
     .AllowAnyMethod()));

var app = builder.Build();
app.UseCors("demo-ui");

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/config", (DemoOptions cfg) => Results.Ok(new
{
    adaptiveapi = new
    {
        baseUrl = cfg.LlmtransBaseUrl,
    },
    openAiModel = cfg.Model,
    languages = Languages.All,
    hasServerKey = !string.IsNullOrEmpty(cfg.OpenAiApiKey),
    includePayloads = cfg.IncludePayloads,
}));

app.MapGet("/api/routes", async (RouteDirectory directory, CancellationToken ct) =>
{
    // Returns route metadata to the UI. Tokens stay on the server and are never
    // serialised in this response.
    var routes = await directory.ListAsync(ct);
    return Results.Ok(routes.Select(r => new
    {
        id = r.Id,
        kind = r.Kind,
        userLanguage = r.UserLanguage,
        llmLanguage = r.LlmLanguage,
        direction = r.Direction,
        translatorId = r.TranslatorId,
        upstreamBaseUrl = r.UpstreamBaseUrl,
        tokenMasked = Mask(r.Token),
    }));
});

app.MapPost("/api/chat", async (
    HttpContext context,
    ChatRequest req,
    IHttpClientFactory factory,
    RouteDirectory directory,
    DemoOptions cfg,
    ILogger<Program> log,
    CancellationToken ct) =>
{
    var pipeline = new PipelineRecorder();
    pipeline.Push("user_input", new Dictionary<string, object?>
    {
        ["chars"] = req.Message?.Length ?? 0,
        ["language"] = req.Language,
        ["streaming"] = false,
        ["strategy"] = req.StreamStrategy ?? "default",
    });

    if (string.IsNullOrWhiteSpace(req.Message))
        return Results.BadRequest(new { error = "empty_message", pipeline = pipeline.Entries });

    var openAiKey = ResolveKey(context, cfg);
    if (string.IsNullOrEmpty(openAiKey))
        return Results.Json(new { error = "openai_key_required", pipeline = pipeline.Entries },
            statusCode: StatusCodes.Status428PreconditionRequired);

    var route = await directory.FindAsync(req.RouteId, ct);
    if (route is null)
        return Results.Json(new { error = "route_not_found", pipeline = pipeline.Entries },
            statusCode: StatusCodes.Status400BadRequest);

    pipeline.Push("route_selected", new Dictionary<string, object?>
    {
        ["routeId"] = route.Id,
        ["kind"] = route.Kind,
        ["userLanguage"] = route.UserLanguage,
        ["llmLanguage"] = route.LlmLanguage,
        ["direction"] = route.Direction,
    });

    var http = factory.CreateClient("adaptiveapi");
    http.BaseAddress = new Uri($"{cfg.LlmtransBaseUrl.TrimEnd('/')}/v1/{route.Token}/");

    using var llmReq = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
    {
        Content = JsonContent.Create(BuildBody(cfg, req, stream: false), options: DemoJson.Opts),
    };
    ApplyCommonHeaders(llmReq, req, cfg, openAiKey, route);

    var sw = Stopwatch.StartNew();
    pipeline.Push("forwarded_to_adaptiveapi", new Dictionary<string, object?>
    {
        ["endpoint"] = "POST /v1/{token}/chat/completions",
        ["streaming"] = false,
    });

    using var resp = await http.SendAsync(llmReq, ct);
    sw.Stop();

    pipeline.Push("adaptiveapi_response_received", new Dictionary<string, object?>
    {
        ["status"] = (int)resp.StatusCode,
        ["totalMs"] = sw.ElapsedMilliseconds,
        ["contentType"] = resp.Content.Headers.ContentType?.MediaType,
    });
    foreach (var serverStep in ParseServerTiming(resp.Headers.GetValues("Server-Timing")))
        pipeline.Push($"adaptiveapi_{serverStep.Name}", new Dictionary<string, object?>
        {
            ["durationMs"] = serverStep.DurationMs,
            ["desc"] = serverStep.Description,
        });

    var body = await resp.Content.ReadAsStringAsync(ct);

    pipeline.Push("response_delivered_to_user", new Dictionary<string, object?>
    {
        ["bytes"] = body.Length,
        ["status"] = (int)resp.StatusCode,
    });

    if (!resp.IsSuccessStatusCode)
    {
        log.LogWarning("upstream {Status}: {Body}", (int)resp.StatusCode, body);
        return Results.Problem(
            title: "upstream_error",
            detail: body,
            statusCode: (int)resp.StatusCode,
            extensions: new Dictionary<string, object?> { ["pipeline"] = pipeline.Entries });
    }

    // Inject the pipeline alongside OpenAI's response body. JsonNode edit is cheaper
    // than round-tripping to a strongly-typed DTO.
    var payload = JsonNode.Parse(body) as JsonObject ?? new JsonObject();

    // If adaptiveapi returned a `_debug` field (because we sent X-AdaptiveApi-Debug: payloads),
    // hoist its contents into their own pipeline entries so the UI can render them
    // inline with everything else.
    if (payload["_debug"] is JsonObject debugNode)
    {
        foreach (var entry in BuildDebugPipelineEntries(debugNode))
            pipeline.Push(entry.Step, entry.Metadata);
        payload.Remove("_debug");
    }

    payload["_pipeline"] = JsonSerializer.SerializeToNode(pipeline.Entries, DemoJson.Opts);
    return Results.Content(payload.ToJsonString(DemoJson.Opts), "application/json");
});

app.MapPost("/api/chat/stream", async (
    HttpContext context,
    ChatRequest req,
    IHttpClientFactory factory,
    RouteDirectory directory,
    DemoOptions cfg,
    CancellationToken ct) =>
{
    var pipeline = new PipelineRecorder();
    pipeline.Push("user_input", new Dictionary<string, object?>
    {
        ["chars"] = req.Message?.Length ?? 0,
        ["language"] = req.Language,
        ["streaming"] = true,
        ["strategy"] = req.StreamStrategy ?? "default",
    });

    if (string.IsNullOrWhiteSpace(req.Message))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "empty_message",
            pipeline = pipeline.Entries }, ct);
        return;
    }

    var openAiKey = ResolveKey(context, cfg);
    if (string.IsNullOrEmpty(openAiKey))
    {
        context.Response.StatusCode = StatusCodes.Status428PreconditionRequired;
        await context.Response.WriteAsJsonAsync(new { error = "openai_key_required",
            pipeline = pipeline.Entries }, ct);
        return;
    }

    var route = await directory.FindAsync(req.RouteId, ct);
    if (route is null)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "route_not_found",
            pipeline = pipeline.Entries }, ct);
        return;
    }

    pipeline.Push("route_selected", new Dictionary<string, object?>
    {
        ["routeId"] = route.Id,
        ["kind"] = route.Kind,
        ["userLanguage"] = route.UserLanguage,
        ["llmLanguage"] = route.LlmLanguage,
        ["direction"] = route.Direction,
    });

    var http = factory.CreateClient("adaptiveapi");
    http.BaseAddress = new Uri($"{cfg.LlmtransBaseUrl.TrimEnd('/')}/v1/{route.Token}/");

    using var llmReq = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
    {
        Content = JsonContent.Create(BuildBody(cfg, req, stream: true), options: DemoJson.Opts),
    };
    ApplyCommonHeaders(llmReq, req, cfg, openAiKey, route);

    var sw = Stopwatch.StartNew();
    pipeline.Push("forwarded_to_adaptiveapi", new Dictionary<string, object?>
    {
        ["endpoint"] = "POST /v1/{token}/chat/completions",
        ["streaming"] = true,
    });

    using var resp = await http.SendAsync(llmReq, HttpCompletionOption.ResponseHeadersRead, ct);

    pipeline.Push("adaptiveapi_response_started", new Dictionary<string, object?>
    {
        ["status"] = (int)resp.StatusCode,
        ["headersMs"] = sw.ElapsedMilliseconds,
        ["contentType"] = resp.Content.Headers.ContentType?.MediaType,
    });
    foreach (var step in ParseServerTiming(resp.Headers.GetValues("Server-Timing")))
        pipeline.Push($"adaptiveapi_{step.Name}", new Dictionary<string, object?>
        {
            ["durationMs"] = step.DurationMs,
            ["desc"] = step.Description,
        });

    context.Response.StatusCode = (int)resp.StatusCode;
    context.Response.Headers["Content-Type"] = "text/event-stream";
    context.Response.Headers["Cache-Control"] = "no-cache";
    context.Response.Headers["Connection"] = "keep-alive";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    // Emit the pre-stream pipeline as a dedicated SSE event so the UI can render it
    // before any delta arrives. Ordering: `event: pipeline` first, then the normal
    // `data:` stream, then a final `event: pipeline` with the completion entry.
    await WritePipelineEventAsync(context.Response.Body, pipeline.Entries, ct);

    await using var upstreamStream = await resp.Content.ReadAsStreamAsync(ct);
    var firstByte = 0L;
    var totalBytes = 0L;
    var buffer = new byte[8192];
    int read;
    while ((read = await upstreamStream.ReadAsync(buffer, ct)) > 0)
    {
        if (firstByte == 0) firstByte = sw.ElapsedMilliseconds;
        await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
        await context.Response.Body.FlushAsync(ct);
        totalBytes += read;
    }
    sw.Stop();

    pipeline.Push("stream_complete", new Dictionary<string, object?>
    {
        ["firstByteMs"] = firstByte,
        ["totalMs"] = sw.ElapsedMilliseconds,
        ["bytes"] = totalBytes,
    });
    pipeline.Push("response_delivered_to_user", new Dictionary<string, object?>
    {
        ["status"] = (int)resp.StatusCode,
        ["bytes"] = totalBytes,
    });

    // Final pipeline event so the UI can display the stream-complete entry.
    await WritePipelineEventAsync(
        context.Response.Body,
        pipeline.Entries.TakeLast(2).ToList(),
        ct);
});

app.Run();

static async Task WritePipelineEventAsync(Stream stream, IReadOnlyList<PipelineEntry> entries, CancellationToken ct)
{
    var json = JsonSerializer.Serialize(entries, DemoJson.Opts);
    var bytes = Encoding.UTF8.GetBytes("event: pipeline\ndata: " + json + "\n\n");
    await stream.WriteAsync(bytes, ct);
    await stream.FlushAsync(ct);
}

/// Expands the `_debug` object adaptiveapi returns when `X-AdaptiveApi-Debug: payloads`
/// is set into a flat sequence of pipeline entries. Keeps the display model the
/// same whether the data arrived as an inline JSON field (non-streaming) or as a
/// trailing `event: x-adaptiveapi-debug` SSE event (streaming — handled in the UI).
static IEnumerable<(string Step, IReadOnlyDictionary<string, object?> Metadata)>
    BuildDebugPipelineEntries(JsonObject debugNode)
{
    if (debugNode["bodies"] is JsonObject bodies)
    {
        var pre = bodies["requestPreTranslation"]?.GetValue<string>();
        var post = bodies["requestPostTranslation"]?.GetValue<string>();
        var upstream = bodies["upstreamResponse"]?.GetValue<string>();
        var final = bodies["finalResponse"]?.GetValue<string>();

        if (pre is not null)
            yield return ("adaptiveapi_debug_request_pre",
                new Dictionary<string, object?> { ["body"] = pre, ["bytes"] = pre.Length });
        if (post is not null && post != pre)
            yield return ("adaptiveapi_debug_request_to_openai",
                new Dictionary<string, object?> { ["body"] = post, ["bytes"] = post.Length });
        if (upstream is not null)
            yield return ("adaptiveapi_debug_openai_response",
                new Dictionary<string, object?> { ["body"] = upstream, ["bytes"] = upstream.Length });
        if (final is not null && final != upstream)
            yield return ("adaptiveapi_debug_final_to_user",
                new Dictionary<string, object?> { ["body"] = final, ["bytes"] = final.Length });
    }

    if (debugNode["translatorCalls"] is JsonArray calls)
    {
        var idx = 0;
        foreach (var call in calls.OfType<JsonObject>())
        {
            var direction = call["direction"]?.GetValue<string>() ?? "translate";
            var src = call["sourceLanguage"]?.GetValue<string>() ?? "?";
            var tgt = call["targetLanguage"]?.GetValue<string>() ?? "?";
            var pairs = call["pairs"] as JsonArray ?? new JsonArray();
            var summary = pairs.OfType<JsonObject>()
                .Select(p => new
                {
                    source = p["source"]?.GetValue<string>(),
                    target = p["target"]?.GetValue<string>(),
                })
                .ToArray();
            yield return ($"adaptiveapi_debug_translator_{++idx}",
                new Dictionary<string, object?>
                {
                    ["direction"] = direction,
                    ["langPair"] = $"{src} → {tgt}",
                    ["pairs"] = summary,
                });
        }
    }
}

static IEnumerable<(string Name, long DurationMs, string? Description)> ParseServerTiming(IEnumerable<string>? headerValues)
{
    if (headerValues is null) yield break;
    foreach (var header in headerValues)
    {
        foreach (var metric in header.Split(','))
        {
            var parts = metric.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            var name = parts[0];
            long dur = 0;
            string? desc = null;
            for (var i = 1; i < parts.Length; i++)
            {
                var kv = parts[i].Split('=', 2);
                if (kv.Length != 2) continue;
                if (kv[0] == "dur" && long.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) dur = d;
                else if (kv[0] == "desc") desc = kv[1].Trim('"');
            }
            yield return (name, dur, desc);
        }
    }
}

static JsonObject BuildBody(DemoOptions cfg, ChatRequest req, bool stream)
{
    var messages = new JsonArray();

    if (!string.IsNullOrWhiteSpace(cfg.SystemPrompt))
    {
        messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = cfg.SystemPrompt,
        });
    }

    if (req.History is { Count: > 0 })
    {
        foreach (var turn in req.History)
        {
            messages.Add(new JsonObject
            {
                ["role"] = turn.Role,
                ["content"] = turn.Content,
            });
        }
    }

    messages.Add(new JsonObject
    {
        ["role"] = "user",
        ["content"] = req.Message,
    });

    var body = new JsonObject
    {
        ["model"] = cfg.Model,
        ["messages"] = messages,
        ["temperature"] = cfg.Temperature,
    };
    if (stream) body["stream"] = true;
    return body;
}

static void ApplyCommonHeaders(HttpRequestMessage req, ChatRequest chat, DemoOptions cfg, string openAiKey, RouteEntry route)
{
    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {openAiKey}");

    // When the caller overrides the route's language in the request, honour it.
    // Otherwise the route's configured languages apply and these headers are optional.
    var userLang = chat.Language ?? route.UserLanguage;
    var llmLang = cfg.LlmLanguage;
    if (!string.IsNullOrEmpty(userLang))
        req.Headers.TryAddWithoutValidation("X-AdaptiveApi-Target-Lang", userLang);
    if (!string.IsNullOrEmpty(llmLang))
        req.Headers.TryAddWithoutValidation("X-AdaptiveApi-Source-Lang", llmLang);

    if (!string.IsNullOrEmpty(chat.StreamStrategy))
        req.Headers.TryAddWithoutValidation("X-AdaptiveApi-Stream-Strategy", chat.StreamStrategy);

    // Opt in to debug payloads if the demo operator has configured it. adaptiveapi strips
    // this header before forwarding to OpenAI so upstream never sees the request for
    // internals.
    if (cfg.IncludePayloads)
        req.Headers.TryAddWithoutValidation("X-AdaptiveApi-Debug", "payloads");
}

static string ResolveKey(HttpContext context, DemoOptions cfg)
{
    var supplied = context.Request.Headers["X-Demo-OpenAI-Key"].ToString()?.Trim() ?? string.Empty;
    if (supplied.Length > 0) return supplied;
    return cfg.OpenAiApiKey;
}

static string Mask(string s)
{
    if (string.IsNullOrEmpty(s)) return string.Empty;
    if (s.Length <= 10) return new string('.', s.Length);
    return s[..6] + new string('.', Math.Max(4, s.Length - 10)) + s[^4..];
}

internal static class DemoJson
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

public sealed record ChatRequest(
    string Message,
    string? Language,
    string? StreamStrategy,
    string? RouteId,
    IReadOnlyList<ChatTurn>? History);

public sealed record ChatTurn(string Role, string Content);

public sealed class DemoOptions
{
    public string LlmtransBaseUrl { get; set; } = "http://api:8080";
    public string OpenAiApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public string LlmLanguage { get; set; } = "en-US";
    public double Temperature { get; set; } = 0.3;
    public string SystemPrompt { get; set; } =
        "You are a friendly assistant. Keep answers short and concrete.";
    public string[] AllowedOrigins { get; set; } = new[] { "http://localhost:8100", "http://localhost:5174" };

    /// WARNING: when true, the demo backend sends `X-AdaptiveApi-Debug: payloads` and
    /// includes the actual OpenAI + DeepL request/response bodies in its pipeline
    /// log so you can inspect what adaptiveapi translated. Only safe on a developer
    /// machine: payloads can contain the user's original message, the upstream LLM's
    /// full reply, and the DeepL source/target strings for every translation site.
    /// Set this to false before exposing the demo to anyone else's traffic.
    public bool IncludePayloads { get; set; } = true;
}

public static class Languages
{
    public static readonly LanguageInfo[] All =
    {
        new("en-US", "English (US)"),
        new("en-GB", "English (UK)"),
        new("de",    "Deutsch"),
        new("fr",    "Français"),
        new("es",    "Español"),
        new("it",    "Italiano"),
        new("nl",    "Nederlands"),
        new("pt-BR", "Português (Brasil)"),
        new("ja",    "日本語"),
        new("ko",    "한국어"),
        new("zh",    "中文"),
    };
}

public sealed record LanguageInfo(string Code, string Name);

public sealed record RouteEntry(
    string Id,
    string Kind,
    string UpstreamBaseUrl,
    string UserLanguage,
    string LlmLanguage,
    string Direction,
    string? TranslatorId,
    string Token);

public sealed record PipelineEntry(
    string Step,
    string TimestampIso,
    long SinceStartMs,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed class PipelineRecorder
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<PipelineEntry> _entries = new();

    public IReadOnlyList<PipelineEntry> Entries => _entries;

    public void Push(string step, IReadOnlyDictionary<string, object?> metadata)
    {
        _entries.Add(new PipelineEntry(
            Step: step,
            TimestampIso: DateTimeOffset.UtcNow.ToString("O"),
            SinceStartMs: _clock.ElapsedMilliseconds,
            Metadata: metadata));
    }
}

/// Discovers routes from the admin API and lazily issues tokens per route, caching
/// them in memory. Issued tokens live only inside the demo process — restarting the
/// demo produces fresh ones (the old ones remain in adaptiveapi's DB until revoked).
public sealed class RouteDirectory
{
    private readonly IHttpClientFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<RouteEntry>? _cache;

    public RouteDirectory(IHttpClientFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<RouteEntry>> ListAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is not null) return _cache;

            var http = _factory.CreateClient("admin");
            var routes = await http.GetFromJsonAsync<AdminRouteDto[]>("admin/routes", DemoJson.Opts, ct)
                         ?? Array.Empty<AdminRouteDto>();

            var entries = new List<RouteEntry>(routes.Length);
            foreach (var r in routes)
            {
                // Only OpenAI-compat chat routes are demoable here. The demo UI's
                // "chat" flow would be misleading for Anthropic (different body shape)
                // or MCP/Generic (different endpoints). Extension left as exercise.
                if (!string.Equals(r.Kind, "OpenAiChat", StringComparison.OrdinalIgnoreCase))
                    continue;

                var token = await IssueTokenAsync(http, r.Id, ct);
                if (token is null) continue;
                entries.Add(new RouteEntry(
                    Id: r.Id,
                    Kind: r.Kind,
                    UpstreamBaseUrl: r.UpstreamBaseUrl,
                    UserLanguage: r.UserLanguage,
                    LlmLanguage: r.LlmLanguage,
                    Direction: r.Direction,
                    TranslatorId: r.TranslatorId,
                    Token: token));
            }
            _cache = entries;
            return _cache;
        }
        finally { _gate.Release(); }
    }

    public async Task<RouteEntry?> FindAsync(string? routeId, CancellationToken ct)
    {
        var routes = await ListAsync(ct);
        if (string.IsNullOrEmpty(routeId))
            return routes.FirstOrDefault();
        return routes.FirstOrDefault(r => string.Equals(r.Id, routeId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> IssueTokenAsync(HttpClient http, string routeId, CancellationToken ct)
    {
        using var resp = await http.PostAsync($"admin/routes/{routeId}/tokens", content: null, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var payload = await resp.Content.ReadFromJsonAsync<JsonObject>(DemoJson.Opts, ct);
        return payload?["plaintextToken"]?.GetValue<string>();
    }

    private sealed record AdminRouteDto(
        string Id,
        string Kind,
        string UpstreamBaseUrl,
        string UserLanguage,
        string LlmLanguage,
        string Direction,
        string? TranslatorId);
}
