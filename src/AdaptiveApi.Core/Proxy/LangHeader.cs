using Microsoft.AspNetCore.Http;

namespace AdaptiveApi.Core.Proxy;

/// Parses the combined <c>X-AdaptiveApi-Lang</c> language-pair header.
///
/// Preferred form: <c>X-AdaptiveApi-Lang: de/en-US</c>
/// (user-language / llm-language). Whitespace is trimmed; values are
/// lower-cased to match the route store.
///
/// The legacy split <c>X-AdaptiveApi-Target-Lang</c> /
/// <c>X-AdaptiveApi-Source-Lang</c> headers continue to work and take
/// precedence per side when both are present, so callers can still override
/// just one direction.
public static class LangHeader
{
    private const string Combined = "X-AdaptiveApi-Lang";
    private const string TargetLang = "X-AdaptiveApi-Target-Lang";
    private const string SourceLang = "X-AdaptiveApi-Source-Lang";

    /// Returns the user/llm language overrides found in the request headers,
    /// or <c>(null, null)</c> if none were specified. Per-direction headers
    /// override the combined form on the side they specify.
    public static (string? UserLang, string? LlmLang) Parse(IHeaderDictionary headers)
    {
        string? user = null, llm = null;
        if (headers.TryGetValue(Combined, out var combined) && combined.Count > 0)
        {
            var parts = combined.ToString().Split(new[] { '/', ',', '⇄' }, 2,
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                user = parts[0].ToLowerInvariant();
                llm = parts[1].ToLowerInvariant();
            }
        }
        if (headers.TryGetValue(TargetLang, out var tl) && tl.Count > 0)
            user = tl.ToString().Trim().ToLowerInvariant();
        if (headers.TryGetValue(SourceLang, out var sl) && sl.Count > 0)
            llm = sl.ToString().Trim().ToLowerInvariant();
        return (user, llm);
    }

    /// True when at least one of the lang headers (combined or split) is set.
    /// Used to decide whether to flip Direction=Off into Bidirectional in the
    /// adapters' header-override logic.
    public static bool AnyOverrideSet(IHeaderDictionary headers)
    {
        var (u, l) = Parse(headers);
        return u is not null || l is not null;
    }
}
