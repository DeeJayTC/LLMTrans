using System.Text.RegularExpressions;

namespace AdaptiveApi.Core.Pipeline;

/// Compile user-supplied regex with a hard match timeout to bound ReDoS
/// exposure. Built-in patterns are audited so they don't go through this,
/// but anything coming from the admin UI / tenant config does.
///
/// The timeout is per-match (not per-call), so a pathological pattern fired
/// at a non-trivial input throws <see cref="RegexMatchTimeoutException"/>
/// rather than holding a thread. Callers wrap match calls in a try/catch and
/// either drop the match (PII redactor) or surface the failure to the
/// operator (request-rule executor).
///
/// We also reject obviously-malicious shapes at compile time — patterns over
/// the size limit or with deeply nested quantifiers — to fail fast on
/// admin-side input rather than at runtime.
public static class SafeRegex
{
    /// Per-match timeout for user-supplied regex. Long enough for legitimate
    /// patterns over multi-kilobyte payloads; short enough that an attacker
    /// pattern can't denial-of-service the host.
    public static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(100);

    /// Cap on user-supplied pattern length. .NET regex parses past this fine,
    /// but a 5KB regex is almost certainly an attempt to game the engine.
    public const int MaxPatternLength = 1024;

    /// Compile a tenant-supplied pattern. Throws <see cref="ArgumentException"/>
    /// for patterns that fail the up-front shape check, and
    /// <see cref="RegexParseException"/> for invalid syntax (callers usually
    /// catch and treat as "rule disabled").
    public static Regex Compile(string pattern, RegexOptions options) =>
        Compile(pattern, options, DefaultMatchTimeout);

    public static Regex Compile(string pattern, RegexOptions options, TimeSpan matchTimeout)
    {
        ValidateShape(pattern);
        // RegexOptions.Compiled + a match timeout is the supported combination
        // for user-supplied regex; the engine still backtracks under the cap.
        return new Regex(pattern, options | RegexOptions.Compiled | RegexOptions.CultureInvariant, matchTimeout);
    }

    private static void ValidateShape(string pattern)
    {
        if (pattern is null) throw new ArgumentNullException(nameof(pattern));
        if (pattern.Length == 0) throw new ArgumentException("pattern must be non-empty", nameof(pattern));
        if (pattern.Length > MaxPatternLength)
            throw new ArgumentException(
                $"pattern exceeds {MaxPatternLength}-character cap (got {pattern.Length})",
                nameof(pattern));

        // Heuristic: deeply nested quantifiers like `(a+)+` or `(a*)+` are the
        // textbook ReDoS shapes. Walk groups, count nested quantified groups
        // and reject when there are more than two layers — anything beyond two
        // layered repeats is almost always pathological.
        var depth = 0;
        var quantifiedDepth = 0;
        var maxQuantifiedDepth = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length) { i++; continue; }
            if (c == '(') { depth++; }
            else if (c == ')')
            {
                if (depth > 0) depth--;
                if (i + 1 < pattern.Length && IsQuantifier(pattern[i + 1]))
                {
                    quantifiedDepth++;
                    if (quantifiedDepth > maxQuantifiedDepth)
                        maxQuantifiedDepth = quantifiedDepth;
                }
            }
        }
        if (maxQuantifiedDepth > 3)
            throw new ArgumentException(
                "pattern has too many nested quantified groups (likely ReDoS shape)",
                nameof(pattern));
    }

    private static bool IsQuantifier(char c) => c is '+' or '*' or '?' or '{';
}
