using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveApi.Core.Plugins;

public static class PluginServiceCollectionExtensions
{
    /// Register the plugin hook dispatcher. Plugin authors register their own
    /// hook implementations via DI in their <c>IAdaptiveApiPlugin.RegisterServices</c>;
    /// the dispatcher resolves them via <c>IEnumerable&lt;THook&gt;</c>.
    public static IServiceCollection AddAdaptiveApiPluginHooks(this IServiceCollection services)
    {
        services.AddSingleton<IPluginHookDispatcher, PluginHookDispatcher>();
        return services;
    }
}
