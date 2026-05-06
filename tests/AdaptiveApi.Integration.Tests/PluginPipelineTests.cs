using System.Net;
using System.Text;

namespace AdaptiveApi.Integration.Tests;

/// Verifies the dispatcher actually fires plugin hooks during proxy requests
/// (covers both the JSON-body path and the streaming SSE path). The
/// previous PluginAdminTests only exercised CRUD on the admin endpoints —
/// these complement it by checking the runtime fan-out works end-to-end.
public sealed class PluginPipelineTests : IClassFixture<ProxyTestFactory>
{
    private readonly ProxyTestFactory _factory;
    public PluginPipelineTests(ProxyTestFactory factory)
    {
        _factory = factory;
        _factory.Upstream.Requests.Clear();
        IntegrationTestPluginCounter.Reset();
    }

    [Fact]
    public async Task Plugin_request_hook_fires_on_json_proxy_call()
    {
        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(
            """{"id":"1","choices":[{"message":{"role":"assistant","content":"hi"}}]}""");
        _factory.Upstream.ResponseContentType = "application/json";

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8, "application/json");
        // Force Bidirectional so the response-translation hook path runs.
        // Without lang headers the seeded route's direction may be Off,
        // which legitimately skips the response side.
        req.Headers.Add("X-AdaptiveApi-Lang", "de/en");
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Six hook points fire per JSON request that goes through translation;
        // we only assert "at least once" so the test isn't brittle if the
        // pipeline shape evolves. The point is to prove the dispatcher
        // reaches the plugin's scoped hook at all.
        Assert.True(IntegrationTestPluginCounter.RequestHookCalls > 0,
            "plugin's request-translation hook never fired");
        Assert.True(IntegrationTestPluginCounter.ResponseHookCalls > 0,
            "plugin's response-translation hook never fired");
    }

    [Fact]
    public async Task Plugin_request_hook_fires_on_streaming_call_with_isStreaming_flag()
    {
        var sse = new StringBuilder();
        sse.Append("data: {\"id\":\"cmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"}}]}\n\n");
        sse.Append("data: {\"id\":\"cmpl-1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"}}]}\n\n");
        sse.Append("data: [DONE]\n\n");
        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(sse.ToString());
        _factory.Upstream.ResponseContentType = "text/event-stream";

        using var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"gpt-4o-mini","stream":true,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // Drain the stream so AfterAi runs to completion.
        _ = await resp.Content.ReadAsStringAsync();

        Assert.True(IntegrationTestPluginCounter.RequestHookCalls > 0,
            "plugin's request-translation hook never fired on streaming call");
        Assert.True(IntegrationTestPluginCounter.IsStreamingSeenByAi,
            "AI hook should have observed IsStreaming=true on a stream:true request");
    }
}
