using System.Text.Json.Nodes;

namespace AdaptiveApi.Core.Pipeline;

public sealed class TranslationSite
{
    public required string PathExpression { get; init; }
    public required string Source { get; init; }
    public required Action<string> Apply { get; init; }
}

public static class JsonTranslationPlanner
{
    public static List<TranslationSite> Plan(JsonNode? root, Allowlist allowlist)
    {
        var sites = new List<TranslationSite>();
        if (root is null) return sites;
        var path = new List<string>();
        Walk(root, path, allowlist, sites);
        return sites;
    }

    private static void Walk(JsonNode? node, List<string> path, Allowlist allowlist, List<TranslationSite> sites)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj.ToList())
                {
                    path.Add(kv.Key);
                    if (kv.Value is JsonValue val && val.TryGetValue<string>(out var s))
                    {
                        if (allowlist.IsAllowed(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(path)))
                        {
                            var key = kv.Key;
                            sites.Add(new TranslationSite
                            {
                                PathExpression = "/" + string.Join('/', path),
                                Source = s,
                                Apply = newValue => obj[key] = newValue,
                            });
                        }
                    }
                    else
                    {
                        Walk(kv.Value, path, allowlist, sites);
                    }
                    path.RemoveAt(path.Count - 1);
                }
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    path.Add(i.ToString());
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        if (allowlist.IsAllowed(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(path)))
                        {
                            var idx = i;
                            sites.Add(new TranslationSite
                            {
                                PathExpression = "/" + string.Join('/', path),
                                Source = s,
                                Apply = newValue => arr[idx] = newValue,
                            });
                        }
                    }
                    else
                    {
                        Walk(arr[i], path, allowlist, sites);
                    }
                    path.RemoveAt(path.Count - 1);
                }
                break;
        }
    }
}
