using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using LlmTrans.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LlmTrans.Integration.Tests;

public sealed class McpFlowATests : IClassFixture<McpTestFactory>
{
    private readonly McpTestFactory _factory;

    public McpFlowATests(McpTestFactory factory)
    {
        _factory = factory;
        _factory.Upstream.Requests.Clear();
        _factory.Upstream.ResponseBody = null;
        _factory.Upstream.ResponseContentType = "application/json";
    }

    [Fact]
    public async Task Tools_list_response_has_descriptions_translated_to_user_language()
    {
        var (_, token) = await _factory.RegisterRemoteServerAsync(
            userLanguage: "de", llmLanguage: "en",
            upstreamUrl: "https://mcp.linear.app/");

        var upstreamResp = """
        {
          "jsonrpc":"2.0","id":1,"result":{"tools":[
            {"name":"search","description":"Find articles by keyword",
             "inputSchema":{"type":"object","properties":{
               "query":{"type":"string","description":"The query string"}
             }}}
          ]}
        }
        """;
        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(upstreamResp);
        _factory.Upstream.ResponseContentType = "application/json";

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{token}");
        req.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "upstream-oauth-token-xyz");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        var tool = root["result"]!["tools"]![0]!;
        Assert.Equal("search", tool["name"]!.GetValue<string>());
        Assert.StartsWith("[de]", tool["description"]!.GetValue<string>());
        Assert.StartsWith("[de]", tool["inputSchema"]!["properties"]!["query"]!["description"]!.GetValue<string>());

        // Upstream saw the exact OAuth token.
        var captured = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("Bearer", captured.AuthorizationScheme());
        Assert.Equal("upstream-oauth-token-xyz", captured.AuthorizationParameter());

        // Audit must not reference the upstream token value.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LlmTransDbContext>();
        var audit = await db.AuditEvents.AsNoTracking().ToListAsync();
        Assert.DoesNotContain(audit, a =>
            (a.Path?.Contains("upstream-oauth-token-xyz") ?? false)
            || (a.TranslatorId?.Contains("upstream-oauth-token-xyz") ?? false));
    }

    [Fact]
    public async Task Tools_call_request_translates_args_de_to_en_for_upstream()
    {
        var (_, token) = await _factory.RegisterRemoteServerAsync(
            userLanguage: "de", llmLanguage: "en",
            upstreamUrl: "https://mcp.example.com/");

        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"ok"}]}}""");

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{token}");
        req.Content = new StringContent("""
        {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{
          "name":"search","arguments":{"query":"Sommerkleider","user_id":"42"}
        }}
        """, Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var captured = Assert.Single(_factory.Upstream.Requests);
        var sent = JsonNode.Parse(captured.BodyAsString()!)!;
        var args = sent["params"]!["arguments"]!;
        Assert.StartsWith("[en]", args["query"]!.GetValue<string>());
        Assert.Contains("Sommerkleider", args["query"]!.GetValue<string>());
        Assert.Equal("42", args["user_id"]!.GetValue<string>());
        Assert.Equal("search", sent["params"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stdio_local_server_token_rejects_flow_a_with_409()
    {
        var token = await _factory.RegisterStdioServerTokenAsync(
            userLanguage: "de", llmLanguage: "en");

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{token}");
        req.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.Contains("stdio-local", body["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_token_rejected_with_401()
    {
        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp/rt_dev_not_a_token");
        req.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            Encoding.UTF8, "application/json");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Tool_call_result_text_translated_server_to_user()
    {
        var (_, token) = await _factory.RegisterRemoteServerAsync(
            userLanguage: "de", llmLanguage: "en",
            upstreamUrl: "https://mcp.example.com/");

        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"Found three items"}]}}""");

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/mcp/{token}");
        req.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"x","arguments":{}}}""",
            Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
        Assert.StartsWith("[de]", root["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }
}
