using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace LlmTrans.Integration.Tests;

public sealed class OpenAiPassthroughTests : IClassFixture<ProxyTestFactory>
{
    private readonly ProxyTestFactory _factory;

    public OpenAiPassthroughTests(ProxyTestFactory factory)
    {
        _factory = factory;
        _factory.Upstream.Requests.Clear();
        _factory.Upstream.ResponseBody = null;
        _factory.Upstream.AdditionalResponseHeaders.Clear();
    }

    [Fact]
    public async Task Chat_completions_streams_sse_body_byte_identical_when_direction_off()
    {
        var body = BuildSseFixture();
        _factory.Upstream.ResponseBody = body;
        _factory.Upstream.ResponseContentType = "text/event-stream";

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"gpt-4o-mini","stream":true,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sk-test-upstream-secret");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var received = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(body, received);

        var captured = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("Bearer", captured.AuthorizationScheme());
        Assert.Equal("sk-test-upstream-secret", captured.AuthorizationParameter());
        Assert.Equal("/v1/chat/completions", captured.RequestUri.AbsolutePath);
    }

    [Fact]
    public async Task Unknown_route_token_returns_401()
    {
        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            "/v1/rt_dev_NOTAREALTOKEN/chat/completions");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task LlmTrans_extension_headers_are_stripped_before_upstream()
    {
        _factory.Upstream.ResponseBody = BuildSseFixture();

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"gpt-4o-mini","stream":true,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8, "application/json");
        req.Headers.Add("X-LlmTrans-Target-Lang", "de");
        req.Headers.Add("X-Custom-Passthrough", "keep-me");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var captured = Assert.Single(_factory.Upstream.Requests);
        Assert.False(captured.HasHeader("X-LlmTrans-Target-Lang"),
            "llmtrans extension header leaked to upstream");
        Assert.True(captured.HasHeader("X-Custom-Passthrough"),
            "arbitrary custom headers must pass through");
    }

    private static byte[] BuildSseFixture()
    {
        var sb = new StringBuilder();
        sb.Append("data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n");
        sb.Append("data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n");
        sb.Append("data: {\"choices\":[{\"delta\":{\"content\":\", world!\"}}]}\n\n");
        sb.Append("data: [DONE]\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
