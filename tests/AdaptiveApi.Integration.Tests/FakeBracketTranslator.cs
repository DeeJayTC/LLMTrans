using AdaptiveApi.Core.Abstractions;

namespace AdaptiveApi.Integration.Tests;

/// Deterministic fake: wraps translatable text (outside placeholder tags) in «…»
/// so tests can assert "translation happened" without depending on a real service.
public sealed class FakeBracketTranslator : ITranslator
{
    public string TranslatorId => "fake-brackets";
    public TranslatorCapabilities Capabilities => TranslatorCapabilities.TagHandling | TranslatorCapabilities.Batching;

    public Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
        IReadOnlyList<TranslationRequest> requests, CancellationToken ct)
    {
        IReadOnlyList<TranslationResult> r = requests
            .Select(req => new TranslationResult(WrapOutsideTags(req.Text, req.Target.Value)))
            .ToArray();
        return Task.FromResult(r);
    }

    private static string WrapOutsideTags(string s, string lang)
    {
        var prefix = $"[{lang}]";
        var sb = new System.Text.StringBuilder();
        sb.Append(prefix);
        var i = 0;
        while (i < s.Length)
        {
            var tagStart = s.IndexOf("<adaptiveapi ", i, StringComparison.Ordinal);
            if (tagStart < 0)
            {
                sb.Append(s[i..]);
                break;
            }
            sb.Append(s[i..tagStart]);
            var tagEnd = s.IndexOf("/>", tagStart, StringComparison.Ordinal) + 2;
            sb.Append(s[tagStart..tagEnd]);
            i = tagEnd;
        }
        return sb.ToString();
    }
}
