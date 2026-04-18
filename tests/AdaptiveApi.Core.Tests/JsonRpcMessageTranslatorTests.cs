using System.Text.Json.Nodes;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Model;
using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Providers.Mcp;

namespace AdaptiveApi.Core.Tests;

public sealed class JsonRpcMessageTranslatorTests
{
    [Fact]
    public async Task Tools_list_result_translates_descriptions_keeps_names()
    {
        var msg = JsonNode.Parse("""
        {"jsonrpc":"2.0","id":1,"result":{"tools":[
          {"name":"search","description":"Find articles by keyword",
           "inputSchema":{"type":"object","properties":{
             "query":{"type":"string","description":"The query string"}
           }}}
        ]}}
        """) as JsonObject;

        var translator = new UppercaseTranslator();
        var n = await JsonRpcMessageTranslator.TranslateAsync(
            msg, McpDirection.ServerToClient, translator,
            LanguageCode.English, new LanguageCode("de"),
            ToolArgsDenylist.Default, default);

        Assert.Equal(2, n);
        var tool = msg!["result"]!["tools"]![0]!;
        Assert.Equal("search", tool["name"]!.GetValue<string>());
        Assert.Equal("FIND ARTICLES BY KEYWORD", tool["description"]!.GetValue<string>());
        Assert.Equal("THE QUERY STRING",
            tool["inputSchema"]!["properties"]!["query"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tools_call_client_to_server_translates_args_but_respects_denylist()
    {
        var msg = JsonNode.Parse("""
        {"jsonrpc":"2.0","id":7,"method":"tools/call","params":{
          "name":"search_articles",
          "arguments":{"query":"hello world","user_id":"42","tax_code":"A1",
                       "meta":{"label":"urgent"}}
        }}
        """) as JsonObject;

        await JsonRpcMessageTranslator.TranslateAsync(
            msg, McpDirection.ClientToServer, new UppercaseTranslator(),
            LanguageCode.English, new LanguageCode("de"),
            ToolArgsDenylist.Default, default);

        var args = msg!["params"]!["arguments"]!;
        Assert.Equal("HELLO WORLD", args["query"]!.GetValue<string>());
        Assert.Equal("URGENT", args["meta"]!["label"]!.GetValue<string>());
        Assert.Equal("42", args["user_id"]!.GetValue<string>());
        Assert.Equal("A1", args["tax_code"]!.GetValue<string>());
        Assert.Equal("search_articles", msg["params"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tools_call_result_translates_content_text()
    {
        var msg = JsonNode.Parse("""
        {"jsonrpc":"2.0","id":7,"result":{"content":[
          {"type":"text","text":"Found three matching articles."}
        ]}}
        """) as JsonObject;

        var n = await JsonRpcMessageTranslator.TranslateAsync(
            msg, McpDirection.ServerToClient, new UppercaseTranslator(),
            LanguageCode.English, new LanguageCode("de"),
            ToolArgsDenylist.Default, default);

        Assert.Equal(1, n);
        Assert.Equal("FOUND THREE MATCHING ARTICLES.",
            msg!["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Initialize_and_ping_pass_through_unchanged()
    {
        var before = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"1.0"}}""";
        var msg = JsonNode.Parse(before) as JsonObject;
        var n = await JsonRpcMessageTranslator.TranslateAsync(
            msg, McpDirection.ClientToServer, new UppercaseTranslator(),
            LanguageCode.English, new LanguageCode("de"),
            ToolArgsDenylist.Default, default);
        Assert.Equal(0, n);
        Assert.Equal(before.Replace(" ", ""), msg!.ToJsonString().Replace(" ", ""));
    }

    [Fact]
    public async Task Error_responses_are_not_translated()
    {
        var msg = JsonNode.Parse("""
        {"jsonrpc":"2.0","id":7,"error":{"code":-32601,"message":"method not found"}}
        """) as JsonObject;

        var n = await JsonRpcMessageTranslator.TranslateAsync(
            msg, McpDirection.ServerToClient, new UppercaseTranslator(),
            LanguageCode.English, new LanguageCode("de"),
            ToolArgsDenylist.Default, default);
        Assert.Equal(0, n);
        Assert.Equal("method not found", msg!["error"]!["message"]!.GetValue<string>());
    }

    private sealed class UppercaseTranslator : ITranslator
    {
        public string TranslatorId => "uc";
        public TranslatorCapabilities Capabilities => TranslatorCapabilities.TagHandling;
        public Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
            IReadOnlyList<TranslationRequest> r, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TranslationResult>>(
                r.Select(x => new TranslationResult(UpperOutsideTags(x.Text))).ToArray());

        private static string UpperOutsideTags(string s)
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
