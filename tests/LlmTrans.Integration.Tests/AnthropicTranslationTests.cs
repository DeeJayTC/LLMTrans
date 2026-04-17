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

/// A separate factory seeded with an Anthropic route pointing at our fake upstream.
public sealed class AnthropicTestFactory : WebApplicationFactory<Program>
{
    public const string FixedToken = "rt_anth_TESTTOKEN";
    public FakeUpstreamHandler Upstream { get; } = new();
    public FakeBracketTranslator FakeTranslator { get; } = new();
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmtrans-anth-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath,
                ["Translators:Default"] = "fake-brackets",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("anthropic-upstream")
                .ConfigurePrimaryHttpMessageHandler(() => Upstream);
            services.AddSingleton<LlmTrans.Core.Abstractions.ITranslator>(FakeTranslator);
        });

        builder.UseEnvironment("Testing");
    }

    public async Task SeedRouteAsync(string userLang, string llmLang)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LlmTransDbContext>();
        await db.Database.EnsureCreatedAsync();

        const string tenantId = "t_anth";
        const string routeId = "r_anth";

        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId))
            db.Tenants.Add(new TenantEntity { Id = tenantId, Name = "Anthropic test", CreatedAt = DateTimeOffset.UtcNow });

        if (!await db.Routes.AnyAsync(r => r.Id == routeId))
            db.Routes.Add(new RouteEntity
            {
                Id = routeId,
                TenantId = tenantId,
                Kind = nameof(RouteKind.AnthropicMessages),
                UpstreamBaseUrl = "https://api.anthropic.com/",
                UserLanguage = userLang,
                LlmLanguage = llmLang,
                Direction = "Bidirectional",
                CreatedAt = DateTimeOffset.UtcNow,
            });

        if (!await db.RouteTokens.AnyAsync(t => t.RouteId == routeId))
            db.RouteTokens.Add(new RouteTokenEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                TenantId = tenantId,
                RouteId = routeId,
                Prefix = RouteToken.PrefixOf(FixedToken),
                Hash = RouteToken.HashForStorage(FixedToken),
                CreatedAt = DateTimeOffset.UtcNow,
            });

        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}

public sealed class AnthropicTranslationTests : IClassFixture<AnthropicTestFactory>
{
    private readonly AnthropicTestFactory _factory;

    public AnthropicTranslationTests(AnthropicTestFactory factory)
    {
        _factory = factory;
        _factory.Upstream.Requests.Clear();
        _factory.Upstream.ResponseBody = null;
        _factory.Upstream.ResponseContentType = "application/json";
    }

    [Fact]
    public async Task Round_trip_translates_system_messages_and_response_content()
    {
        await _factory.SeedRouteAsync("de", "en");

        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(
            """{"id":"msg_1","type":"message","role":"assistant","model":"claude-3","content":[{"type":"text","text":"Sure, here is the answer."}],"stop_reason":"end_turn"}""");

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/anthropic/v1/{AnthropicTestFactory.FixedToken}/messages");
        req.Content = new StringContent("""
            {
              "model":"claude-3-sonnet-latest",
              "system":"You are helpful.",
              "messages":[{"role":"user","content":"Please assist."}]
            }
            """, Encoding.UTF8, "application/json");
        req.Headers.Add("x-api-key", "sk-ant-test-upstream");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Upstream received the exact API key (pass-through).
        var captured = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("sk-ant-test-upstream", captured.Headers["x-api-key"]);

        // Request: system + user content translated DE → EN (prefixed with [en]).
        var sentBody = captured.BodyAsString()!;
        var sent = JsonNode.Parse(sentBody)!;
        Assert.StartsWith("[en]", sent["system"]!.GetValue<string>());
        Assert.StartsWith("[en]", sent["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("claude-3-sonnet-latest", sent["model"]!.GetValue<string>());

        // Response: content[0].text translated EN → DE.
        var body = await resp.Content.ReadAsStringAsync();
        var root = JsonNode.Parse(body)!;
        Assert.StartsWith("[de]", root["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("msg_1", root["id"]!.GetValue<string>());
        Assert.Equal("end_turn", root["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_anthropic_route_token_returns_401()
    {
        await _factory.SeedRouteAsync("de", "en");
        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "/anthropic/v1/rt_anth_WRONGTOKEN/messages");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Openai_token_cannot_be_used_on_anthropic_endpoint()
    {
        await _factory.SeedRouteAsync("de", "en");
        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/anthropic/v1/{ProxyTestFactory.FixedToken}/messages");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
