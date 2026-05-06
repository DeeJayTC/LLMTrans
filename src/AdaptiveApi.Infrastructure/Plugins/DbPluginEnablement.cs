using AdaptiveApi.Core.Plugins;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Infrastructure.Plugins;

/// Scoped EF-backed lookup. Loads the disabled-set once on first call within
/// the request scope so subsequent hook invocations are cache-hits.
public sealed class DbPluginEnablement : IPluginEnablement
{
    private const string GlobalTenantId = "*";
    private readonly AdaptiveApiDbContext _db;
    private HashSet<string>? _disabled;

    public DbPluginEnablement(AdaptiveApiDbContext db) => _db = db;

    public bool IsEnabled(string pluginId)
    {
        var disabled = _disabled ??= LoadDisabled();
        return !disabled.Contains(pluginId);
    }

    private HashSet<string> LoadDisabled()
    {
        // Synchronous read is intentional — `IsEnabled` is called from inside
        // hook dispatch, where suspending isn't free. The query is a single
        // indexed scan over a tiny table (one row per plugin), and EF caches
        // the compiled query across requests.
        return _db.PluginSettings.AsNoTracking()
            .Where(x => x.TenantId == GlobalTenantId && !x.Enabled)
            .Select(x => x.PluginId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
