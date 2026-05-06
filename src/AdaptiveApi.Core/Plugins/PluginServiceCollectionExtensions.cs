using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdaptiveApi.Core.Plugins;

public static class PluginServiceCollectionExtensions
{
    /// Register the plugin hook dispatcher. Plugin authors register their own
    /// hook implementations via DI in their <c>IAdaptiveApiPlugin.RegisterServices</c>;
    /// the dispatcher resolves them per request from
    /// <c>HttpContext.RequestServices</c>, so hooks may safely be registered
    /// as <c>Scoped</c>.
    public static IServiceCollection AddAdaptiveApiPluginHooks(this IServiceCollection services)
    {
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.TryAddSingleton<IPluginMetrics, InMemoryPluginMetrics>();
        services.AddSingleton<IPluginHookDispatcher, PluginHookDispatcher>();
        return services;
    }
}
