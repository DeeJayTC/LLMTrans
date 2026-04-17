using System.Text.Json.Nodes;

namespace LlmTrans.Core.Pipeline;

/// Walks a JSON node tree (the parsed inner JSON of a tool-call's `arguments` string)
/// and plans every string leaf whose key is NOT in the denylist.
/// Unlike the allowlist planner, this translates by default.
public static class ToolArgsPlanner
{
    public static List<TranslationSite> Plan(JsonNode? root, ToolArgsDenylist denylist)
    {
        var sites = new List<TranslationSite>();
        if (root is null) return sites;
        Walk(root, parentKey: null, currentPath: new(), denylist, sites);
        return sites;
    }

    private static void Walk(
        JsonNode? node, string? parentKey, List<string> currentPath,
        ToolArgsDenylist denylist, List<TranslationSite> sites)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj.ToList())
                {
                    currentPath.Add(kv.Key);
                    if (kv.Value is JsonValue val && val.TryGetValue<string>(out var s))
                    {
                        if (!denylist.IsDenied(kv.Key))
                        {
                            var key = kv.Key;
                            sites.Add(new TranslationSite
                            {
                                PathExpression = "/" + string.Join('/', currentPath),
                                Source = s,
                                Apply = newValue => obj[key] = newValue,
                            });
                        }
                    }
                    else
                    {
                        Walk(kv.Value, kv.Key, currentPath, denylist, sites);
                    }
                    currentPath.RemoveAt(currentPath.Count - 1);
                }
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    currentPath.Add(i.ToString());
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        // Array string elements inherit the parent's key denylist status.
                        if (parentKey is null || !denylist.IsDenied(parentKey))
                        {
                            var idx = i;
                            sites.Add(new TranslationSite
                            {
                                PathExpression = "/" + string.Join('/', currentPath),
                                Source = s,
                                Apply = newValue => arr[idx] = newValue,
                            });
                        }
                    }
                    else
                    {
                        Walk(arr[i], parentKey, currentPath, denylist, sites);
                    }
                    currentPath.RemoveAt(currentPath.Count - 1);
                }
                break;
        }
    }
}
