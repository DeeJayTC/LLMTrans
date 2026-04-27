using AdaptiveApi.Plugins.SDK;

namespace AdaptiveApi.Core.Plugins;

/// Snapshot of every plugin module discovered at startup. The host populates
/// this once before the DI container is built; the management API and any
/// code that needs to iterate plugins reads from it at runtime.
public interface IPluginRegistry
{
    IReadOnlyList<PluginManifest> GetAllManifests();
    PluginManifest? GetManifest(string pluginId);
}

public sealed class PluginRegistry : IPluginRegistry
{
    private readonly Dictionary<string, PluginManifest> _byId;

    public PluginRegistry(IEnumerable<IAdaptiveApiPlugin> plugins)
    {
        _byId = plugins.ToDictionary(p => p.Manifest.Id, p => p.Manifest, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<PluginManifest> GetAllManifests() => _byId.Values.ToList();

    public PluginManifest? GetManifest(string pluginId) =>
        _byId.TryGetValue(pluginId, out var m) ? m : null;
}
