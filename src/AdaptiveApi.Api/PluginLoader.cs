using System.Reflection;
using AdaptiveApi.Core.Plugins;
using AdaptiveApi.Plugins.SDK;

namespace AdaptiveApi.Api;

internal static class PluginLoader
{
    /// Result of a discovery pass: legacy host plugins (`IWebPlugin`), the
    /// new third-party plugin modules (`IAdaptiveApiPlugin`) that survived
    /// dependency / dedupe checks, and the rejected modules with reasons
    /// for the admin UI to surface.
    public sealed record DiscoveredPlugins(
        IReadOnlyList<IWebPlugin> WebPlugins,
        IReadOnlyList<IAdaptiveApiPlugin> Modules,
        IReadOnlyList<DisabledPluginEntry> Disabled);

    /// Scans for plugin assemblies and instantiates every public parameterless
    /// type implementing <see cref="IWebPlugin"/> or
    /// <see cref="IAdaptiveApiPlugin"/>. Sources, in order:
    ///   1. assemblies already resident in <c>AppDomain.CurrentDomain</c> (project references)
    ///   2. <c>AdaptiveApi.*.dll</c> files in the Api's base directory (plugin DLLs dropped next
    ///      to the host at deployment time)
    ///   3. <c>plugins/</c> subfolder (opt-in volume mount)
    ///
    /// After collection, modules are topo-sorted by <see cref="PluginManifest.Dependencies"/>;
    /// modules with missing dependencies, duplicate ids, or participating in a
    /// dependency cycle are dropped from the runtime list and reported via
    /// <see cref="DiscoveredPlugins.Disabled"/>.
    public static DiscoveredPlugins Discover(ILogger? log = null)
    {
        var webPlugins = new List<IWebPlugin>();
        var modules = new List<IAdaptiveApiPlugin>();
        var disabled = new List<DisabledPluginEntry>();
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

        var (deduped, dupDisabled) = DedupeById(modules, log);
        disabled.AddRange(dupDisabled);

        var (resolved, depDisabled) = ResolveDependencies(deduped, log);
        disabled.AddRange(depDisabled);

        foreach (var m in resolved)
        {
            if (m.Manifest.AllowAnonymousEndpoints)
                log?.LogWarning(
                    "plugin '{Id}' v{Version} declares AllowAnonymousEndpoints — its /plugins/{Id} routes are NOT protected by the admin policy",
                    m.Manifest.Id, m.Manifest.Version, m.Manifest.Id);
        }

        return new DiscoveredPlugins(webPlugins, resolved, disabled);
    }

    private static (List<IAdaptiveApiPlugin> Kept, List<DisabledPluginEntry> Dropped) DedupeById(
        List<IAdaptiveApiPlugin> modules, ILogger? log)
    {
        var kept = new List<IAdaptiveApiPlugin>();
        var dropped = new List<DisabledPluginEntry>();
        var seen = new Dictionary<string, IAdaptiveApiPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules)
        {
            var id = m.Manifest.Id;
            if (seen.TryGetValue(id, out var first))
            {
                log?.LogWarning(
                    "plugin id '{Id}' is registered by multiple assemblies; keeping {KeptType}, dropping {DroppedType}",
                    id, first.GetType().FullName, m.GetType().FullName);
                dropped.Add(new DisabledPluginEntry(id, m.Manifest.Version,
                    $"duplicate id (already provided by {first.GetType().FullName})"));
                continue;
            }
            seen[id] = m;
            kept.Add(m);
        }
        return (kept, dropped);
    }

    /// Topological sort with cycle detection. Modules whose dependencies
    /// cannot be resolved (missing or part of a cycle) are dropped and
    /// returned in the disabled list with a human-readable reason.
    private static (List<IAdaptiveApiPlugin> Resolved, List<DisabledPluginEntry> Disabled)
        ResolveDependencies(List<IAdaptiveApiPlugin> modules, ILogger? log)
    {
        var byId = modules.ToDictionary(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase);
        var disabled = new List<DisabledPluginEntry>();

        // Drop modules with missing dependencies first (transitively — if A
        // depends on a missing B, A is also disabled).
        var alive = new HashSet<string>(byId.Keys, StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (var m in modules)
            {
                if (!alive.Contains(m.Manifest.Id)) continue;
                var missing = m.Manifest.Dependencies
                    .FirstOrDefault(d => !alive.Contains(d));
                if (missing is not null)
                {
                    alive.Remove(m.Manifest.Id);
                    var reason = byId.ContainsKey(missing)
                        ? $"depends on '{missing}' which was itself disabled"
                        : $"missing dependency '{missing}'";
                    log?.LogWarning("plugin '{Id}' v{Version} disabled: {Reason}",
                        m.Manifest.Id, m.Manifest.Version, reason);
                    disabled.Add(new DisabledPluginEntry(m.Manifest.Id, m.Manifest.Version, reason));
                    changed = true;
                }
            }
        } while (changed);

        // Topo-sort remaining modules. Any node still unvisited after the
        // sort is part of a cycle and gets dropped.
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<IAdaptiveApiPlugin>();
        var inCycle = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string id)
        {
            if (visited.Contains(id)) return true;
            if (visiting.Contains(id)) { inCycle.Add(id); return false; }
            if (!byId.TryGetValue(id, out var m) || !alive.Contains(id)) return true;
            visiting.Add(id);
            foreach (var dep in m.Manifest.Dependencies)
            {
                if (!Visit(dep))
                {
                    inCycle.Add(id);
                    visiting.Remove(id);
                    return false;
                }
            }
            visiting.Remove(id);
            visited.Add(id);
            ordered.Add(m);
            return true;
        }

        foreach (var id in byId.Keys.Where(alive.Contains))
            Visit(id);

        foreach (var id in inCycle)
        {
            if (byId.TryGetValue(id, out var m))
            {
                log?.LogError("plugin '{Id}' v{Version} disabled: dependency cycle",
                    m.Manifest.Id, m.Manifest.Version);
                disabled.Add(new DisabledPluginEntry(m.Manifest.Id, m.Manifest.Version,
                    "dependency cycle"));
            }
        }

        return (ordered, disabled);
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
        catch (ReflectionTypeLoadException ex)
        {
            log?.LogWarning(ex,
                "plugin assembly {Assembly} had {Count} type-load errors; continuing with the {Loaded} type(s) that did load",
                assembly.GetName().Name, ex.LoaderExceptions.Length, ex.Types.Count(t => t is not null));
            types = ex.Types.Where(t => t is not null).ToArray()!;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "plugin assembly {Assembly} failed to enumerate types; skipping", assembly.GetName().Name);
            return;
        }

        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || type.IsInterface) continue;
            if (!seenTypes.Add(type.FullName ?? type.Name)) continue;

            var implementsWeb = typeof(IWebPlugin).IsAssignableFrom(type);
            var implementsModule = typeof(IAdaptiveApiPlugin).IsAssignableFrom(type);
            if (!implementsWeb && !implementsModule) continue;

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                // The plugin contract requires a public parameterless ctor —
                // surfacing this loudly avoids "why is my plugin missing?"
                // confusion.
                log?.LogWarning(
                    "type {Type} in {Assembly} implements a plugin interface but has no public parameterless constructor; skipping",
                    type.FullName, assembly.GetName().Name);
                continue;
            }

            if (implementsWeb)
            {
                TryCreate<IWebPlugin>(type, log, instance =>
                {
                    webPlugins.Add(instance);
                    log?.LogInformation("loaded host plugin '{Name}' from {Assembly}",
                        instance.Name, assembly.GetName().Name);
                });
            }
            else if (implementsModule)
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
