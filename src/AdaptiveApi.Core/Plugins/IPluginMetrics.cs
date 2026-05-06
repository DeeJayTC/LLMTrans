using System.Collections.Concurrent;

namespace AdaptiveApi.Core.Plugins;

/// In-memory per-plugin metrics. The dispatcher records one entry per hook
/// invocation; the admin surface reads aggregate counts back. Singleton
/// because metrics aggregate across requests.
///
/// This is process-local — there's no Prometheus / OTLP integration here
/// yet. Real observability still goes through the existing telemetry
/// pipeline; this exists so the Plugins admin page can show "plugin X added
/// 200ms" without standing up Grafana.
public interface IPluginMetrics
{
    void Record(string pluginId, string hookKind, double elapsedMs, bool failed);

    IReadOnlyDictionary<string, PluginMetricsSnapshot> Snapshot();
}

public sealed record PluginMetricsSnapshot(
    string PluginId, long Calls, long Failures,
    double TotalMs, double AvgMs, double LastMs);

public sealed class InMemoryPluginMetrics : IPluginMetrics
{
    private sealed class Bucket
    {
        public long Calls;
        public long Failures;
        public double TotalMs;
        public double LastMs;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string pluginId, string hookKind, double elapsedMs, bool failed)
    {
        var b = _buckets.GetOrAdd(pluginId, _ => new Bucket());
        Interlocked.Increment(ref b.Calls);
        if (failed) Interlocked.Increment(ref b.Failures);
        // Concurrent doubles need lock-free atomics; CAS loop on TotalMs.
        double initial, computed;
        do
        {
            initial = b.TotalMs;
            computed = initial + elapsedMs;
        } while (Interlocked.CompareExchange(ref b.TotalMs, computed, initial) != initial);
        b.LastMs = elapsedMs;
    }

    public IReadOnlyDictionary<string, PluginMetricsSnapshot> Snapshot()
    {
        var result = new Dictionary<string, PluginMetricsSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _buckets)
        {
            var b = kv.Value;
            var calls = b.Calls;
            var avg = calls > 0 ? b.TotalMs / calls : 0;
            result[kv.Key] = new PluginMetricsSnapshot(kv.Key, calls, b.Failures, b.TotalMs, avg, b.LastMs);
        }
        return result;
    }
}
