using AdaptiveApi.Core.Plugins;
using AdaptiveApi.Infrastructure.Persistence;
using AdaptiveApi.Plugins.SDK;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
        group.MapPost("/{id}/enable",
            (string id, IPluginRegistry r, AdaptiveApiDbContext db, CancellationToken ct) =>
                SetEnabledAsync(id, true, r, db, ct));
        group.MapPost("/{id}/disable",
            (string id, IPluginRegistry r, AdaptiveApiDbContext db, CancellationToken ct) =>
                SetEnabledAsync(id, false, r, db, ct));
    }

    public sealed record PluginDto(
        string Id, string Name, string Version, string Description, string Category,
        bool HasSettings, bool HasEndpoints, IReadOnlyList<string> Dependencies,
        bool AllowAnonymousEndpoints, bool Enabled,
        PluginMetricsDto? Metrics);

    public sealed record PluginMetricsDto(
        long Calls, long Failures, double TotalMs, double AvgMs, double LastMs);

    public sealed record DisabledPluginDto(string PluginId, string Version, string Reason);

    public sealed record PluginListDto(
        IReadOnlyList<PluginDto> Loaded,
        IReadOnlyList<DisabledPluginDto> Disabled);

    public sealed record SettingsDto(string SettingsJson);

    private static async Task<IResult> ListPlugins(IPluginRegistry registry, AdaptiveApiDbContext db, IPluginMetrics metrics, CancellationToken ct)
    {
        // Pull the disabled-set in a single query so the listing reflects
        // persisted enable state, not just what's loaded in DI.
        var disabledSet = await db.PluginSettings.AsNoTracking()
            .Where(x => x.TenantId == "*" && !x.Enabled)
            .Select(x => x.PluginId)
            .ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);

        var snapshot = metrics.Snapshot();
        var loaded = registry.GetAllManifests()
            .Select(m => new PluginDto(
                m.Id, m.Name, m.Version, m.Description, m.Category,
                m.HasSettings, m.HasEndpoints, m.Dependencies, m.AllowAnonymousEndpoints,
                Enabled: !disabledSet.Contains(m.Id),
                Metrics: snapshot.TryGetValue(m.Id, out var s)
                    ? new PluginMetricsDto(s.Calls, s.Failures, s.TotalMs, s.AvgMs, s.LastMs)
                    : null))
            .ToList();
        var disabled = registry.GetDisabled()
            .Select(d => new DisabledPluginDto(d.PluginId, d.Version, d.Reason))
            .ToList();
        return Results.Ok(new PluginListDto(loaded, disabled));
    }

    /// Persist an enable / disable for the plugin. The runtime gate is the
    /// scoped <see cref="IPluginEnablement"/> service the dispatcher consults
    /// per request — once this row is written, subsequent requests skip the
    /// disabled plugin's hooks.
    private static async Task<IResult> SetEnabledAsync(
        string id, bool enabled, IPluginRegistry registry,
        AdaptiveApiDbContext db, CancellationToken ct)
    {
        if (registry.GetManifest(id) is null) return Results.NotFound();

        var row = await db.PluginSettings
            .FirstOrDefaultAsync(x => x.TenantId == "*" && x.PluginId == id, ct);
        if (row is null)
        {
            row = new PluginSettingsEntity
            {
                TenantId = "*",
                PluginId = id,
                SettingsJson = "{}",
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.PluginSettings.Add(row);
        }
        row.Enabled = enabled;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
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

        // Validate JSON syntax at the input boundary; the store trusts callers.
        try
        {
            using (System.Text.Json.JsonDocument.Parse(body.SettingsJson)) { }
        }
        catch (System.Text.Json.JsonException ex)
        {
            return Results.BadRequest(new { error = "invalid_json", message = ex.Message });
        }

        await store.SetRawAsync(id, tenantId: null, body.SettingsJson, ct);
        return Results.NoContent();
    }
}
