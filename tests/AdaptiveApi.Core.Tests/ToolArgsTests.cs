using System.Text.Json.Nodes;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Model;
using AdaptiveApi.Core.Pipeline;

namespace AdaptiveApi.Core.Tests;

public sealed class ToolArgsTests
{
    [Theory]
    [InlineData("id", true)]
    [InlineData("uuid", true)]
    [InlineData("email", true)]
    [InlineData("user_id", true)]
    [InlineData("customerId", true)]
    [InlineData("tax_code", true)]
    [InlineData("description", false)]
    [InlineData("title", false)]
    [InlineData("message", false)]
    public void Default_denylist_covers_identifier_keys(string key, bool denied)
    {
        Assert.Equal(denied, ToolArgsDenylist.Default.IsDenied(key));
    }

    [Fact]
    public void Planner_translates_string_leaves_not_in_denylist()
    {
        var root = JsonNode.Parse(
            """{"query":"find invoices","user_id":"42","description":"monthly report","metadata":{"email":"user@host","title":"Overview"}}""");

        var sites = ToolArgsPlanner.Plan(root, ToolArgsDenylist.Default);

        Assert.Contains(sites, s => s.Source == "find invoices");
        Assert.Contains(sites, s => s.Source == "monthly report");
        Assert.Contains(sites, s => s.Source == "Overview");
        Assert.DoesNotContain(sites, s => s.Source == "42");            // user_id
        Assert.DoesNotContain(sites, s => s.Source == "user@host");     // email
    }

    [Fact]
    public async Task ToolCallTranslator_rewrites_openai_tool_arguments_in_place()
    {
        var root = JsonNode.Parse(
            """{"messages":[{"role":"assistant","tool_calls":[{"id":"c1","type":"function","function":{"name":"search","arguments":"{\"query\":\"hello world\",\"user_id\":\"7\",\"filter\":{\"label\":\"urgent\",\"tax_code\":\"A1\"}}"}}]}]}""")!;

        var translator = new UppercaseTranslator();
        var rewritten = await ToolCallTranslator.TranslateAsync(
            root, ToolCallTranslator.RootShape.OpenAiChat,
            translator, LanguageCode.English, new LanguageCode("de"),
            ToolArgsDenylist.Default, default);

        Assert.Equal(2, rewritten); // query + label; user_id and tax_code denied

        var argsStr = root["messages"]![0]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>();
        var args = JsonNode.Parse(argsStr)!;

        Assert.Equal("HELLO WORLD", args["query"]!.GetValue<string>());
        Assert.Equal("URGENT", args["filter"]!["label"]!.GetValue<string>());
        Assert.Equal("7", args["user_id"]!.GetValue<string>());
        Assert.Equal("A1", args["filter"]!["tax_code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolCallTranslator_handles_response_side_openai_shape()
    {
        var root = JsonNode.Parse(
            """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"x","type":"function","function":{"name":"f","arguments":"{\"summary\":\"this is a note\"}"}}]}}]}""")!;

        var rewritten = await ToolCallTranslator.TranslateAsync(
            root, ToolCallTranslator.RootShape.OpenAiChat,
            new UppercaseTranslator(), LanguageCode.English, new LanguageCode("de"),
            ToolArgsDenylist.Default, default);

        Assert.Equal(1, rewritten);
        var argsStr = root["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>();
        Assert.Contains("THIS IS A NOTE", argsStr);
    }

    private sealed class UppercaseTranslator : ITranslator
    {
        public string TranslatorId => "uc";
        public TranslatorCapabilities Capabilities => TranslatorCapabilities.TagHandling;
        public Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
            IReadOnlyList<TranslationRequest> r, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TranslationResult>>(
                r.Select(x => new TranslationResult(UpperPreservingTags(x.Text))).ToArray());

        private static string UpperPreservingTags(string s)
        {
            var sb = new System.Text.StringBuilder();
            var i = 0;
            while (i < s.Length)
            {
                var tagStart = s.IndexOf("<adaptiveapi ", i, StringComparison.Ordinal);
                if (tagStart < 0) { sb.Append(s[i..].ToUpperInvariant()); break; }
                sb.Append(s[i..tagStart].ToUpperInvariant());
                var tagEnd = s.IndexOf("/>", tagStart, StringComparison.Ordinal) + 2;
                sb.Append(s[tagStart..tagEnd]);
                i = tagEnd;
            }
            return sb.ToString();
        }
    }
}
