namespace AdaptiveApi.Plugins.SDK;

/// Per-plugin settings persistence. Keys are <c>(pluginId, tenantId)</c>; pass
/// <c>tenantId = null</c> for global settings (the only mode supported in v1).
///
/// Settings JSON is opaque to the host — each plugin owns its schema. Use the
/// typed overloads for normal access; the raw overloads exist for the admin
/// management API which round-trips strings.
public interface IPluginSettingsStore
{
    Task<string?> GetRawAsync(string pluginId, string? tenantId, CancellationToken ct);
    Task SetRawAsync(string pluginId, string? tenantId, string json, CancellationToken ct);

    Task<T?> GetAsync<T>(string pluginId, string? tenantId, CancellationToken ct) where T : class;
    Task SetAsync<T>(string pluginId, string? tenantId, T value, CancellationToken ct) where T : class;
}
