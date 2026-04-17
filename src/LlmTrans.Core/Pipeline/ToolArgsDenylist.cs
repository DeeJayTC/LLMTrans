using System.Text.RegularExpressions;

namespace LlmTrans.Core.Pipeline;

/// Keys under which string values must NEVER be translated (ids, urls, codes, etc).
/// Default set from §6.1; tenants may extend through proxy rules in M5.
public sealed class ToolArgsDenylist
{
    private readonly HashSet<string> _exactKeys;
    private readonly Regex[] _keyPatterns;

    public static readonly ToolArgsDenylist Default = new(
        exactKeys: new[]
        {
            "id", "uuid", "email", "slug", "key", "locale", "tz",
            "url", "href", "path"
        },
        keyPatterns: new[]
        {
            new Regex("^.*_id$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex("^.*_code$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex("^.*Id$", RegexOptions.Compiled),
            new Regex("^.*Code$", RegexOptions.Compiled),
        });

    public ToolArgsDenylist(IEnumerable<string> exactKeys, IEnumerable<Regex> keyPatterns)
    {
        _exactKeys = new HashSet<string>(exactKeys, StringComparer.OrdinalIgnoreCase);
        _keyPatterns = keyPatterns.ToArray();
    }

    public bool IsDenied(string key)
    {
        if (_exactKeys.Contains(key)) return true;
        foreach (var p in _keyPatterns)
            if (p.IsMatch(key)) return true;
        return false;
    }
}
