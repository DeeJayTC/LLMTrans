namespace AdaptiveApi.Plugins.SDK.Hooks;

/// Hooks fired around the request-side language translation step.
///
/// <see cref="BeforeAsync"/> sees the raw inbound request body, before any
/// translation has run. <see cref="AfterAsync"/> sees the body after the
/// language pipeline has translated user-language fields into LLM-language.
///
/// Use this hook to inspect, redact, or rewrite request bodies. Returning
/// <see cref="HookResult.ShortCircuit"/> from <see cref="BeforeAsync"/> blocks
/// the request before it reaches the upstream LLM.
public interface IRequestTranslationHook
{
    /// Plugin id this hook belongs to. Used by the dispatcher to skip hooks
    /// from disabled plugins.
    string PluginId { get; }

    Task<HookResult> BeforeAsync(PipelineHookContext context, byte[] body, CancellationToken ct);

    Task<HookResult> AfterAsync(PipelineHookContext context, byte[] body, CancellationToken ct);
}
