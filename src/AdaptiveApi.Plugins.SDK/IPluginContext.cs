using Microsoft.Extensions.Logging;

namespace AdaptiveApi.Plugins.SDK;

/// Runtime context handed to a plugin. Resolve scoped services through
/// <see cref="Services"/> — never cache the provider, since hooks run inside
/// per-request scopes.
public interface IPluginContext
{
    IServiceProvider Services { get; }
    ILogger Logger { get; }
}
