using System.Net.Http;

namespace AdaptiveApi.Plugins.SDK.Hooks;

/// Hooks fired around the upstream LLM call.
///
/// <see cref="BeforeAsync"/> runs after request-side translation, with the
/// fully-prepared upstream request in hand. Plugins may mutate headers /
/// content here. <see cref="AfterAsync"/> runs once the upstream response is
/// available, before any response-side translation.
///
/// For streaming responses, <see cref="AfterAsync"/> fires once on the
/// response handle — not per chunk. Per-chunk inspection is out of scope for v1.
public interface IAiCallHook
{
    string PluginId { get; }

    /// Inspect or mutate the upstream request. Throwing or returning a
    /// short-circuit result aborts the call before bytes leave the host.
    Task<HookResult> BeforeAsync(PipelineHookContext context, HttpRequestMessage request, CancellationToken ct);

    /// Observe the upstream response. The body is not yet read; plugins that
    /// need to inspect the body must buffer and replace, which is best done
    /// via <see cref="IResponseTranslationHook"/> instead.
    Task<HookResult> AfterAsync(PipelineHookContext context, HttpResponseMessage response, CancellationToken ct);
}
