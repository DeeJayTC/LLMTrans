using System.Text.RegularExpressions;

namespace AdaptiveApi.Core.Pipeline;

/// Replaces detected PII with `<adaptiveapi id="PII_KIND_n"/>` placeholders so the
/// upstream LLM sees a stable substitute (e.g. `[redacted-email]`) instead of the
/// original value.
///
/// The returned placeholder list uses the same `Placeholder(Id, Original)` shape as the
/// tokenizer, with `Original` set to the REPLACEMENT string (not the source PII), so
/// post-translation reinjection yields a redacted but natural-reading sentence rather
/// than restoring the sensitive value.
///
/// The default detector set covers email, phone (E.164 + common formats), US SSN,
/// IBAN, credit cards (with Luhn check), and IPv4. Custom detector sets are supplied
/// per-route by `DbRuleResolver` after composing premade packs and tenant-defined
/// custom rules.
public static class PiiRedactor
{
    public sealed record Result(string Text, IReadOnlyList<Placeholder> Redactions);

    private const string TagPrefix = "adaptiveapi";

    /// Backwards-compatible entry point using the default detector set.
    public static Result Redact(string input) => Redact(input, PiiDetectorSet.Default);

    /// Redact using an explicit detector set. Detector ORDER matters: callers must
    /// place narrow-and-validated patterns (cards, SSN, IBAN) before broader numeric
    /// patterns (phone) so the validated match isn't eaten.
    public static Result Redact(string input, PiiDetectorSet detectors)
    {
        if (string.IsNullOrEmpty(input) || detectors.Detectors.Count == 0)
            return new Result(input, Array.Empty<Placeholder>());

        var redactions = new List<Placeholder>();
        var working = input;

        foreach (var detector in detectors.Detectors)
        {
            working = detector.Regex.Replace(working, m =>
            {
                if (detector.LuhnValidate && !LuhnValid(m.Value)) return m.Value;
                var id = $"PII_{detector.Kind}_{redactions.Count}";
                redactions.Add(new Placeholder(id, detector.Replacement));
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
