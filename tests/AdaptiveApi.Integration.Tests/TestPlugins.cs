using AdaptiveApi.Plugins.SDK;
using AdaptiveApi.Plugins.SDK.Hooks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveApi.Integration.Tests;

/// Cross-test counter that <see cref="IntegrationTestPlugin"/>'s hook bumps
/// every time it fires. Tests reset it before exercising the proxy and
/// then assert the call count to verify hooks ran.
public static class IntegrationTestPluginCounter
{
    public static int RequestHookCalls;
    public static int ResponseHookCalls;
    public static bool IsStreamingSeenByAi;

    public static void Reset()
    {
        RequestHookCalls = 0;
        ResponseHookCalls = 0;
        IsStreamingSeenByAi = false;
    }
}

/// Smoke-test plugin module: registered automatically by the loader's
/// AppDomain scan whenever the integration tests boot the API. The plugin
/// declares a manifest, persists no settings, and registers hooks on each
/// of the three contracts so we can verify the dispatcher fires them
/// (including across streaming requests).
public sealed class IntegrationTestPlugin : IAdaptiveApiPlugin
{
    public PluginManifest Manifest => new()
    {
        Id = "integration-test",
        Name = "Integration Test Plugin",
        Version = "0.0.1",
        Description = "Smoke-test plugin for the plugin loader / admin endpoints.",
        Category = "Other",
        HasSettings = true,
        HasEndpoints = false,
    };

    public void RegisterServices(IServiceCollection services)
    {
        services.AddScoped<IRequestTranslationHook, RecordingRequestHook>();
        services.AddScoped<IResponseTranslationHook, RecordingResponseHook>();
        services.AddScoped<IAiCallHook, RecordingAiHook>();
    }

    public void MapRoutes(IEndpointRouteBuilder routes) { }
}

internal sealed class RecordingRequestHook : IRequestTranslationHook
{
    public string PluginId => "integration-test";
    public Task<HookResult> BeforeAsync(PipelineHookContext c, byte[] body, CancellationToken ct)
    {
        Interlocked.Increment(ref IntegrationTestPluginCounter.RequestHookCalls);
        return Task.FromResult(HookResult.Continue());
    }
    public Task<HookResult> AfterAsync(PipelineHookContext c, byte[] body, CancellationToken ct) =>
        Task.FromResult(HookResult.Continue());
}

internal sealed class RecordingResponseHook : IResponseTranslationHook
{
    public string PluginId => "integration-test";
    public Task<HookResult> BeforeAsync(PipelineHookContext c, byte[] body, CancellationToken ct)
    {
        Interlocked.Increment(ref IntegrationTestPluginCounter.ResponseHookCalls);
        return Task.FromResult(HookResult.Continue());
    }
    public Task<HookResult> AfterAsync(PipelineHookContext c, byte[] body, CancellationToken ct) =>
        Task.FromResult(HookResult.Continue());
}

internal sealed class RecordingAiHook : IAiCallHook
{
    public string PluginId => "integration-test";
    public Task<HookResult> BeforeAsync(PipelineHookContext c, HttpRequestMessage r, CancellationToken ct)
    {
        if (c.IsStreaming) IntegrationTestPluginCounter.IsStreamingSeenByAi = true;
        return Task.FromResult(HookResult.Continue());
    }
    public Task<HookResult> AfterAsync(PipelineHookContext c, HttpResponseMessage r, CancellationToken ct) =>
        Task.FromResult(HookResult.Continue());
}
