using LlmTrans.Core.Model;

namespace LlmTrans.Core.Routing;

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
    string? ConfigJson = null);

public enum RouteKind
{
    OpenAiChat,
    OpenAiResponses,
    AnthropicMessages,
    Mcp,
    McpTranslate,
    Generic,
}
