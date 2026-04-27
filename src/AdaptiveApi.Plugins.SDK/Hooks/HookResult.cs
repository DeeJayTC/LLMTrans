namespace AdaptiveApi.Plugins.SDK.Hooks;

/// Result returned by a body-mutating hook. Use the static factories to
/// construct instances. Hooks MUST return a result — returning <c>null</c>
/// is treated as <see cref="Continue"/> with no modifications.
///
/// Composition: when multiple plugins implement the same hook, the dispatcher
/// runs them in DI registration order, threading <see cref="ModifiedBody"/>
/// from one to the next. The first hook to return <see cref="ShortCircuit"/>
/// wins; remaining hooks for that point are skipped.
public sealed record HookResult
{
    /// True when the request should continue. False signals short-circuit —
    /// <see cref="ShortCircuitStatus"/> and <see cref="ShortCircuitBody"/> describe the response.
    public bool ContinuePipeline { get; init; }

    /// Replacement body bytes. Null leaves the existing body untouched.
    public byte[]? ModifiedBody { get; init; }

    /// HTTP status code to return when short-circuiting.
    public int? ShortCircuitStatus { get; init; }

    /// Response body to write when short-circuiting.
    public byte[]? ShortCircuitBody { get; init; }

    /// Optional content type to set on a short-circuit response.
    public string? ShortCircuitContentType { get; init; }

    public static HookResult Continue() => new() { ContinuePipeline = true };

    public static HookResult Modify(byte[] body) => new()
    {
        ContinuePipeline = true,
        ModifiedBody = body,
    };

    public static HookResult ShortCircuit(int status, byte[] body, string? contentType = null) => new()
    {
        ContinuePipeline = false,
        ShortCircuitStatus = status,
        ShortCircuitBody = body,
        ShortCircuitContentType = contentType,
    };
}
