using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace AdaptiveApi.Integration.Tests;

/// Integration tests for the plugin admin surface. The
/// <see cref="IntegrationTestPlugin"/> in this project is picked up by the
/// loader's AppDomain scan whenever the API spins up, so these tests work
/// against a real plugin instance without any extra wiring.
public sealed class PluginAdminTests : IClassFixture<ProxyTestFactory>
{
    private readonly ProxyTestFactory _factory;
    public PluginAdminTests(ProxyTestFactory factory) => _factory = factory;

    [Fact]
    public async Task List_includes_manifest_for_loaded_plugin()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/admin/plugins");
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.NotNull(body);
        var loaded = body!["loaded"]!.AsArray();
        var match = loaded.FirstOrDefault(n => n!["id"]!.GetValue<string>() == "integration-test");
        Assert.NotNull(match);
        Assert.Equal("0.0.1", match!["version"]!.GetValue<string>());
        Assert.True(match["hasSettings"]!.GetValue<bool>());
        Assert.False(match["allowAnonymousEndpoints"]!.GetValue<bool>());

        Assert.NotNull(body["disabled"]);
    }

    [Fact]
    public async Task Settings_round_trip_returns_what_was_written()
    {
        using var client = _factory.CreateClient();

        var put = await client.PutAsJsonAsync("/admin/plugins/integration-test/settings",
            new { settingsJson = "{\"a\":1}" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var get = await client.GetAsync("/admin/plugins/integration-test/settings");
        get.EnsureSuccessStatusCode();
        var body = await get.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("{\"a\":1}", body!["settingsJson"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_json_settings_returns_400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PutAsJsonAsync("/admin/plugins/integration-test/settings",
            new { settingsJson = "{not-json" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Settings_for_unknown_plugin_returns_404()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PutAsJsonAsync("/admin/plugins/does-not-exist/settings",
            new { settingsJson = "{}" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
