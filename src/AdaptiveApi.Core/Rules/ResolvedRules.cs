using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Pipeline;

namespace AdaptiveApi.Core.Rules;

/// Everything the pipeline needs from glossaries, style rules and proxy rules,
/// pre-compiled at request time. Style rules are split per direction: the request
/// binding covers user → LLM translations (usually normalising English for the
/// model), the response binding covers LLM → user translations (brand voice).
public sealed record ResolvedRules(
    IReadOnlyList<GlossaryTerm> Glossary,
    string? DeeplGlossaryId,
    StyleBinding RequestStyle,
    StyleBinding ResponseStyle,
    Allowlist RequestAllowlist,
    Allowlist ResponseAllowlist,
    ToolArgsDenylist ToolArgsDenylist,
    Formality Formality,
    bool RedactPii = false,
    string? SystemContext = null,
    PiiDetectorSet? PiiDetectors = null);

public sealed record StyleBinding(
    string? StyleRuleId,
    string? DeeplStyleId,
    IReadOnlyList<string> CustomInstructions)
{
    public static StyleBinding Empty { get; } = new(null, null, Array.Empty<string>());
}

public sealed record GlossaryTerm(
    string SourceLanguage,
    string TargetLanguage,
    string SourceTerm,
    string TargetTerm,
    bool DoNotTranslate,
    bool CaseSensitive);

public static class ResolvedRulesExtensions
{
    public static IReadOnlyCollection<string> DoNotTranslateFor(
        this ResolvedRules r, string source, string target)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in r.Glossary)
        {
            if (!entry.DoNotTranslate) continue;
            if (string.Equals(entry.SourceLanguage, source, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.TargetLanguage, target, StringComparison.OrdinalIgnoreCase))
                result.Add(entry.SourceTerm);
        }
        return result;
    }

    public static IReadOnlyList<string> SystemInstructionsFor(
        this ResolvedRules r, StyleBinding style, string source, string target)
    {
        var instructions = new List<string>(style.CustomInstructions);
        var mappings = r.Glossary
            .Where(e => !e.DoNotTranslate
                        && string.Equals(e.SourceLanguage, source, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.TargetLanguage, target, StringComparison.OrdinalIgnoreCase))
            .Take(16)
            .Select(e => $"Translate \"{e.SourceTerm}\" as \"{e.TargetTerm}\".")
            .ToList();
        instructions.AddRange(mappings);
        return instructions;
    }
}
