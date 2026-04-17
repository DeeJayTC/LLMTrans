using System.Text.Json;
using System.Text.Json.Nodes;
using LlmTrans.Core.Abstractions;
using LlmTrans.Core.Model;

namespace LlmTrans.Core.Pipeline;

/// Translates the JSON-in-JSON `arguments` strings inside assistant tool calls,
/// plus tool result contents, in place. Called after the main pipeline on both
/// request and response bodies.
public static class ToolCallTranslator
{
    public enum RootShape
    {
        /// OpenAI chat: {"messages":[...]} and optionally {"choices":[{"message":{...}}]}.
        OpenAiChat,
        /// Anthropic Messages: {"messages":[...]} (request) and {"content":[...]} (response).
        AnthropicMessages,
    }

    public static async Task<int> TranslateAsync(
        JsonNode? root,
        RootShape shape,
        ITranslator translator,
        LanguageCode source,
        LanguageCode target,
        ToolArgsDenylist denylist,
        CancellationToken ct)
    {
        if (root is null || source.Value == target.Value) return 0;

        var argsTargets = new List<JsonValue>();

        switch (shape)
        {
            case RootShape.OpenAiChat:
                CollectOpenAiArguments(root, argsTargets);
                break;
            case RootShape.AnthropicMessages:
                CollectAnthropicArguments(root, argsTargets);
                break;
        }

        var translated = 0;
        foreach (var argsNode in argsTargets)
        {
            if (!argsNode.TryGetValue<string>(out var argsJson) || string.IsNullOrEmpty(argsJson))
                continue;

            JsonNode? inner;
            try { inner = JsonNode.Parse(argsJson); }
            catch (JsonException) { continue; }

            var sites = ToolArgsPlanner.Plan(inner, denylist);
            if (sites.Count == 0) continue;

            var tokenized = sites.Select(s => PlaceholderTokenizer.Tokenize(s.Source)).ToList();
            var requests = new List<TranslationRequest>(sites.Count);
            for (var i = 0; i < sites.Count; i++)
                requests.Add(new TranslationRequest(tokenized[i].Text, source, target, TagHandling: TagHandling.Xml));
            var results = await translator.TranslateBatchAsync(requests, ct);

            for (var i = 0; i < sites.Count; i++)
            {
                var raw = i < results.Count ? results[i].Text : tokenized[i].Text;
                var validation = PlaceholderValidator.Validate(raw, tokenized[i].Placeholders);
                if (!validation.Ok) continue;
                sites[i].Apply(PlaceholderTokenizer.Reinject(raw, tokenized[i].Placeholders));
                translated++;
            }

            // Serialize the mutated inner tree back into the arguments string.
            var parent = argsNode.Parent;
            if (parent is JsonObject parentObj)
            {
                var key = parentObj.Where(kv => ReferenceEquals(kv.Value, argsNode))
                    .Select(kv => kv.Key).FirstOrDefault();
                if (key is not null)
                    parentObj[key] = inner?.ToJsonString() ?? argsJson;
            }
        }

        return translated;
    }

    private static void CollectOpenAiArguments(JsonNode root, List<JsonValue> sink)
    {
        // Request: /messages/*/tool_calls/*/function/arguments
        if (root["messages"] is JsonArray reqMessages)
        {
            foreach (var m in reqMessages)
                CollectFromOpenAiMessage(m, sink);
        }

        // Response: /choices/*/message/tool_calls/*/function/arguments
        if (root["choices"] is JsonArray choices)
        {
            foreach (var c in choices)
            {
                if (c is JsonObject co && co["message"] is JsonNode msg)
                    CollectFromOpenAiMessage(msg, sink);
            }
        }
    }

    private static void CollectFromOpenAiMessage(JsonNode? msg, List<JsonValue> sink)
    {
        if (msg is not JsonObject mo) return;
        if (mo["tool_calls"] is not JsonArray tools) return;
        foreach (var t in tools)
        {
            if (t is JsonObject toolCall
                && toolCall["function"] is JsonObject fn
                && fn["arguments"] is JsonValue argsValue)
                sink.Add(argsValue);
        }
    }

    private static void CollectAnthropicArguments(JsonNode root, List<JsonValue> sink)
    {
        // Request: /messages/*/content[*] where type=tool_use → input (object, NOT a string)
        // These are handled by the main pipeline. We additionally translate tool_result content
        // (which may contain string leaves inside an object). Both paths go through the standard
        // allowlist; no JSON-in-JSON in Anthropic. This is intentionally a no-op here.
        _ = root; _ = sink;
    }
}
