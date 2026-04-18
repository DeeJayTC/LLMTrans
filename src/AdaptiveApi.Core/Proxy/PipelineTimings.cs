using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace AdaptiveApi.Core.Proxy;

/// Collects per-stage durations for a single proxied request. Adapters push stage
/// timings (`plan`, `translate-request`, `upstream`, `translate-response`, …) and
/// the header emitter serialises them as a `Server-Timing` response header before
/// the body starts streaming. The shape matches W3C Server-Timing so tooling like
/// the Chrome DevTools "Timing" panel displays them natively.
public sealed class PipelineTimings
{
    private readonly List<Entry> _entries = new();

    public void Record(string name, TimeSpan duration, string? description = null)
    {
        _entries.Add(new Entry(name, (long)duration.TotalMilliseconds, description));
    }

    public IReadOnlyList<Entry> Entries => _entries;

    public string BuildHeader()
    {
        if (_entries.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        for (var i = 0; i < _entries.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var e = _entries[i];
            sb.Append(SanitizeToken(e.Name));
            if (!string.IsNullOrEmpty(e.Description))
                sb.Append(";desc=").Append(Quote(e.Description));
            sb.Append(";dur=").Append(e.DurationMs.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// Attaches the header to `context.Response.Headers` so `OnStarting` can flush it.
    public void WriteTo(HttpContext context)
    {
        if (_entries.Count == 0) return;
        context.Response.Headers["Server-Timing"] = BuildHeader();
    }

    private static string SanitizeToken(string name)
    {
        // Server-Timing names are RFC 7230 tokens: no spaces or special chars.
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
        return sb.Length == 0 ? "step" : sb.ToString();
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";

    public readonly record struct Entry(string Name, long DurationMs, string? Description);
}
