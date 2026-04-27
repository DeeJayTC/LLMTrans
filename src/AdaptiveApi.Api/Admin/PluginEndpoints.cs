using AdaptiveApi.Core.Plugins;
using AdaptiveApi.Plugins.SDK;
using Microsoft.AspNetCore.Mvc;

namespace AdaptiveApi.Api.Admin;

/// Admin endpoints for plugin discovery and per-plugin settings. Plugins
/// expose their own management endpoints under <c>/plugins/{id}/...</c> via
/// <see cref="IAdaptiveApiPlugin.MapRoutes"/>; these endpoints are the
/// generic surface the management UI uses for the plugin list and the simple
/// opaque-JSON settings editor.
public static class PluginEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/plugins");

        group.MapGet("", ListPlugins);
        group.MapGet("/{id}/settings", GetSettings);
        group.MapPut("/{id}/settings", UpdateSettings);
    }

    public sealed record PluginDto(
        string Id, string Name, string Version, string Description, string Category,
        bool HasSettings, bool HasEndpoints, IReadOnlyList<string> Dependencies);

    public sealed record SettingsDto(string SettingsJson);

    private static IResult ListPlugins(IPluginRegistry registry)
    {
        var dtos = registry.GetAllManifests()
            .Select(m => new PluginDto(
                m.Id, m.Name, m.Version, m.Description, m.Category,
                m.HasSettings, m.HasEndpoints, m.Dependencies))
            .ToList();
        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetSettings(
        string id, IPluginRegistry registry, IPluginSettingsStore store, CancellationToken ct)
    {
        if (registry.GetManifest(id) is null) return Results.NotFound();
        var json = await store.GetRawAsync(id, tenantId: null, ct) ?? "{}";
        return Results.Ok(new SettingsDto(json));
    }

    private static async Task<IResult> UpdateSettings(
        string id,
        [FromBody] SettingsDto body,
        IPluginRegistry registry,
        IPluginSettingsStore store,
        CancellationToken ct)
    {
        if (registry.GetManifest(id) is null) return Results.NotFound();

        try
        {
            await store.SetRawAsync(id, tenantId: null, body.SettingsJson, ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Results.BadRequest(new { error = "invalid_json", message = ex.Message });
        }

        return Results.NoContent();
    }
}
