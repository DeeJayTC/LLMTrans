namespace AdaptiveApi.Core.Pipeline;

public static class PlaceholderValidator
{
    public sealed record Result(bool Ok, IReadOnlyList<string> MissingIds, IReadOnlyList<string> DuplicateIds);

    public static Result Validate(string translated, IReadOnlyList<Placeholder> sourcePlaceholders)
    {
        if (sourcePlaceholders.Count == 0)
            return new Result(true, Array.Empty<string>(), Array.Empty<string>());

        var missing = new List<string>();
        var dup = new List<string>();

        foreach (var ph in sourcePlaceholders)
        {
            var count = CountOccurrences(translated, $"id=\"{ph.Id}\"");
            if (count == 0) missing.Add(ph.Id);
            else if (count > 1) dup.Add(ph.Id);
        }

        return new Result(missing.Count == 0 && dup.Count == 0, missing, dup);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }
}
