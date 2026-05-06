using AdaptiveApi.Core.Plugins;
using AdaptiveApi.Plugins.SDK.Hooks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdaptiveApi.Core.Tests;

public sealed class PluginHookDispatcherTests
{
    [Fact]
    public async Task Body_hooks_are_threaded_through_in_registration_order()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestTranslationHook>(new BodyAppenderHook("a", "[A]"));
        services.AddSingleton<IRequestTranslationHook>(new BodyAppenderHook("b", "[B]"));
        var dispatcher = MakeDispatcher(services);

        var ctx = MakeCtx();
        var result = await dispatcher.RunBeforeRequestTranslationAsync(ctx, Bytes("x"), default);

        Assert.True(result.ContinuePipeline);
        Assert.Equal("x[A][B]", Str(result.ModifiedBody!));
    }

    [Fact]
    public async Task Body_hook_short_circuit_skips_remaining_hooks()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestTranslationHook>(new ShortCircuitHook("first", 418, "teapot"));
        services.AddSingleton<IRequestTranslationHook>(new BodyAppenderHook("second", "should-not-run"));
        var dispatcher = MakeDispatcher(services);

        var result = await dispatcher.RunBeforeRequestTranslationAsync(MakeCtx(), Bytes("body"), default);

        Assert.False(result.ContinuePipeline);
        Assert.Equal(418, result.ShortCircuitStatus);
    }

    [Fact]
    public async Task Body_hook_throw_fails_open_translation_hooks()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestTranslationHook>(new ThrowingRequestHook("boom"));
        services.AddSingleton<IRequestTranslationHook>(new BodyAppenderHook("survivor", "[S]"));
        var dispatcher = MakeDispatcher(services);

        var result = await dispatcher.RunBeforeRequestTranslationAsync(MakeCtx(), Bytes("body"), default);

        Assert.True(result.ContinuePipeline);
        Assert.Equal("body[S]", Str(result.ModifiedBody!));
    }

    [Fact]
    public async Task Ai_hook_throw_fails_closed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAiCallHook>(new ThrowingAiHook("boom"));
        var dispatcher = MakeDispatcher(services);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/");
        var result = await dispatcher.RunBeforeAiAsync(MakeCtx(), req, default);

        Assert.False(result.ContinuePipeline);
        Assert.Equal(500, result.ShortCircuitStatus);
        Assert.Contains("boom", Str(result.ShortCircuitBody!));
    }

    [Fact]
    public async Task Scoped_hook_resolves_a_fresh_instance_per_request()
    {
        var services = new ServiceCollection();
        services.AddScoped<IRequestTranslationHook, CountingScopedHook>();
        services.AddSingleton<CountingScopedHook.Counter>();

        var (dispatcher, root) = MakeDispatcherWithRoot(services);

        // Simulate two distinct requests by swapping the active HttpContext.
        // Each request gets its own service scope; the hook is Scoped, so it
        // must be a different instance each time. The captive-dependency bug
        // would make this fail by reusing the same instance across requests.
        var seen = new HashSet<Guid>();
        for (var i = 0; i < 2; i++)
        {
            using var scope = root.CreateScope();
            var ctxAccessor = root.GetRequiredService<IHttpContextAccessor>();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            ctxAccessor.HttpContext = http;
            await dispatcher.RunBeforeRequestTranslationAsync(MakeCtx(), Bytes("x"), default);
            ctxAccessor.HttpContext = null;

            var hook = (CountingScopedHook)scope.ServiceProvider.GetServices<IRequestTranslationHook>().Single();
            seen.Add(hook.InstanceId);
        }
        Assert.Equal(2, seen.Count);

        var counter = root.GetRequiredService<CountingScopedHook.Counter>();
        Assert.Equal(2, counter.Calls);
    }

    private static IPluginHookDispatcher MakeDispatcher(IServiceCollection services) =>
        MakeDispatcherWithRoot(services).Dispatcher;

    private static (IPluginHookDispatcher Dispatcher, IServiceProvider Root) MakeDispatcherWithRoot(IServiceCollection services)
    {
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddSingleton<IPluginMetrics, InMemoryPluginMetrics>();
        var root = services.BuildServiceProvider();
        var dispatcher = new PluginHookDispatcher(
            root.GetRequiredService<IHttpContextAccessor>(),
            root,
            root.GetRequiredService<IPluginMetrics>(),
            NullLogger<PluginHookDispatcher>.Instance);
        return (dispatcher, root);
    }

    private static PipelineHookContext MakeCtx() => new(
        HttpContext: new DefaultHttpContext(),
        RouteId: "r", TenantId: "t", ProviderId: "test",
        UserLanguage: "de", LlmLanguage: "en-US", Direction: "Bidirectional",
        IsStreaming: false,
        Properties: new Dictionary<string, object?>(StringComparer.Ordinal));

    private static byte[] Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);
    private static string Str(byte[] b) => System.Text.Encoding.UTF8.GetString(b);

    private sealed class BodyAppenderHook : IRequestTranslationHook
    {
        private readonly string _suffix;
        public BodyAppenderHook(string id, string suffix) { PluginId = id; _suffix = suffix; }
        public string PluginId { get; }
        public Task<HookResult> BeforeAsync(PipelineHookContext c, byte[] body, CancellationToken ct) =>
            Task.FromResult(HookResult.Modify(Bytes(Str(body) + _suffix)));
        public Task<HookResult> AfterAsync(PipelineHookContext c, byte[] body, CancellationToken ct) =>
            Task.FromResult(HookResult.Continue());
    }

    private sealed class ShortCircuitHook : IRequestTranslationHook
    {
        private readonly int _status;
        private readonly string _payload;
        public ShortCircuitHook(string id, int status, string payload) { PluginId = id; _status = status; _payload = payload; }
        public string PluginId { get; }
        public Task<HookResult> BeforeAsync(PipelineHookContext c, byte[] body, CancellationToken ct) =>
            Task.FromResult(HookResult.ShortCircuit(_status, Bytes(_payload), "text/plain"));
        public Task<HookResult> AfterAsync(PipelineHookContext c, byte[] body, CancellationToken ct) =>
            Task.FromResult(HookResult.Continue());
    }

    private sealed class ThrowingRequestHook : IRequestTranslationHook
    {
        public ThrowingRequestHook(string id) { PluginId = id; }
        public string PluginId { get; }
        public Task<HookResult> BeforeAsync(PipelineHookContext c, byte[] body, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
        public Task<HookResult> AfterAsync(PipelineHookContext c, byte[] body, CancellationToken ct) =>
            Task.FromResult(HookResult.Continue());
    }

    private sealed class ThrowingAiHook : IAiCallHook
    {
        public ThrowingAiHook(string id) { PluginId = id; }
        public string PluginId { get; }
        public Task<HookResult> BeforeAsync(PipelineHookContext c, HttpRequestMessage r, CancellationToken ct) =>
            throw new InvalidOperationException("boom-ai");
        public Task<HookResult> AfterAsync(PipelineHookContext c, HttpResponseMessage r, CancellationToken ct) =>
            Task.FromResult(HookResult.Continue());
    }

    public sealed class CountingScopedHook : IRequestTranslationHook
    {
        public sealed class Counter { public int Calls; }
        private readonly Counter _counter;
        public CountingScopedHook(Counter counter) { _counter = counter; }
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string PluginId => "scoped";
        public Task<HookResult> BeforeAsync(PipelineHookContext c, byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref _counter.Calls);
            return Task.FromResult(HookResult.Continue());
        }
        public Task<HookResult> AfterAsync(PipelineHookContext c, byte[] body, CancellationToken ct) =>
            Task.FromResult(HookResult.Continue());
    }
}
