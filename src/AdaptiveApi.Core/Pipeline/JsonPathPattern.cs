namespace AdaptiveApi.Core.Pipeline;

/// Minimal JSON-path-like pattern: slash-separated segments; "*" matches any key or index;
/// "**" matches any number of segments (including zero). Examples:
///   /messages/*/content
///   /messages/*/content/*/text
///   /tools/*/function/description
public sealed class JsonPathPattern
{
    private readonly string[] _segments;

    public string Pattern { get; }

    public JsonPathPattern(string pattern)
    {
        Pattern = pattern;
        _segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    public bool Matches(ReadOnlySpan<string> path)
    {
        return MatchSegments(_segments, 0, path, 0);
    }

    private static bool MatchSegments(string[] pat, int pi, ReadOnlySpan<string> path, int xi)
    {
        while (pi < pat.Length)
        {
            var seg = pat[pi];
            if (seg == "**")
            {
                if (pi == pat.Length - 1) return true;
                for (var k = xi; k <= path.Length; k++)
                    if (MatchSegments(pat, pi + 1, path, k)) return true;
                return false;
            }

            if (xi >= path.Length) return false;
            if (seg != "*" && seg != path[xi]) return false;

            pi++;
            xi++;
        }
        return xi == path.Length;
    }
}

public sealed class Allowlist
{
    private readonly JsonPathPattern[] _patterns;

    public Allowlist(params string[] patterns)
    {
        _patterns = patterns.Select(p => new JsonPathPattern(p)).ToArray();
    }

    public bool IsAllowed(ReadOnlySpan<string> path)
    {
        foreach (var p in _patterns)
            if (p.Matches(path)) return true;
        return false;
    }
}
