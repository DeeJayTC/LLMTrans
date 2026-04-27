using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveApi.Plugins.SDK;

/// Entry point for a plugin's backend registration.
///
/// Implement this interface (one class per plugin) and place the assembly
/// either next to the host (named <c>AdaptiveApi.*.dll</c>) or in the host's
/// <c>plugins/</c> directory. The host discovers the type at startup and calls:
///   1. <see cref="RegisterServices"/> — register hooks, services, repositories
///   2. <see cref="MapRoutes"/>        — map Minimal API endpoints under <c>/plugins/{Id}</c>
///
/// Hooks (<see cref="Hooks.IRequestTranslationHook"/>, etc.) and event handlers
/// must be registered as DI services in <see cref="RegisterServices"/> so the
/// pipeline can resolve them.
public interface IAdaptiveApiPlugin
{
    PluginManifest Manifest { get; }

    /// Register all DI services the plugin needs. Called once during host build,
    /// before the container is built. The plugin's hooks are picked up from DI
    /// by the pipeline dispatcher — there is no separate registration step.
    void RegisterServices(IServiceCollection services);

    /// Map plugin endpoints onto a route group already scoped to
    /// <c>/plugins/{Manifest.Id}</c>. No-op for plugins that add no endpoints.
    void MapRoutes(IEndpointRouteBuilder routes);
}
