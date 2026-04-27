using AdaptiveApi.Core.Model;

namespace AdaptiveApi.Core.Routing;

public sealed record RouteConfig(
    string RouteId,
    string TenantId,
    RouteKind Kind,
    Uri UpstreamBaseUrl,
    LanguageCode UserLanguage,
    LanguageCode LlmLanguage,
    DirectionMode Direction,
    string? TranslatorId,
    string? GlossaryId,
    /// Style rule applied to the user → LLM (request) direction.
    string? RequestStyleRuleId,
    /// Style rule applied to the LLM → user (response) direction.
    string? ResponseStyleRuleId,
    string? ProxyRuleId,
    string? ConfigJson = null,
    /// DeepL Translation Memory UUID bound to this route. When set, DeepL
    /// translation calls are dispatched via the v2 HTTP API (not the SDK)
    /// because the SDK does not yet expose `translation_memory_id`.
    string? TranslationMemoryId = null,
    /// Optional fuzzy-match threshold (0–100) override. Default 75 server-side.
    int? TranslationMemoryThreshold = null);

public enum RouteKind
{
    OpenAiChat,
    OpenAiResponses,
    AnthropicMessages,
    Mcp,
    McpTranslate,
    Generic,
}
