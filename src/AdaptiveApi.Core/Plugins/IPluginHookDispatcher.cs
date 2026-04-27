using AdaptiveApi.Plugins.SDK.Hooks;

namespace AdaptiveApi.Core.Plugins;

/// Fans hook invocations out to every registered <see cref="IRequestTranslationHook"/>,
/// <see cref="IAiCallHook"/>, and <see cref="IResponseTranslationHook"/>. Adapters
/// call into this dispatcher at the six pipeline points instead of resolving hook
/// services themselves.
///
/// Composition rules:
///   * Hooks run in DI registration order.
///   * Body-mutating returns (<see cref="HookResult.Modify"/>) are threaded into the next hook.
///   * The first hook to short-circuit wins; subsequent hooks at that point are skipped.
///   * Throwing hooks fail-open for translation hook points and fail-closed for AI hook points.
public interface IPluginHookDispatcher
{
    Task<HookResult> RunBeforeRequestTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct);
    Task<HookResult> RunAfterRequestTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct);

    Task<HookResult> RunBeforeAiAsync(PipelineHookContext ctx, System.Net.Http.HttpRequestMessage request, CancellationToken ct);
    Task<HookResult> RunAfterAiAsync(PipelineHookContext ctx, System.Net.Http.HttpResponseMessage response, CancellationToken ct);

    Task<HookResult> RunBeforeResponseTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct);
    Task<HookResult> RunAfterResponseTranslationAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct);
}
