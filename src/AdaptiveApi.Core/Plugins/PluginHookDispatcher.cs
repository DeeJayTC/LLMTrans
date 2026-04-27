using AdaptiveApi.Plugins.SDK.Hooks;
using Microsoft.Extensions.Logging;

namespace AdaptiveApi.Core.Plugins;

public sealed class PluginHookDispatcher : IPluginHookDispatcher
{
    private readonly IEnumerable<IRequestTranslationHook> _requestHooks;
    private readonly IEnumerable<IAiCallHook> _aiHooks;
    private readonly IEnumerable<IResponseTranslationHook> _responseHooks;
    private readonly ILogger<PluginHookDispatcher> _log;

    public PluginHookDispatcher(
        IEnumerable<IRequestTranslationHook> requestHooks,
        IEnumerable<IAiCallHook> aiHooks,
        IEnumerable<IResponseTranslationHook> responseHooks,
        ILogger<PluginHookDispatcher> log)
    {
        _requestHooks = requestHooks;
        _aiHooks = aiHooks;
        _responseHooks = responseHooks;
        _log = log;
    }

    public Task<HookResult> RunBeforeRequestTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct) =>
        RunBodyHooksAsync("BeforeRequestTranslation", ctx, body, _requestHooks,
            (h, c, b, t) => h.BeforeAsync(c, b, t), failOpen: true, ct);

    public Task<HookResult> RunAfterRequestTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct) =>
        RunBodyHooksAsync("AfterRequestTranslation", ctx, body, _requestHooks,
            (h, c, b, t) => h.AfterAsync(c, b, t), failOpen: true, ct);

    public Task<HookResult> RunBeforeResponseTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct) =>
        RunBodyHooksAsync("BeforeResponseTranslation", ctx, body, _responseHooks,
            (h, c, b, t) => h.BeforeAsync(c, b, t), failOpen: true, ct);

    public Task<HookResult> RunAfterResponseTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct) =>
        RunBodyHooksAsync("AfterResponseTranslation", ctx, body, _responseHooks,
            (h, c, b, t) => h.AfterAsync(c, b, t), failOpen: true, ct);

    public async Task<HookResult> RunBeforeAiAsync(PipelineHookContext ctx, System.Net.Http.HttpRequestMessage request, CancellationToken ct)
    {
        foreach (var hook in _aiHooks)
        {
            HookResult? result;
            try
            {
                result = await hook.BeforeAsync(ctx, request, ct);
            }
            catch (Exception ex)
            {
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
        foreach (var hook in _aiHooks)
        {
            HookResult? result;
            try
            {
                result = await hook.AfterAsync(ctx, response, ct);
            }
            catch (Exception ex)
            {
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
        IEnumerable<THook> hooks,
        Func<THook, PipelineHookContext, byte[], CancellationToken, Task<HookResult>> invoke,
        bool failOpen,
        CancellationToken ct)
        where THook : class
    {
        var current = body;
        var modified = false;

        foreach (var hook in hooks)
        {
            HookResult? result;
            try
            {
                result = await invoke(hook, ctx, current, ct);
            }
            catch (Exception ex)
            {
                if (failOpen)
                {
                    _log.LogWarning(ex, "{Hook} on plugin {Plugin} threw; continuing (fail-open)",
                        label, GetPluginId(hook));
                    continue;
                }
                _log.LogError(ex, "{Hook} on plugin {Plugin} threw; failing closed",
                    label, GetPluginId(hook));
                return HookResult.ShortCircuit(500,
                    System.Text.Encoding.UTF8.GetBytes($"{{\"error\":\"plugin '{GetPluginId(hook)}' failed\"}}"),
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

    private static string GetPluginId(object hook) => hook switch
    {
        IRequestTranslationHook r => r.PluginId,
        IResponseTranslationHook r => r.PluginId,
        IAiCallHook a => a.PluginId,
        _ => hook.GetType().Name,
    };
}
