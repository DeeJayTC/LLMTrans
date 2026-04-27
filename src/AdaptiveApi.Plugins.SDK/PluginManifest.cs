namespace AdaptiveApi.Plugins.SDK;

/// Identity, capabilities, and metadata for a plugin. Pure data — no DI, no
/// side-effects. Returned by <see cref="IAdaptiveApiPlugin.Manifest"/> and
/// surfaced via the management API to drive admin UIs.
public sealed class PluginManifest
{
    /// Stable, human-readable slug used as the route prefix and settings key.
    /// Must be lowercase, alphanumeric + dashes. Becomes the plugin's namespace
    /// under <c>/plugins/{Id}/...</c>.
    public required string Id { get; init; }

    /// Display name shown in the plugin admin UI.
    public required string Name { get; init; }

    /// SemVer version string.
    public required string Version { get; init; }

    /// Short description for the admin marketplace.
    public string Description { get; init; } = string.Empty;

    /// Category tag for grouping in the UI (e.g. "Observability", "Policy").
    public string Category { get; init; } = "Other";

    /// True when the plugin persists configuration. The host exposes
    /// <c>GET/PUT /admin/plugins/{Id}/settings</c> for plugins where this is set.
    public bool HasSettings { get; init; }

    /// True when the plugin contributes its own routes under
    /// <c>/plugins/{Id}/...</c> via <see cref="IAdaptiveApiPlugin.MapRoutes"/>.
    public bool HasEndpoints { get; init; }

    /// Plugin IDs that must also be loaded for this plugin to function.
    /// The host enforces these at startup; missing dependencies disable the plugin.
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}
