using AdaptiveApi.Plugins.SDK;
using AdaptiveApi.Plugins.SDK.Hooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdaptiveApi.Plugins.SamplePlugin;

/// Reference implementation of the AdaptiveAPI plugin contract.
///
/// Exercises every concrete extension point so plugin authors have a copy-
/// pasteable starting point:
///   * declares a manifest (id, version, capabilities)
///   * registers a hook implementation as a DI service
///   * maps a small admin endpoint at /plugins/sample-plugin/ping
///
/// Build and drop the resulting DLL next to the host binary, or mount it
/// under /app/plugins/ in the docker-compose deployment. The host picks it
/// up at startup; toggle it on / off from the Plugins admin page.
public sealed class SamplePlugin : IAdaptiveApiPlugin
{
    public PluginManifest Manifest => new()
    {
        Id = "sample-plugin",
        Name = "Sample Plugin",
        Version = "0.1.0",
        Description = "Reference implementation showing the SDK surface.",
        Category = "Example",
        HasSettings = true,
        HasEndpoints = true,
    };

    public void RegisterServices(IServiceCollection services)
    {
        // Register hook implementations the same way you'd register any
        // other service. Scoped is the natural choice when a hook depends
        // on per-request state (DbContext, etc.) — the dispatcher resolves
        // hooks from the request scope, so this is safe.
        services.AddScoped<IRequestTranslationHook, HeaderStampHook>();
    }

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        // Routes mapped here are mounted at /plugins/{Id}/<your-path>. The
        // host applies the admin authorization policy by default; see
        // PluginManifest.AllowAnonymousEndpoints for the opt-out.
        routes.MapGet("/ping", () => Results.Ok(new { plugin = "sample-plugin", ok = true }));
    }
}

/// Tags every translation request with a header indicating the plugin saw it.
/// A real plugin would do something useful here — quota enforcement, audit
/// logging, body sanitisation. This one just demonstrates the wiring.
internal sealed class HeaderStampHook : IRequestTranslationHook
{
    private readonly ILogger<HeaderStampHook> _log;
    public HeaderStampHook(ILogger<HeaderStampHook> log) => _log = log;

    public string PluginId => "sample-plugin";

    public Task<HookResult> BeforeAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct)
    {
        _log.LogInformation("sample-plugin saw request on route {RouteId} ({UserLang} -> {LlmLang})",
            ctx.RouteId, ctx.UserLanguage, ctx.LlmLanguage);
        ctx.HttpContext.Response.OnStarting(() =>
        {
            ctx.HttpContext.Response.Headers["X-AdaptiveApi-Sample-Plugin"] = "saw-request";
            return Task.CompletedTask;
        });
        return Task.FromResult(HookResult.Continue());
    }

    public Task<HookResult> AfterAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct) =>
        Task.FromResult(HookResult.Continue());
}
