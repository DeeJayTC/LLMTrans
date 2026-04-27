using Microsoft.AspNetCore.Http;

namespace AdaptiveApi.Plugins.SDK.Hooks;

/// Per-request context shared across all six hook points so plugins can
/// correlate their before/after observations. Plugins must not mutate the
/// <see cref="HttpContext"/>; use the dedicated payload parameters on each
/// hook to alter the body or short-circuit.
///
/// <param name="HttpContext">The active ASP.NET request. Available for read-only access (route id, headers).</param>
/// <param name="RouteId">Stable route id from the resolved route configuration.</param>
/// <param name="TenantId">Tenant id resolved for this request.</param>
/// <param name="ProviderId">Adapter handling the request (e.g. "openai.chat", "anthropic.messages", "generic", "mcp").</param>
/// <param name="UserLanguage">User-facing language code (input direction source / output direction target).</param>
/// <param name="LlmLanguage">Upstream LLM language code (input direction target / output direction source).</param>
/// <param name="Direction">Direction mode for the route ("Bidirectional" / "RequestOnly" / "ResponseOnly" / "Off").</param>
/// <param name="Properties">Free-form bag for plugins to share state across hook points within a single request. Keys are plugin-namespaced strings.</param>
public sealed record PipelineHookContext(
    HttpContext HttpContext,
    string RouteId,
    string TenantId,
    string ProviderId,
    string UserLanguage,
    string LlmLanguage,
    string Direction,
    IDictionary<string, object?> Properties);
