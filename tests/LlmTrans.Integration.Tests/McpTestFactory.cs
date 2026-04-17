using System.Net.Http.Json;
using LlmTrans.Core.Abstractions;
using LlmTrans.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmTrans.Integration.Tests;

public sealed class McpTestFactory : WebApplicationFactory<Program>
{
    public FakeUpstreamHandler Upstream { get; } = new();
    public FakeBracketTranslator FakeTranslator { get; } = new();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(),
        $"llmtrans-mcp-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(cfg =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath,
                ["Translators:Default"] = "fake-brackets",
                ["Mcp:CatalogFile"] = "nonexistent-path-disable-auto-seed.json",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("mcp-upstream")
                .ConfigurePrimaryHttpMessageHandler(() => Upstream);
            services.AddSingleton<ITranslator>(FakeTranslator);
        });

        builder.UseEnvironment("Testing");
    }

    /// Returns (serverId, plaintextRouteToken) for a freshly-registered remote MCP server.
    public async Task<(string ServerId, string Token)> RegisterRemoteServerAsync(
        string userLanguage, string llmLanguage, string upstreamUrl)
    {
        using var client = CreateClient();
        var serverId = $"mcp_{Guid.NewGuid():N}";
        var req = await client.PostAsJsonAsync("/admin/mcp/servers", new
        {
            id = serverId,
            tenantId = Seeder.DevTenantId,
            name = "test mcp",
            transport = "remote",
            remoteUpstreamUrl = upstreamUrl,
            userLanguage,
            llmLanguage,
            translatorId = "fake-brackets",
            glossaryId = (string?)null,
            styleRuleId = (string?)null,
            proxyRuleId = (string?)null,
            catalogEntryId = (string?)null,
        });
        req.EnsureSuccessStatusCode();
        var body = await req.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
        var token = body!["routeToken"]!.GetValue<string>();
        return (serverId, token);
    }

    public async Task<string> RegisterStdioServerTokenAsync(string userLanguage, string llmLanguage)
    {
        using var client = CreateClient();
        var serverId = $"mcp_{Guid.NewGuid():N}";
        var req = await client.PostAsJsonAsync("/admin/mcp/servers", new
        {
            id = serverId,
            tenantId = Seeder.DevTenantId,
            name = "stdio mcp",
            transport = "stdio-local",
            remoteUpstreamUrl = (string?)null,
            userLanguage,
            llmLanguage,
            translatorId = "fake-brackets",
            glossaryId = (string?)null,
            styleRuleId = (string?)null,
            proxyRuleId = (string?)null,
            catalogEntryId = (string?)null,
        });
        req.EnsureSuccessStatusCode();
        var body = await req.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
        return body!["routeToken"]!.GetValue<string>();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}
