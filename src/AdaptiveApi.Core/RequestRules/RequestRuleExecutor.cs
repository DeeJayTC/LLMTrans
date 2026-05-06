using System.Text;
using System.Text.RegularExpressions;
using AdaptiveApi.Core.Pipeline;

namespace AdaptiveApi.Core.RequestRules;

/// Pure-function executor: takes a payload + a list of compiled rules and
/// returns the (possibly modified, possibly blocked) result. Has no
/// dependency on EF or HttpContext; the integration is the hook layer that
/// loads rules from the DB and passes payloads in.
public static class RequestRuleExecutor
{
    /// Run rules whose scope matches <paramref name="bodyScope"/> against the
    /// UTF-8 string view of <paramref name="bodyBytes"/>. Returns a fresh
    /// <see cref="RequestRuleResult"/> describing the cumulative effect.
    public static RequestRuleResult ApplyToBody(
        IReadOnlyList<CompiledRequestRule> rules,
        RequestRuleScope bodyScope,
        byte[] bodyBytes)
    {
        if (rules.Count == 0 || bodyBytes.Length == 0)
            return new RequestRuleResult(false, null, null, null, null, null);

        var current = Encoding.UTF8.GetString(bodyBytes);
        var modified = false;
        Dictionary<string, string>? headersToSet = null;

        foreach (var rule in rules)
        {
            if (rule.Scope != bodyScope) continue;
            if (!TryMatch(rule.Match, current, out var matched)) continue;
            if (!matched) continue;

            switch (rule.Action)
            {
                case RequestRuleAction.Block:
                    return new RequestRuleResult(
                        Blocked: true,
                        BlockStatus: rule.BlockStatus ?? 403,
                        BlockBody: rule.ActionPayload ?? $"{{\"error\":\"blocked by rule '{rule.Name}'\"}}",
                        BlockContentType: "application/json",
                        ModifiedBody: null,
                        HeadersToSet: null);

                case RequestRuleAction.Replace:
                    if (rule.ActionPayload is { } replacement)
                    {
                        try
                        {
                            current = rule.Match.Replace(current, replacement);
                            modified = true;
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            // Skip a rule whose replace pass overran the
                            // timeout — the request continues with the body
                            // it had before this rule fired.
                        }
                    }
                    break;

                case RequestRuleAction.SetHeader:
                    if (!string.IsNullOrEmpty(rule.ActionHeader))
                    {
                        headersToSet ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        headersToSet[rule.ActionHeader!] = rule.ActionPayload ?? string.Empty;
                    }
                    break;

                case RequestRuleAction.Log:
                    // Logging happens in the hook layer (it owns ILogger); the
                    // executor stays pure. The hook checks each rule's match
                    // separately for the log path.
                    break;
            }
        }

        var bytes = modified ? Encoding.UTF8.GetBytes(current) : null;
        return new RequestRuleResult(false, null, null, null, bytes, headersToSet);
    }

    /// True if any rule with <see cref="RequestRuleAction.Log"/> matches the
    /// payload. Surfaces the matching rules so the hook can emit one log
    /// entry per match.
    public static IEnumerable<CompiledRequestRule> CollectLogMatches(
        IReadOnlyList<CompiledRequestRule> rules,
        RequestRuleScope scope,
        string payload)
    {
        foreach (var rule in rules)
        {
            if (rule.Scope != scope) continue;
            if (rule.Action != RequestRuleAction.Log) continue;
            if (TryMatch(rule.Match, payload, out var matched) && matched)
                yield return rule;
        }
    }

    /// Apply header / path scope rules. Returns block info if a Block rule
    /// fires, or a header-set map if any SetHeader rules fire.
    public static RequestRuleResult ApplyToString(
        IReadOnlyList<CompiledRequestRule> rules,
        RequestRuleScope scope,
        string value)
    {
        Dictionary<string, string>? headersToSet = null;
        foreach (var rule in rules)
        {
            if (rule.Scope != scope) continue;
            if (!TryMatch(rule.Match, value, out var matched) || !matched) continue;

            if (rule.Action == RequestRuleAction.Block)
            {
                return new RequestRuleResult(
                    Blocked: true,
                    BlockStatus: rule.BlockStatus ?? 403,
                    BlockBody: rule.ActionPayload ?? $"{{\"error\":\"blocked by rule '{rule.Name}'\"}}",
                    BlockContentType: "application/json",
                    ModifiedBody: null,
                    HeadersToSet: null);
            }
            if (rule.Action == RequestRuleAction.SetHeader && !string.IsNullOrEmpty(rule.ActionHeader))
            {
                headersToSet ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                headersToSet[rule.ActionHeader!] = rule.ActionPayload ?? string.Empty;
            }
        }
        return new RequestRuleResult(false, null, null, null, null, headersToSet);
    }

    private static bool TryMatch(Regex regex, string input, out bool matched)
    {
        try { matched = regex.IsMatch(input); return true; }
        catch (RegexMatchTimeoutException) { matched = false; return false; }
    }
}
