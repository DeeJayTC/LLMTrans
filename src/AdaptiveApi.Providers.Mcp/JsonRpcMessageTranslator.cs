using System.Text.Json;
using System.Text.Json.Nodes;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Model;
using AdaptiveApi.Core.Pipeline;

namespace AdaptiveApi.Providers.Mcp;

public enum McpDirection
{
    /// Message flowing from MCP client toward upstream server (requests).
    ClientToServer,
    /// Message flowing from upstream server back to MCP client (responses + notifications).
    ServerToClient,
}

/// Translates the translatable string leaves inside a single JSON-RPC message in place.
/// The method name + direction pick which allowlist / denylist policy applies.
public static class JsonRpcMessageTranslator
{
    private static readonly Allowlist ToolsCallResult = new(
        "/result/content/*/text",
        "/result/content/*/resource/text");

    private static readonly Allowlist ToolsListResult = new(
        "/result/tools/*/description",
        "/result/tools/*/inputSchema/properties/*/description");

    private static readonly Allowlist PromptsGetResult = new(
        "/result/description",
        "/result/messages/*/content/text");

    private static readonly Allowlist PromptsListResult = new(
        "/result/prompts/*/description");

    private static readonly Allowlist ResourcesListResult = new(
        "/result/resources/*/description",
        "/result/resources/*/name");

    private static readonly Allowlist Empty = new();

    /// Translate `message` in place. Returns number of string leaves translated.
    public static async Task<int> TranslateAsync(
        JsonNode? message,
        McpDirection direction,
        ITranslator translator,
        LanguageCode source,
        LanguageCode target,
        ToolArgsDenylist denylist,
        CancellationToken ct)
    {
        if (message is null) return 0;
        if (source.Value == target.Value) return 0;
        if (message is not JsonObject obj) return 0;

        var method = obj["method"]?.GetValue<string>();
        var hasResult = obj["result"] is not null;
        var hasError = obj["error"] is not null;
        if (hasError) return 0;

        // Requests carry `method`; responses carry `result` (both keyed to the same rpc id).
        // Notifications have `method` but no `id` — same structure as requests for translation purposes.
        var sites = new List<TranslationSite>();

        if (direction == McpDirection.ClientToServer && !string.IsNullOrEmpty(method))
        {
            // tools/call: walk params.arguments as free JSON with denylist-aware key filtering.
            if (method == "tools/call" && obj["params"] is JsonObject paramsObj
                && paramsObj["arguments"] is JsonNode args)
            {
                sites.AddRange(ToolArgsPlanner.Plan(args, denylist));
            }
        }
        else if (direction == McpDirection.ServerToClient && hasResult)
        {
            // Method isn't echoed in responses — but the caller typically sends
            // the original method via a side channel. We sniff by shape instead.
            var allowlist = SniffResponseAllowlist(obj);
            sites.AddRange(JsonTranslationPlanner.Plan(obj, allowlist));

            // Tool-call results may also contain JSON-in-JSON for embedded resources; not MCP-spec yet.
        }

        if (sites.Count == 0) return 0;

        var tokenized = sites.Select(s => PlaceholderTokenizer.Tokenize(s.Source)).ToList();
        var requests = new List<TranslationRequest>(sites.Count);
        for (var i = 0; i < sites.Count; i++)
            requests.Add(new TranslationRequest(tokenized[i].Text, source, target, TagHandling: TagHandling.Xml));

        var results = await translator.TranslateBatchAsync(requests, ct);
        var translated = 0;
        for (var i = 0; i < sites.Count; i++)
        {
            var raw = i < results.Count ? results[i].Text : tokenized[i].Text;
            var validation = PlaceholderValidator.Validate(raw, tokenized[i].Placeholders);
            if (!validation.Ok) continue;
            sites[i].Apply(PlaceholderTokenizer.Reinject(raw, tokenized[i].Placeholders));
            translated++;
        }

        return translated;
    }

    /// Look at the shape of the `result` object to decide which response allowlist applies.
    /// The translator doesn't see the original request, so this is deliberately shape-based.
    private static Allowlist SniffResponseAllowlist(JsonObject message)
    {
        if (message["result"] is not JsonObject result) return Empty;

        if (result["tools"] is JsonArray) return ToolsListResult;
        if (result["prompts"] is JsonArray) return PromptsListResult;
        if (result["resources"] is JsonArray) return ResourcesListResult;
        if (result["content"] is JsonArray) return ToolsCallResult;
        if (result["messages"] is JsonArray) return PromptsGetResult;
        return Empty;
    }
}
