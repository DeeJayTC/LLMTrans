using System.Text.RegularExpressions;

namespace LlmTrans.Core.Pipeline;

/// Replaces common PII patterns with `<llmtrans id="PII_KIND_n"/>` tags so the upstream LLM
/// sees a stable substitute (e.g. `[redacted-email]`) instead of the original value.
///
/// The returned placeholder list uses the same `Placeholder(Id, Original)` shape as the
/// tokenizer, with `Original` set to the REPLACEMENT string — not the source PII — so
/// post-translation reinjection yields a redacted but natural-reading sentence rather
/// than restoring the sensitive value.
///
/// Detection is regex-based (not ML); add a Presidio-powered detector alongside this for
/// higher recall in the future. Patterns covered: email, phone (E.164 + common formats),
/// US SSN, generic IBAN, credit cards (with Luhn check), IPv4.
public static class PiiRedactor
{
    public sealed record Result(string Text, IReadOnlyList<Placeholder> Redactions);

    private const string TagPrefix = "llmtrans";

    // Detector ORDER matters: broader numeric patterns (phone) must run AFTER
    // narrow-and-validated ones (credit card, SSN, IBAN) so the Luhn-checked credit
    // card isn't eaten by the phone matcher.
    private static readonly (string Kind, string Replacement, Regex Regex)[] Detectors =
    {
        ("EMAIL", "[redacted-email]",
            new Regex(@"\b[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("CREDIT_CARD", "[redacted-card]",
            new Regex(@"\b(?:\d[ -]?){13,19}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("SSN", "[redacted-ssn]",
            new Regex(@"\b\d{3}-\d{2}-\d{4}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("IBAN", "[redacted-iban]",
            new Regex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{10,30}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("IPV4", "[redacted-ip]",
            new Regex(@"\b(?:(?:25[0-5]|2[0-4]\d|[01]?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|[01]?\d?\d)\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),
        ("PHONE", "[redacted-phone]",
            new Regex(@"\+?\d[\d\s().-]{8,}\d",
                RegexOptions.Compiled | RegexOptions.CultureInvariant)),
    };

    public static Result Redact(string input)
    {
        if (string.IsNullOrEmpty(input))
            return new Result(input, Array.Empty<Placeholder>());

        var redactions = new List<Placeholder>();
        var working = input;

        foreach (var (kind, replacement, regex) in Detectors)
        {
            working = regex.Replace(working, m =>
            {
                if (kind == "CREDIT_CARD" && !LuhnValid(m.Value)) return m.Value;
                var id = $"PII_{kind}_{redactions.Count}";
                redactions.Add(new Placeholder(id, replacement));
                return $"<{TagPrefix} id=\"{id}\"/>";
            });
        }

        return new Result(working, redactions);
    }

    private static bool LuhnValid(string candidate)
    {
        var digits = 0;
        var sum = 0;
        var even = false;
        for (var i = candidate.Length - 1; i >= 0; i--)
        {
            var c = candidate[i];
            if (!char.IsDigit(c)) continue;
            var d = c - '0';
            if (even)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            even = !even;
            digits++;
        }
        return digits >= 12 && sum % 10 == 0;
    }
}
