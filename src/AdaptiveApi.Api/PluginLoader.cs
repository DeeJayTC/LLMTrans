using System.Reflection;
using AdaptiveApi.Core.Plugins;

namespace AdaptiveApi.Api;

internal static class PluginLoader
{
    /// Scans for plugin assemblies and instantiates every public parameterless type
    /// implementing `IWebPlugin`. Sources, in order:
    ///   1. assemblies already resident in `AppDomain.CurrentDomain` (project references)
    ///   2. `AdaptiveApi.*.dll` files in the Api's base directory (plugin DLLs dropped next
    ///      to the host at deployment time)
    ///   3. `plugins/` subfolder (opt-in volume mount)
    public static IReadOnlyList<IWebPlugin> Discover(ILogger? log = null)
    {
        var plugins = new List<IWebPlugin>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        var seenAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            seenAssemblies.Add(asm.GetName().Name ?? asm.FullName ?? string.Empty);
            CollectFromAssembly(asm, plugins, seenTypes, log);
        }

        var baseDir = AppContext.BaseDirectory;
        ScanDirectory(baseDir, "AdaptiveApi.*.dll", plugins, seenTypes, seenAssemblies, log);

        var pluginDir = Path.Combine(baseDir, "plugins");
        if (Directory.Exists(pluginDir))
            ScanDirectory(pluginDir, "*.dll", plugins, seenTypes, seenAssemblies, log);

        return plugins;
    }

    private static void ScanDirectory(
        string directory, string pattern,
        List<IWebPlugin> plugins, HashSet<string> seenTypes, HashSet<string> seenAssemblies,
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
            CollectFromAssembly(asm, plugins, seenTypes, log);
        }
    }

    private static void CollectFromAssembly(
        Assembly assembly, List<IWebPlugin> plugins, HashSet<string> seenTypes, ILogger? log)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
        catch { return; }

        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || type.IsInterface) continue;
            if (!typeof(IWebPlugin).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;
            if (!seenTypes.Add(type.FullName ?? type.Name)) continue;

            try
            {
                var instance = (IWebPlugin)Activator.CreateInstance(type)!;
                plugins.Add(instance);
                log?.LogInformation("loaded plugin '{Name}' from {Assembly}",
                    instance.Name, assembly.GetName().Name);
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "failed to instantiate plugin {Type}", type.FullName);
            }
        }
    }
}
