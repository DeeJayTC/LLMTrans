using System.Reflection;
using AdaptiveApi.Core.Plugins;
using AdaptiveApi.Plugins.SDK;

namespace AdaptiveApi.Api;

internal static class PluginLoader
{
    /// Result of a discovery pass: legacy host plugins (`IWebPlugin`) and the
    /// new third-party plugin modules (`IAdaptiveApiPlugin`). Both come from
    /// the same scan but have different lifecycles, so the host needs to keep
    /// them separate.
    public sealed record DiscoveredPlugins(
        IReadOnlyList<IWebPlugin> WebPlugins,
        IReadOnlyList<IAdaptiveApiPlugin> Modules);

    /// Scans for plugin assemblies and instantiates every public parameterless
    /// type implementing <see cref="IWebPlugin"/> or
    /// <see cref="IAdaptiveApiPlugin"/>. Sources, in order:
    ///   1. assemblies already resident in <c>AppDomain.CurrentDomain</c> (project references)
    ///   2. <c>AdaptiveApi.*.dll</c> files in the Api's base directory (plugin DLLs dropped next
    ///      to the host at deployment time)
    ///   3. <c>plugins/</c> subfolder (opt-in volume mount)
    public static DiscoveredPlugins Discover(ILogger? log = null)
    {
        var webPlugins = new List<IWebPlugin>();
        var modules = new List<IAdaptiveApiPlugin>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        var seenAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            seenAssemblies.Add(asm.GetName().Name ?? asm.FullName ?? string.Empty);
            CollectFromAssembly(asm, webPlugins, modules, seenTypes, log);
        }

        var baseDir = AppContext.BaseDirectory;
        ScanDirectory(baseDir, "AdaptiveApi.*.dll", webPlugins, modules, seenTypes, seenAssemblies, log);

        var pluginDir = Path.Combine(baseDir, "plugins");
        if (Directory.Exists(pluginDir))
            ScanDirectory(pluginDir, "*.dll", webPlugins, modules, seenTypes, seenAssemblies, log);

        return new DiscoveredPlugins(webPlugins, modules);
    }

    private static void ScanDirectory(
        string directory, string pattern,
        List<IWebPlugin> webPlugins, List<IAdaptiveApiPlugin> modules,
        HashSet<string> seenTypes, HashSet<string> seenAssemblies,
        ILogger? log)
    {
        foreach (var path in Directory.GetFiles(directory, pattern))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!seenAssemblies.Add(name)) continue;

            Assembly asm;
            try { asm = Assembly.LoadFrom(path); }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "failed to load plugin assembly {Path}", path);
                continue;
            }
            CollectFromAssembly(asm, webPlugins, modules, seenTypes, log);
        }
    }

    private static void CollectFromAssembly(
        Assembly assembly,
        List<IWebPlugin> webPlugins, List<IAdaptiveApiPlugin> modules,
        HashSet<string> seenTypes, ILogger? log)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
        catch { return; }

        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || type.IsInterface) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            if (!seenTypes.Add(type.FullName ?? type.Name)) continue;

            if (typeof(IWebPlugin).IsAssignableFrom(type))
            {
                TryCreate<IWebPlugin>(type, log, instance =>
                {
                    webPlugins.Add(instance);
                    log?.LogInformation("loaded host plugin '{Name}' from {Assembly}",
                        instance.Name, assembly.GetName().Name);
                });
            }
            else if (typeof(IAdaptiveApiPlugin).IsAssignableFrom(type))
            {
                TryCreate<IAdaptiveApiPlugin>(type, log, instance =>
                {
                    modules.Add(instance);
                    log?.LogInformation("loaded plugin module '{Id}' v{Version} from {Assembly}",
                        instance.Manifest.Id, instance.Manifest.Version, assembly.GetName().Name);
                });
            }
        }
    }

    private static void TryCreate<T>(Type type, ILogger? log, Action<T> onCreated)
    {
        try
        {
            var instance = (T)Activator.CreateInstance(type)!;
            onCreated(instance);
        }
        catch (Exception ex)
        {
            log?.LogError(ex, "failed to instantiate plugin {Type}", type.FullName);
        }
    }
}
