using LlmTrans.Core.Pipeline;

namespace LlmTrans.Providers.Generic;

/// Converts user-friendly JSONPath-like expressions into the internal slash path
/// pattern the planner understands.
///   `$.messages[*].content`              → `/messages/*/content`
///   `$.tool_calls[*].parameters.*`       → `/tool_calls/*/parameters/*`
///   `$.nested..field` (descendant)       → `/nested/**/field`
///   `$`                                   → empty (matches nothing)
public static class JsonPathConverter
{
    public static Allowlist ToAllowlist(IEnumerable<string> jsonPaths)
    {
        var patterns = new List<string>();
        foreach (var raw in jsonPaths)
        {
            var p = ToSlashPattern(raw);
            if (!string.IsNullOrEmpty(p)) patterns.Add(p);
        }
        return new Allowlist(patterns.ToArray());
    }

    public static string ToSlashPattern(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath)) return string.Empty;

        var s = jsonPath.Trim();
        if (s.StartsWith("$", StringComparison.Ordinal)) s = s[1..];

        // Handle `..` (recursive descent) before `.`
        s = s.Replace("..", "/**/");

        // `[*]` and `[<index>]` → `/*` (indexed access not distinguished from wildcard).
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[\s*\*\s*\]", "/*");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[\s*\d+\s*\]", "/*");

        // `.` → `/`
        s = s.Replace('.', '/');

        // Collapse accidental double slashes (but preserve `//**//` as `/**/`).
        while (s.Contains("///")) s = s.Replace("///", "//");

        if (!s.StartsWith('/')) s = "/" + s;
        return s.TrimEnd('/');
    }
}
