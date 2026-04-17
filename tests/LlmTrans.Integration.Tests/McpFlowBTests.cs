using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace LlmTrans.Integration.Tests;

public sealed class McpFlowBTests : IClassFixture<McpTestFactory>
{
    private readonly McpTestFactory _factory;

    public McpFlowBTests(McpTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Client_to_server_message_has_args_translated()
    {
        var token = await _factory.RegisterStdioServerTokenAsync("de", "en");

        using var client = _factory.CreateClient();
        var request = new
        {
            direction = "client-to-server",
            message = new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = "search",
                    arguments = new Dictionary<string, object?>
                    {
                        ["query"] = "Sommerkleider",
                        ["user_id"] = "42",
                    },
                },
            },
        };

        var resp = await client.PostAsJsonAsync($"/mcp-translate/{token}", request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        var args = body["message"]!["params"]!["arguments"]!;
        Assert.StartsWith("[en]", args["query"]!.GetValue<string>());
        Assert.Equal("42", args["user_id"]!.GetValue<string>());
        Assert.Equal("search", body["message"]!["params"]!["name"]!.GetValue<string>());
        Assert.Equal("fake-brackets", body["diagnostics"]!["translator"]!.GetValue<string>());
    }

    [Fact]
    public async Task Server_to_client_tools_list_response_has_descriptions_translated()
    {
        var token = await _factory.RegisterStdioServerTokenAsync("de", "en");

        using var client = _factory.CreateClient();
        var request = new
        {
            direction = "server-to-client",
            message = JsonNode.Parse("""
            {"jsonrpc":"2.0","id":1,"result":{"tools":[
              {"name":"search","description":"Find articles"}
            ]}}
            """),
        };

        var resp = await client.PostAsJsonAsync($"/mcp-translate/{token}", request);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        var tool = body["message"]!["result"]!["tools"]![0]!;
        Assert.StartsWith("[de]", tool["description"]!.GetValue<string>());
        Assert.Equal("search", tool["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_direction_returns_400()
    {
        var token = await _factory.RegisterStdioServerTokenAsync("de", "en");
        using var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync($"/mcp-translate/{token}", new
        {
            direction = "sideways",
            message = new { jsonrpc = "2.0", id = 1, method = "tools/list" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Invalid_token_returns_401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/mcp-translate/rt_dev_wrong", new
        {
            direction = "client-to-server",
            message = new { jsonrpc = "2.0", id = 1, method = "tools/list" },
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
