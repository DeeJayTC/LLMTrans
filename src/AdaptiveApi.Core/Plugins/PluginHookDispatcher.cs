using System.Diagnostics;
using AdaptiveApi.Plugins.SDK.Hooks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdaptiveApi.Core.Plugins;

/// Singleton dispatcher that resolves hook lists from the per-request scope on
/// every invocation. The dispatcher itself holds no hook state — that lets
/// plugin authors register hooks as <c>Scoped</c> (the natural choice when a
/// hook needs <c>DbContext</c>) without leaking instances across requests.
public sealed class PluginHookDispatcher : IPluginHookDispatcher
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceProvider _rootServices;
    private readonly IPluginMetrics _metrics;
    private readonly ILogger<PluginHookDispatcher> _log;

    public PluginHookDispatcher(
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider rootServices,
        IPluginMetrics metrics,
        ILogger<PluginHookDispatcher> log)
    {
        _httpContextAccessor = httpContextAccessor;
        _rootServices = rootServices;
        _metrics = metrics;
        _log = log;
    }

    public Task<HookResult> RunBeforeRequestTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct) =>
        RunBodyHooksAsync<IRequestTranslationHook>("BeforeRequestTranslation", ctx, body,
            (h, c, b, t) => h.BeforeAsync(c, b, t), failOpen: true, ct);

    public Task<HookResult> RunAfterRequestTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct) =>
        RunBodyHooksAsync<IRequestTranslationHook>("AfterRequestTranslation", ctx, body,
            (h, c, b, t) => h.AfterAsync(c, b, t), failOpen: true, ct);

    public Task<HookResult> RunBeforeResponseTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct) =>
        RunBodyHooksAsync<IResponseTranslationHook>("BeforeResponseTranslation", ctx, body,
            (h, c, b, t) => h.BeforeAsync(c, b, t), failOpen: true, ct);

    public Task<HookResult> RunAfterResponseTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct) =>
        RunBodyHooksAsync<IResponseTranslationHook>("AfterResponseTranslation", ctx, body,
            (h, c, b, t) => h.AfterAsync(c, b, t), failOpen: true, ct);

    public async Task<HookResult> RunBeforeAiAsync(PipelineHookContext ctx, System.Net.Http.HttpRequestMessage request, CancellationToken ct)
    {
        foreach (var hook in ResolveHooks<IAiCallHook>())
        {
            var sw = Stopwatch.StartNew();
            HookResult? result;
            try
            {
                result = await hook.BeforeAsync(ctx, request, ct);
                sw.Stop();
                _metrics.Record(hook.PluginId, "BeforeAi", sw.Elapsed.TotalMilliseconds, failed: false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _metrics.Record(hook.PluginId, "BeforeAi", sw.Elapsed.TotalMilliseconds, failed: true);
                // AI hooks are policy-shaped (auth, quota, redaction). Fail closed.
                _log.LogError(ex, "BeforeAi hook {Plugin} threw; failing closed", hook.PluginId);
                return HookResult.ShortCircuit(500,
                    System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"plugin '{hook.PluginId}' failed\"}}"),
                    "application/json");
            }
            if (result is null) continue;
            if (!result.ContinuePipeline) return result;
        }
        return HookResult.Continue();
    }

    public async Task<HookResult> RunAfterAiAsync(PipelineHookContext ctx, System.Net.Http.HttpResponseMessage response, CancellationToken ct)
    {
        foreach (var hook in ResolveHooks<IAiCallHook>())
        {
            var sw = Stopwatch.StartNew();
            HookResult? result;
            try
            {
                result = await hook.AfterAsync(ctx, response, ct);
                sw.Stop();
                _metrics.Record(hook.PluginId, "AfterAi", sw.Elapsed.TotalMilliseconds, failed: false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _metrics.Record(hook.PluginId, "AfterAi", sw.Elapsed.TotalMilliseconds, failed: true);
                _log.LogError(ex, "AfterAi hook {Plugin} threw; failing closed", hook.PluginId);
                return HookResult.ShortCircuit(500,
                    System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"plugin '{hook.PluginId}' failed\"}}"),
                    "application/json");
            }
            if (result is null) continue;
            if (!result.ContinuePipeline) return result;
        }
        return HookResult.Continue();
    }

    private async Task<HookResult> RunBodyHooksAsync<THook>(
        string label,
        PipelineHookContext ctx,
        byte[] body,
        Func<THook, PipelineHookContext, byte[], CancellationToken, Task<HookResult>> invoke,
        bool failOpen,
        CancellationToken ct)
        where THook : class
    {
        var current = body;
        var modified = false;

        foreach (var hook in ResolveHooks<THook>())
        {
            var pluginId = GetPluginId(hook!);
            var sw = Stopwatch.StartNew();
            HookResult? result;
            try
            {
                result = await invoke(hook, ctx, current, ct);
                sw.Stop();
                _metrics.Record(pluginId, label, sw.Elapsed.TotalMilliseconds, failed: false);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _metrics.Record(pluginId, label, sw.Elapsed.TotalMilliseconds, failed: true);
                if (failOpen)
                {
                    _log.LogWarning(ex, "{Hook} on plugin {Plugin} threw; continuing (fail-open)",
                        label, pluginId);
                    continue;
                }
                _log.LogError(ex, "{Hook} on plugin {Plugin} threw; failing closed",
                    label, pluginId);
                return HookResult.ShortCircuit(500,
                    System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"plugin '{pluginId}' failed\"}}"),
                    "application/json");
            }
            if (result is null) continue;
            if (!result.ContinuePipeline) return result;
            if (result.ModifiedBody is { } body2)
            {
                current = body2;
                modified = true;
            }
        }

        return modified ? HookResult.Modify(current) : HookResult.Continue();
    }

    /// Resolve hook implementations from the active request scope, filtered
    /// by the request's <see cref="IPluginEnablement"/> gate. Falling back
    /// to the root container only happens when there's no <c>HttpContext</c>
    /// (e.g. background tasks or tests calling the dispatcher directly),
    /// in which case the no-op <see cref="AlwaysEnabled"/> service treats
    /// every plugin as enabled.
    private IEnumerable<THook> ResolveHooks<THook>() where THook : class
    {
        var sp = _httpContextAccessor.HttpContext?.RequestServices ?? _rootServices;
        var enablement = sp.GetService<IPluginEnablement>() ?? new AlwaysEnabled();
        foreach (var hook in sp.GetServices<THook>())
        {
            var pluginId = GetPluginId(hook!);
            if (enablement.IsEnabled(pluginId)) yield return hook;
        }
    }

    private static string GetPluginId(object hook) => hook switch
    {
        IRequestTranslationHook r => r.PluginId,
        IResponseTranslationHook r => r.PluginId,
        IAiCallHook a => a.PluginId,
        _ => hook.GetType().Name,
    };
}
