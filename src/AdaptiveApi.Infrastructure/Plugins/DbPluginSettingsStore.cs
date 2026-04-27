using System.Text.Json;
using AdaptiveApi.Infrastructure.Persistence;
using AdaptiveApi.Plugins.SDK;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Infrastructure.Plugins;

/// EF-backed plugin settings store. Persists opaque settings JSON keyed by
/// <c>(TenantId, PluginId)</c>; <c>tenantId = null</c> maps to the "*" sentinel
/// for global settings (v1 has no per-tenant scoping yet).
public sealed class DbPluginSettingsStore : IPluginSettingsStore
{
    private const string GlobalTenantId = "*";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AdaptiveApiDbContext _db;

    public DbPluginSettingsStore(AdaptiveApiDbContext db) => _db = db;

    public async Task<string?> GetRawAsync(string pluginId, string? tenantId, CancellationToken ct)
    {
        var key = tenantId ?? GlobalTenantId;
        var row = await _db.PluginSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == key && x.PluginId == pluginId, ct);
        return row?.SettingsJson;
    }

    public async Task SetRawAsync(string pluginId, string? tenantId, string json, CancellationToken ct)
    {
        // Validate JSON syntax before writing — schema is plugin-owned but
        // garbage strings would corrupt every future read.
        using (JsonDocument.Parse(json)) { }

        var key = tenantId ?? GlobalTenantId;
        var row = await _db.PluginSettings
            .FirstOrDefaultAsync(x => x.TenantId == key && x.PluginId == pluginId, ct);
        if (row is null)
        {
            row = new PluginSettingsEntity { TenantId = key, PluginId = pluginId };
            _db.PluginSettings.Add(row);
        }
        row.SettingsJson = json;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<T?> GetAsync<T>(string pluginId, string? tenantId, CancellationToken ct) where T : class
    {
        var raw = await GetRawAsync(pluginId, tenantId, ct);
        return string.IsNullOrEmpty(raw) ? null : JsonSerializer.Deserialize<T>(raw, Json);
    }

    public Task SetAsync<T>(string pluginId, string? tenantId, T value, CancellationToken ct) where T : class =>
        SetRawAsync(pluginId, tenantId, JsonSerializer.Serialize(value, Json), ct);
}
