using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveApi.Integration.Tests;

public sealed class McpAdminTests : IClassFixture<McpTestFactory>
{
    private readonly McpTestFactory _factory;

    public McpAdminTests(McpTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_remote_server_rejects_missing_upstream_url()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/admin/mcp/servers", new
        {
            id = "x_remote_bad",
            tenantId = Seeder.DevTenantId,
            name = "bad",
            transport = "remote",
            remoteUpstreamUrl = (string?)null,
            userLanguage = "de",
            llmLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_invalid_transport_rejected()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/admin/mcp/servers", new
        {
            id = "x_bad_transport",
            tenantId = Seeder.DevTenantId,
            name = "bad",
            transport = "websocket",
            userLanguage = "de",
            llmLanguage = "en",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_stores_no_upstream_credentials_only_metadata()
    {
        using var client = _factory.CreateClient();
        var (id, token) = await _factory.RegisterRemoteServerAsync("de", "en", "https://mcp.linear.app/");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdaptiveApiDbContext>();
        var server = await db.McpServers.AsNoTracking().FirstAsync(s => s.Id == id);

        // McpServerEntity exposes exactly these fields — none for upstream credentials.
        var properties = typeof(McpServerEntity).GetProperties().Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("UpstreamToken", properties);
        Assert.DoesNotContain("UpstreamApiKey", properties);
        Assert.DoesNotContain("UpstreamEnv", properties);

        Assert.StartsWith("rt_", token);
        Assert.Equal("remote", server.Transport);
        Assert.Equal("https://mcp.linear.app/", server.RemoteUpstreamUrl);
    }

    [Fact]
    public async Task Snippet_for_remote_server_contains_url_placeholder_and_not_a_real_token()
    {
        var (id, _) = await _factory.RegisterRemoteServerAsync("de", "en", "https://mcp.example.com/");
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/admin/mcp/servers/{id}/snippet?client=claude-desktop");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        var snippet = body["snippet"]!.GetValue<string>();

        Assert.Contains("\"url\"", snippet);
        Assert.Contains("/mcp/<your-route-token>", snippet);
        Assert.Contains("<your-upstream-token-here>", snippet);
    }

    [Fact]
    public async Task Snippet_for_stdio_server_uses_bridge_command_form()
    {
        var tokenRes = await _factory.RegisterStdioServerTokenAsync("de", "en");
        // Fetch the server id by listing (token is opaque to the admin view).
        using var client = _factory.CreateClient();
        var list = await client.GetFromJsonAsync<JsonArray>("/admin/mcp/servers");
        var stdioServer = list!.OfType<JsonObject>().First(s => s["transport"]!.GetValue<string>() == "stdio-local");
        var id = stdioServer["id"]!.GetValue<string>();

        var resp = await client.GetAsync($"/admin/mcp/servers/{id}/snippet?client=claude-desktop");
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        var snippet = body["snippet"]!.GetValue<string>();

        Assert.Contains("@adaptiveapi/mcp-bridge", snippet);
        Assert.Contains("\"command\": \"npx\"", snippet);
        Assert.Contains("<your existing command>", snippet);
        Assert.Contains("<YOUR_EXISTING_ENV_KEY>", snippet);
    }

    [Fact]
    public async Task Catalog_endpoint_returns_entries_after_seed()
    {
        // Separate factory that points to our real catalog file.
        using var factory = new CatalogSeededFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/admin/mcp/catalog");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var list = JsonNode.Parse(await resp.Content.ReadAsStringAsync()) as JsonArray;
        Assert.NotNull(list);
        Assert.True(list!.Count > 0);
        Assert.Contains(list, e => e!["slug"]!.GetValue<string>() == "github");
        Assert.Contains(list, e => e!["slug"]!.GetValue<string>() == "linear");
    }

    private sealed class CatalogSeededFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"adaptiveapi-cat-{Guid.NewGuid():N}.db");
        private static readonly string CatalogRepoPath = FindCatalogPath();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(cfg =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = _dbPath,
                    ["Mcp:CatalogFile"] = CatalogRepoPath,
                });
            });
            builder.UseEnvironment("Testing");
        }

        private static string FindCatalogPath()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null)
            {
                var candidate = Path.Combine(d.FullName, "catalog", "mcp-servers.json");
                if (File.Exists(candidate)) return candidate;
                d = d.Parent;
            }
            return Path.Combine("catalog", "mcp-servers.json");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        }
    }
}
