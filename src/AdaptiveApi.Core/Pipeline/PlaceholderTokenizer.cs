using System.Text;
using System.Text.RegularExpressions;

namespace AdaptiveApi.Core.Pipeline;

public static class PlaceholderTokenizer
{
    public const string Tag = "adaptiveapi";

    private static readonly (string Name, Regex Pattern)[] Patterns = BuildPatterns();

    public static TokenizedText Tokenize(string input, IEnumerable<string>? doNotTranslateTerms = null)
    {
        if (string.IsNullOrEmpty(input))
            return new TokenizedText(input, Array.Empty<Placeholder>());

        var placeholders = new List<Placeholder>();
        var working = input;

        foreach (var (_, pattern) in Patterns)
        {
            working = pattern.Replace(working, m =>
            {
                var id = $"TAG_{placeholders.Count}";
                placeholders.Add(new Placeholder(id, m.Value));
                return $"<{Tag} id=\"{id}\"/>";
            });
        }

        if (doNotTranslateTerms is not null)
        {
            foreach (var term in doNotTranslateTerms.Where(t => !string.IsNullOrEmpty(t)))
            {
                var rx = new Regex("\\b" + Regex.Escape(term) + "\\b", RegexOptions.CultureInvariant);
                working = rx.Replace(working, m =>
                {
                    var id = $"TAG_{placeholders.Count}";
                    placeholders.Add(new Placeholder(id, m.Value));
                    return $"<{Tag} id=\"{id}\"/>";
                });
            }
        }

        return new TokenizedText(working, placeholders);
    }

    public static string Reinject(string translated, IReadOnlyList<Placeholder> placeholders)
    {
        if (placeholders.Count == 0) return translated;

        var sb = new StringBuilder(translated.Length + placeholders.Sum(p => p.Original.Length));
        var i = 0;
        while (i < translated.Length)
        {
            var start = translated.IndexOf($"<{Tag} ", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(translated, i, translated.Length - i);
                break;
            }

            sb.Append(translated, i, start - i);
            var end = translated.IndexOf("/>", start, StringComparison.Ordinal);
            if (end < 0)
            {
                sb.Append(translated, start, translated.Length - start);
                break;
            }

            var tagSpan = translated.AsSpan(start, end - start + 2);
            var idStart = tagSpan.IndexOf("id=\"".AsSpan()) + 4;
            var idEnd = tagSpan[idStart..].IndexOf('"') + idStart;
            var id = tagSpan[idStart..idEnd].ToString();

            var ph = placeholders.FirstOrDefault(p => p.Id == id);
            sb.Append(ph?.Original ?? tagSpan.ToString());

            i = end + 2;
        }
        return sb.ToString();
    }

    private static (string, Regex)[] BuildPatterns()
    {
        const RegexOptions opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        return
        [
            ("code_fence",     new Regex(@"```[\s\S]*?```", opts)),
            ("inline_code",    new Regex(@"`[^`\n]+`", opts)),
            ("url",            new Regex(@"https?://[^\s<>""']+", opts)),
            ("email",          new Regex(@"\b[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+\b", opts)),
            ("win_path",       new Regex(@"\b[A-Za-z]:[\\/](?:[^\s""<>|]+)", opts)),
            ("unix_path",      new Regex(@"(?<![\w.])/[A-Za-z0-9_./\-]+", opts)),
            ("handlebars",     new Regex(@"\{\{[^}]+\}\}", opts)),
            ("dollar_tmpl",    new Regex(@"\$\{[^}]+\}", opts)),
            ("erb_tmpl",       new Regex(@"<%[-=]?\s*[\s\S]*?\s*%>", opts)),
            ("jinja_tmpl",     new Regex(@"\{%[-]?\s*[\s\S]*?\s*[-]?%\}", opts)),
            ("dotted_ident",   new Regex(@"\b[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*){1,}\b", opts)),
        ];
    }
}

public sealed record TokenizedText(string Text, IReadOnlyList<Placeholder> Placeholders);

public sealed record Placeholder(string Id, string Original);
