using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace LlmTrans.Integration.Tests;

public sealed class OpenAiToolCallTests : IClassFixture<ProxyTestFactory>
{
    private readonly ProxyTestFactory _factory;

    public OpenAiToolCallTests(ProxyTestFactory factory)
    {
        _factory = factory;
        _factory.Upstream.Requests.Clear();
        _factory.Upstream.ResponseBody = null;
        _factory.Upstream.ResponseContentType = "application/json";
    }

    [Fact]
    public async Task Tool_call_arguments_are_translated_in_response()
    {
        var upstreamBody = """
        {
          "id":"cmpl-1",
          "choices":[{
            "index":0,
            "message":{
              "role":"assistant",
              "content":null,
              "tool_calls":[{
                "id":"call_1",
                "type":"function",
                "function":{
                  "name":"search_articles",
                  "arguments":"{\"query\":\"summer dresses\",\"filter\":{\"label\":\"sale\",\"sku_code\":\"SD-9\"},\"user_id\":\"42\"}"
                }
              }]
            },
            "finish_reason":"tool_calls"
          }]
        }
        """;
        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(upstreamBody);

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"find me nice dresses"}],"tools":[{"type":"function","function":{"name":"search_articles","description":"Looks up articles"}}]}""",
            Encoding.UTF8, "application/json");
        req.Headers.Add("X-LlmTrans-Target-Lang", "de");
        req.Headers.Add("X-LlmTrans-Source-Lang", "en");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        var root = JsonNode.Parse(body)!;

        // The arguments field is a JSON string — parse and inspect.
        var argsStr = root["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>();
        var args = JsonNode.Parse(argsStr)!;

        Assert.StartsWith("[de]", args["query"]!.GetValue<string>());
        Assert.Contains("summer dresses", args["query"]!.GetValue<string>());
        Assert.StartsWith("[de]", args["filter"]!["label"]!.GetValue<string>());

        // Denylist kept these untouched.
        Assert.Equal("42", args["user_id"]!.GetValue<string>());
        Assert.Equal("SD-9", args["filter"]!["sku_code"]!.GetValue<string>());

        // tool_call metadata preserved.
        Assert.Equal("call_1", root["choices"]![0]!["message"]!["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("search_articles", root["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tool_descriptions_in_request_are_translated_to_llm_language()
    {
        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(
            """{"id":"x","choices":[{"message":{"role":"assistant","content":"ok"}}]}""");

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"q"}],"tools":[{"type":"function","function":{"name":"search","description":"Sucht nach Artikeln"}}]}""",
            Encoding.UTF8, "application/json");
        req.Headers.Add("X-LlmTrans-Target-Lang", "de");
        req.Headers.Add("X-LlmTrans-Source-Lang", "en");

        using var resp = await client.SendAsync(req);
        var captured = Assert.Single(_factory.Upstream.Requests);
        var sent = JsonNode.Parse(captured.BodyAsString()!)!;

        var desc = sent["tools"]![0]!["function"]!["description"]!.GetValue<string>();
        Assert.StartsWith("[en]", desc);
        Assert.Equal("search", sent["tools"]![0]!["function"]!["name"]!.GetValue<string>());
    }
}
