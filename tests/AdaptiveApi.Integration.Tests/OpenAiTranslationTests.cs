using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdaptiveApi.Integration.Tests;

public sealed class OpenAiTranslationTests : IClassFixture<ProxyTestFactory>
{
    private readonly ProxyTestFactory _factory;

    public OpenAiTranslationTests(ProxyTestFactory factory)
    {
        _factory = factory;
        _factory.Upstream.Requests.Clear();
        _factory.Upstream.ResponseBody = null;
        _factory.Upstream.ResponseContentType = "application/json";
    }

    [Fact]
    public async Task Request_body_is_translated_to_llm_language_before_upstream_receives_it()
    {
        // Fake upstream returns a valid, small JSON chat response — any response works here,
        // we care about the REQUEST the upstream saw.
        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(
            """{"id":"resp","choices":[{"index":0,"message":{"role":"assistant","content":"hello"}}]}""");

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"Translate this line"}]}""",
            Encoding.UTF8, "application/json");
        req.Headers.Add("X-AdaptiveApi-Target-Lang", "de"); // user language = DE
        req.Headers.Add("X-AdaptiveApi-Source-Lang", "en"); // llm language = EN

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var captured = Assert.Single(_factory.Upstream.Requests);
        var sentBody = captured.BodyAsString()!;
        var sent = JsonNode.Parse(sentBody)!;

        // Request translated DE → EN, with FakeBracketTranslator annotating with [en].
        var content = sent["messages"]![0]!["content"]!.GetValue<string>();
        Assert.StartsWith("[en]", content);
        Assert.Contains("Translate this line", content);

        // JSON shape preserved: model, role, keys all present and unchanged.
        Assert.Equal("gpt-4o-mini", sent["model"]!.GetValue<string>());
        Assert.Equal("user", sent["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Response_body_is_translated_into_user_language()
    {
        var upstreamResponse = """{"id":"r","choices":[{"index":0,"message":{"role":"assistant","content":"Hello, friend."}}],"usage":{"total_tokens":5}}""";
        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(upstreamResponse);

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"Hi"}]}""",
            Encoding.UTF8, "application/json");
        req.Headers.Add("X-AdaptiveApi-Target-Lang", "de");
        req.Headers.Add("X-AdaptiveApi-Source-Lang", "en");

        using var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        var root = JsonNode.Parse(body)!;

        var content = root["choices"]![0]!["message"]!["content"]!.GetValue<string>();
        Assert.StartsWith("[de]", content);
        Assert.Contains("Hello, friend.", content);

        // Keys untouched: `usage`, `role`, `id`.
        Assert.Equal("r", root["id"]!.GetValue<string>());
        Assert.Equal("assistant", root["choices"]![0]!["message"]!["role"]!.GetValue<string>());
        Assert.Equal(5, root["usage"]!["total_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Admin_api_can_create_tenant_route_and_issue_token()
    {
        using var client = _factory.CreateClient();

        var t = await client.PostAsJsonAsync("/admin/tenants",
            new { id = "t_acme", name = "Acme Corp" });
        Assert.Equal(HttpStatusCode.Created, t.StatusCode);

        var r = await client.PostAsJsonAsync("/admin/routes", new
        {
            id = "r_acme_openai",
            tenantId = "t_acme",
            kind = "OpenAiChat",
            upstreamBaseUrl = "https://api.openai.com/",
            userLanguage = "de",
            llmLanguage = "en",
            direction = "Bidirectional",
            translatorId = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        var tokResp = await client.PostAsync("/admin/routes/r_acme_openai/tokens", content: null);
        Assert.Equal(HttpStatusCode.Created, tokResp.StatusCode);
        var tok = await tokResp.Content.ReadFromJsonAsync<JsonObject>();
        var plaintext = tok!["plaintextToken"]!.GetValue<string>();
        Assert.StartsWith("rt_", plaintext);
    }
}
