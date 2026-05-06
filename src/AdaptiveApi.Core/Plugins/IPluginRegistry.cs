using AdaptiveApi.Plugins.SDK;

namespace AdaptiveApi.Core.Plugins;

/// Snapshot of every plugin module discovered at startup, including ones
/// that were rejected (missing dependencies, duplicate ids, …). The host
/// populates this once before the DI container is built; the management
/// API and any code that needs to iterate plugins reads from it at runtime.
public interface IPluginRegistry
{
    IReadOnlyList<PluginManifest> GetAllManifests();
    PluginManifest? GetManifest(string pluginId);

    /// Plugins that were discovered but not loaded, with the reason. Surfaced
    /// by the admin API so operators can see "disabled — missing X" in the
    /// UI rather than wondering why a plugin doesn't appear.
    IReadOnlyList<DisabledPluginEntry> GetDisabled();
}

/// Why a discovered plugin module is not running. <see cref="PluginId"/> and
/// <see cref="Version"/> are best-effort — for fatal load failures they may
/// be the assembly name when the manifest itself couldn't be read.
public sealed record DisabledPluginEntry(string PluginId, string Version, string Reason);

public sealed class PluginRegistry : IPluginRegistry
{
    private readonly Dictionary<string, PluginManifest> _byId;
    private readonly IReadOnlyList<DisabledPluginEntry> _disabled;

    public PluginRegistry(IEnumerable<IAdaptiveApiPlugin> plugins)
        : this(plugins, Array.Empty<DisabledPluginEntry>())
    {
    }

    public PluginRegistry(
        IEnumerable<IAdaptiveApiPlugin> plugins,
        IReadOnlyList<DisabledPluginEntry> disabled)
    {
        _byId = plugins.ToDictionary(p => p.Manifest.Id, p => p.Manifest, StringComparer.OrdinalIgnoreCase);
        _disabled = disabled;
    }

    public IReadOnlyList<PluginManifest> GetAllManifests() => _byId.Values.ToList();

    public PluginManifest? GetManifest(string pluginId) =>
        _byId.TryGetValue(pluginId, out var m) ? m : null;

    public IReadOnlyList<DisabledPluginEntry> GetDisabled() => _disabled;
}
