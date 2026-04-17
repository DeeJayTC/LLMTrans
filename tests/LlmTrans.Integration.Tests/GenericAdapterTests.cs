using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using LlmTrans.Core.Routing;
using LlmTrans.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmTrans.Integration.Tests;

public sealed class GenericTestFactory : WebApplicationFactory<Program>
{
    public FakeUpstreamHandler Upstream { get; } = new();
    public FakeBracketTranslator FakeTranslator { get; } = new();
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmtrans-gen-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath,
                ["Translators:Default"] = "fake-brackets",
                ["Mcp:CatalogFile"] = "nonexistent.json",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("generic-upstream")
                .ConfigurePrimaryHttpMessageHandler(() => Upstream);
            services.AddSingleton<LlmTrans.Core.Abstractions.ITranslator>(FakeTranslator);
        });

        builder.UseEnvironment("Testing");
    }

    /// Seeds a Generic route + token directly via the admin endpoints.
    public async Task<(string RouteId, string Token)> RegisterGenericRouteAsync(string configJson)
    {
        using var client = CreateClient();
        var routeId = $"gen_{Guid.NewGuid():N}";
        var resp = await client.PostAsJsonAsync("/admin/routes", new
        {
            id = routeId,
            tenantId = Seeder.DevTenantId,
            kind = nameof(RouteKind.Generic),
            upstreamBaseUrl = "https://ignored.example/", // not used — config_json carries the real URL
            userLanguage = "de",
            llmLanguage = "en",
            direction = "Bidirectional",
            translatorId = "fake-brackets",
            configJson = configJson,
        });
        resp.EnsureSuccessStatusCode();

        var tokResp = await client.PostAsync($"/admin/routes/{routeId}/tokens", content: null);
        tokResp.EnsureSuccessStatusCode();
        var tok = await tokResp.Content.ReadFromJsonAsync<JsonObject>();
        return (routeId, tok!["plaintextToken"]!.GetValue<string>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}

public sealed class GenericAdapterTests : IClassFixture<GenericTestFactory>
{
    private readonly GenericTestFactory _factory;

    public GenericAdapterTests(GenericTestFactory factory)
    {
        _factory = factory;
        _factory.Upstream.Requests.Clear();
        _factory.Upstream.ResponseBody = null;
        _factory.Upstream.ResponseContentType = "application/json";
    }

    [Fact]
    public async Task Non_streaming_translates_request_and_response_per_jsonpath()
    {
        var config = """
        {
          "upstream": { "urlTemplate": "https://api.cohere.com/v1/chat", "method": "POST" },
          "request": {
            "translateJsonPaths": ["$.message", "$.chat_history[*].message"]
          },
          "response": {
            "streaming": "none",
            "finalPaths": ["$.text", "$.generations[*].text"]
          },
          "direction": "bidirectional"
        }
        """;
        var (_, token) = await _factory.RegisterGenericRouteAsync(config);

        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(
            """{"text":"Reply from server","generations":[{"text":"Alt reply"}],"meta":{"warnings":[]}}""");

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/generic/{token}");
        req.Content = new StringContent("""
        {
          "message":"Frage an das System",
          "chat_history":[{"role":"USER","message":"Hallo"}]
        }
        """, Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Upstream received the forwarded request — message fields translated DE→EN.
        var captured = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("/v1/chat", captured.RequestUri.AbsolutePath);
        var sent = JsonNode.Parse(captured.BodyAsString()!)!;
        Assert.StartsWith("[en]", sent["message"]!.GetValue<string>());
        Assert.StartsWith("[en]", sent["chat_history"]![0]!["message"]!.GetValue<string>());
        Assert.Equal("USER", sent["chat_history"]![0]!["role"]!.GetValue<string>());

        // Response: `text` and `generations[*].text` translated EN→DE; meta untouched.
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.StartsWith("[de]", body["text"]!.GetValue<string>());
        Assert.StartsWith("[de]", body["generations"]![0]!["text"]!.GetValue<string>());
        Assert.NotNull(body["meta"]);
    }

    [Fact]
    public async Task Additional_headers_are_added_to_upstream_request()
    {
        var config = """
        {
          "upstream": {
            "urlTemplate": "https://api.cohere.com/v1/chat",
            "additionalHeaders": { "X-Client-Name": "llmtrans-test" }
          },
          "request": { "translateJsonPaths": [] },
          "response": { "streaming": "none", "finalPaths": [] },
          "direction": "off"
        }
        """;
        var (_, token) = await _factory.RegisterGenericRouteAsync(config);
        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes("""{}""");

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/generic/{token}");
        req.Content = new StringContent("""{"k":"v"}""", Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var captured = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("llmtrans-test", captured.Headers["X-Client-Name"]);
    }

    [Fact]
    public async Task Tail_path_is_appended_to_upstream()
    {
        var config = """
        {
          "upstream": { "urlTemplate": "https://api.cohere.com/v1" },
          "request": { "translateJsonPaths": [] },
          "response": { "streaming": "none", "finalPaths": [] },
          "direction": "off"
        }
        """;
        var (_, token) = await _factory.RegisterGenericRouteAsync(config);
        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes("{}");

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/generic/{token}/chat/completions?stream=false");
        req.Content = new StringContent("""{}""", Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        var captured = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("/v1/chat/completions", captured.RequestUri.AbsolutePath);
        Assert.Contains("stream=false", captured.RequestUri.Query);
    }

    [Fact]
    public async Task Sse_streaming_translates_event_text_fields()
    {
        var config = """
        {
          "upstream": { "urlTemplate": "https://api.cohere.com/v1/chat" },
          "request": { "translateJsonPaths": [] },
          "response": {
            "streaming": "sse",
            "eventPath": "$.text",
            "finalPaths": []
          },
          "direction": "bidirectional"
        }
        """;
        var (_, token) = await _factory.RegisterGenericRouteAsync(config);

        var sse = new StringBuilder();
        sse.Append("data: {\"text\":\"Hello there\"}\n\n");
        sse.Append("data: {\"text\":\"How are you today\"}\n\n");
        sse.Append("data: [DONE]\n\n");
        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(sse.ToString());
        _factory.Upstream.ResponseContentType = "text/event-stream";

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/generic/{token}");
        req.Content = new StringContent("""{"message":"hi"}""", Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var text = await resp.Content.ReadAsStringAsync();
        // Events' `text` field translated EN→DE (prefixed with [de]).
        Assert.Contains("[de]Hello there", text);
        Assert.Contains("[de]How are you today", text);
        Assert.EndsWith("data: [DONE]\n\n", text);
    }

    [Fact]
    public async Task Misconfigured_route_returns_500()
    {
        // Missing upstream URL.
        var (_, token) = await _factory.RegisterGenericRouteAsync(
            """{"upstream":{"urlTemplate":""},"request":{},"response":{},"direction":"off"}""");

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/generic/{token}");
        req.Content = new StringContent("""{}""", Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.Equal("route_misconfigured", body["error"]!["type"]!.GetValue<string>());
    }
}
