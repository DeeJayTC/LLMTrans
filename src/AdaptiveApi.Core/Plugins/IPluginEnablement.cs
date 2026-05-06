namespace AdaptiveApi.Core.Plugins;

/// Per-request gate the dispatcher consults before invoking a plugin's hooks.
/// Backed by the plugin settings table (the <c>Enabled</c> column there).
/// Defaults to enabled when no row exists, so freshly-loaded plugins run
/// without an explicit opt-in.
///
/// Lifetime is <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped"/>:
/// the implementation may cache the disabled-set for the duration of a
/// single request to avoid hitting the DB on every hook call. Background
/// callers without an HTTP scope get a no-op implementation that treats
/// every plugin as enabled.
public interface IPluginEnablement
{
    bool IsEnabled(string pluginId);
}

/// Fallback used when no scoped <see cref="IPluginEnablement"/> is available
/// (background tasks, unit tests). Treats every plugin as enabled — the
/// safer default since callers in those contexts haven't expressed otherwise.
public sealed class AlwaysEnabled : IPluginEnablement
{
    public bool IsEnabled(string pluginId) => true;
}
