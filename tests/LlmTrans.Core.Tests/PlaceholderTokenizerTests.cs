using LlmTrans.Core.Pipeline;

namespace LlmTrans.Core.Tests;

public sealed class PlaceholderTokenizerTests
{
    [Fact]
    public void Tokenizes_inline_code_url_and_dotted_identifiers()
    {
        const string input = "Format the `user.email` field as a URL like https://example.com/u/42 and explain what `SELECT *` does.";
        var result = PlaceholderTokenizer.Tokenize(input);

        Assert.DoesNotContain("https://", result.Text);
        Assert.DoesNotContain("`user.email`", result.Text);
        Assert.DoesNotContain("`SELECT *`", result.Text);
        Assert.True(result.Placeholders.Count >= 3);

        // Reinjection must restore bytes exactly.
        var restored = PlaceholderTokenizer.Reinject(result.Text, result.Placeholders);
        Assert.Equal(input, restored);
    }

    [Fact]
    public void Preserves_code_fences_across_newlines()
    {
        const string input = "Explain this:\n```python\nx = 1\n```\nclear?";
        var result = PlaceholderTokenizer.Tokenize(input);
        Assert.DoesNotContain("```", result.Text);
        Assert.Equal(input, PlaceholderTokenizer.Reinject(result.Text, result.Placeholders));
    }

    [Fact]
    public void Validator_detects_missing_and_duplicate_tags()
    {
        const string input = "Format `a.b` as URL https://x.y";
        var tok = PlaceholderTokenizer.Tokenize(input);

        var okResult = PlaceholderValidator.Validate(tok.Text, tok.Placeholders);
        Assert.True(okResult.Ok);

        // Simulate translator dropping a tag.
        var broken = tok.Text.Replace($"<llmtrans id=\"{tok.Placeholders[0].Id}\"/>", "");
        var br = PlaceholderValidator.Validate(broken, tok.Placeholders);
        Assert.False(br.Ok);
        Assert.Contains(tok.Placeholders[0].Id, br.MissingIds);

        // Simulate duplication.
        var dupText = tok.Text + $" <llmtrans id=\"{tok.Placeholders[0].Id}\"/>";
        var dup = PlaceholderValidator.Validate(dupText, tok.Placeholders);
        Assert.False(dup.Ok);
        Assert.Contains(tok.Placeholders[0].Id, dup.DuplicateIds);
    }

    [Fact]
    public void Do_not_translate_terms_are_tagged()
    {
        var result = PlaceholderTokenizer.Tokenize(
            "DeepL and MCP are great.",
            new[] { "DeepL", "MCP" });
        Assert.DoesNotContain("DeepL", result.Text);
        Assert.DoesNotContain("MCP", result.Text);
        Assert.Equal("DeepL and MCP are great.",
            PlaceholderTokenizer.Reinject(result.Text, result.Placeholders));
    }
}
