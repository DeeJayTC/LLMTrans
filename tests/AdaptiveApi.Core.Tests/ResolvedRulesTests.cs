using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Core.Rules;

namespace AdaptiveApi.Core.Tests;

public sealed class ResolvedRulesTests
{
    private static ResolvedRules Make(IReadOnlyList<GlossaryTerm> glossary, IReadOnlyList<string>? customInstructions = null)
    {
        var style = new StyleBinding(null, null, customInstructions ?? Array.Empty<string>());
        return new ResolvedRules(
            Glossary: glossary,
            DeeplGlossaryId: null,
            RequestStyle: style,
            ResponseStyle: style,
            RequestAllowlist: AllowlistCatalog.Empty,
            ResponseAllowlist: AllowlistCatalog.Empty,
            ToolArgsDenylist: ToolArgsDenylist.Default,
            Formality: AdaptiveApi.Core.Abstractions.Formality.Default);
    }

    private static StyleBinding StyleFrom(ResolvedRules r) => r.RequestStyle;

    [Fact]
    public void Do_not_translate_terms_filter_by_language_pair()
    {
        var rules = Make(new[]
        {
            new GlossaryTerm("en", "de", "DeepL", "DeepL", DoNotTranslate: true, CaseSensitive: false),
            new GlossaryTerm("en", "fr", "DeepL", "DeepL", DoNotTranslate: true, CaseSensitive: false),
            new GlossaryTerm("en", "de", "cart", "Warenkorb", DoNotTranslate: false, CaseSensitive: false),
        });

        var terms = rules.DoNotTranslateFor("en", "de");
        Assert.Single(terms);
        Assert.Contains("DeepL", terms);
    }

    [Fact]
    public void System_instructions_include_translation_mappings()
    {
        var rules = Make(
            glossary: new[]
            {
                new GlossaryTerm("en", "de", "cart", "Warenkorb", DoNotTranslate: false, CaseSensitive: false),
                new GlossaryTerm("en", "de", "checkout", "Kasse", DoNotTranslate: false, CaseSensitive: false),
            },
            customInstructions: new[] { "Use business-formal register throughout." });

        var instructions = rules.SystemInstructionsFor(StyleFrom(rules), "en", "de");
        Assert.Equal(3, instructions.Count);
        Assert.Contains("business-formal", instructions[0]);
        Assert.Contains(instructions, s => s.Contains("\"cart\"") && s.Contains("\"Warenkorb\""));
        Assert.Contains(instructions, s => s.Contains("\"checkout\"") && s.Contains("\"Kasse\""));
    }
}
