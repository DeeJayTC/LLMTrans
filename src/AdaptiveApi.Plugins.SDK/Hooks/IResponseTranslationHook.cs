namespace AdaptiveApi.Plugins.SDK.Hooks;

/// Hooks fired around the response-side language translation step.
///
/// <see cref="BeforeAsync"/> sees the raw upstream response body, before any
/// translation has run. <see cref="AfterAsync"/> sees the body after the
/// language pipeline has translated LLM-language fields back into user-language,
/// just before it is written to the client.
///
/// Streaming responses skip this hook in v1 — they invoke neither the language
/// pipeline nor this hook because translation happens inline on each event.
public interface IResponseTranslationHook
{
    string PluginId { get; }

    Task<HookResult> BeforeAsync(PipelineHookContext context, byte[] body, CancellationToken ct);

    Task<HookResult> AfterAsync(PipelineHookContext context, byte[] body, CancellationToken ct);
}
