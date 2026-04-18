using System.Net;
using System.Text;

namespace AdaptiveApi.Integration.Tests;

public sealed class OpenAiStreamingTests : IClassFixture<ProxyTestFactory>
{
    private readonly ProxyTestFactory _factory;

    public OpenAiStreamingTests(ProxyTestFactory factory)
    {
        _factory = factory;
        _factory.Upstream.Requests.Clear();
        _factory.Upstream.ResponseBody = null;
        _factory.Upstream.ResponseContentType = "text/event-stream";
    }

    [Fact]
    public async Task Stream_with_direction_bidirectional_translates_response_deltas()
    {
        var sse = new StringBuilder();
        sse.Append("data: {\"id\":\"cmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n");
        sse.Append("data: {\"id\":\"cmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello, this is a long enough sentence to trigger a flush. \"}}]}\n\n");
        sse.Append("data: {\"id\":\"cmpl-1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
        sse.Append("data: [DONE]\n\n");

        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(sse.ToString());
        _factory.Upstream.ResponseContentType = "text/event-stream";

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"gpt-4o-mini","stream":true,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8, "application/json");
        req.Headers.Add("X-AdaptiveApi-Target-Lang", "de");
        req.Headers.Add("X-AdaptiveApi-Source-Lang", "en");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var text = await resp.Content.ReadAsStringAsync();
        // FakeBracketTranslator prefixes translated text with [de].
        Assert.Contains("[de]", text);
        Assert.Contains("Hello, this is a long enough sentence", text);
        Assert.Contains("\"role\":\"assistant\"", text);
        Assert.Contains("\"finish_reason\":\"stop\"", text);
        Assert.Contains("data: [DONE]\n\n", text);
        // adaptiveapi appends a trailing `x-adaptiveapi-timing` SSE event after [DONE].
        Assert.Contains("event: x-adaptiveapi-timing", text);
    }

    [Fact]
    public async Task Stream_passthrough_when_direction_off()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n";
        var bytes = Encoding.UTF8.GetBytes(sse);
        _factory.Upstream.ResponseBody = bytes;
        _factory.Upstream.ResponseContentType = "text/event-stream";

        // NO target-lang header → route remains Off → passthrough.
        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"x","stream":true,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        var received = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes, received);
    }
}
