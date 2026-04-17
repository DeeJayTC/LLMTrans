using System.Text;
using LlmTrans.Core.Pipeline;

namespace LlmTrans.Core.Tests;

public sealed class PiiRedactorTests
{
    [Fact]
    public void Redacts_email_to_opaque_substitute()
    {
        var r = PiiRedactor.Redact("Email alice@example.com for details.");
        Assert.DoesNotContain("alice@example.com", r.Text);
        Assert.Contains("<llmtrans id=\"PII_EMAIL_0\"/>", r.Text);
        var ph = Assert.Single(r.Redactions);
        Assert.Equal("PII_EMAIL_0", ph.Id);
        Assert.Equal("[redacted-email]", ph.Original);
    }

    [Fact]
    public void Redacts_ssn_and_phone_together()
    {
        var r = PiiRedactor.Redact("SSN 123-45-6789 and call +1 555-867-5309 today.");
        Assert.DoesNotContain("123-45-6789", r.Text);
        Assert.Contains(r.Redactions, p => p.Id.StartsWith("PII_SSN_"));
        Assert.Contains(r.Redactions, p => p.Id.StartsWith("PII_PHONE_"));
    }

    [Fact]
    public void Luhn_invalid_credit_card_is_not_redacted_as_card()
    {
        // 1234 5678 9012 3456 fails Luhn → must not be tagged as CREDIT_CARD.
        // (It may still be captured by the broader phone detector; that's the right
        //  posture — any long digit string is treated as sensitive by default.)
        var r = PiiRedactor.Redact("Ref 1234 5678 9012 3456 is our number.");
        Assert.DoesNotContain(r.Redactions, p => p.Id.StartsWith("PII_CREDIT_CARD"));
    }

    [Fact]
    public void Luhn_valid_credit_card_is_redacted()
    {
        // 4242 4242 4242 4242 is a Stripe test card (passes Luhn).
        var r = PiiRedactor.Redact("Card 4242 4242 4242 4242 on file.");
        Assert.Contains(r.Redactions, p => p.Id.StartsWith("PII_CREDIT_CARD") && p.Original == "[redacted-card]");
    }

    [Fact]
    public void Text_without_pii_passes_through()
    {
        const string clean = "Hello world, this is clean text.";
        var r = PiiRedactor.Redact(clean);
        Assert.Equal(clean, r.Text);
        Assert.Empty(r.Redactions);
    }

    [Fact]
    public void Round_trip_produces_substitute_not_original()
    {
        // Full request-pipeline flow: redact → tokenize → tag-preserving "translate" → reinject.
        var r = PiiRedactor.Redact("Email alice@example.com for info.");
        var tok = PlaceholderTokenizer.Tokenize(r.Text);
        var combined = tok.Placeholders.Concat(r.Redactions).ToList();

        // Real DeepL/LLM translators preserve `<llmtrans ... />` tags verbatim; simulate that.
        var translated = UppercaseOutsideTags(tok.Text);
        Assert.DoesNotContain("ALICE@EXAMPLE.COM", translated);
        Assert.Contains("<llmtrans id=\"PII_EMAIL_0\"/>", translated);

        var finalText = PlaceholderTokenizer.Reinject(translated, combined);
        Assert.Contains("[redacted-email]", finalText);
        Assert.DoesNotContain("alice@example.com", finalText);
    }

    private static string UppercaseOutsideTags(string s)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < s.Length)
        {
            var tagStart = s.IndexOf("<llmtrans ", i, StringComparison.Ordinal);
            if (tagStart < 0) { sb.Append(s[i..].ToUpperInvariant()); break; }
            sb.Append(s[i..tagStart].ToUpperInvariant());
            var tagEnd = s.IndexOf("/>", tagStart, StringComparison.Ordinal) + 2;
            sb.Append(s[tagStart..tagEnd]);
            i = tagEnd;
        }
        return sb.ToString();
    }
}
