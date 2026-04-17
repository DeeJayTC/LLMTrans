using LlmTrans.Core.Routing;

namespace LlmTrans.Core.Pipeline;

/// Default per-provider allowlists for the translation planner.
/// Extended / overridden via proxy rules in M5.
public static class AllowlistCatalog
{
    public static Allowlist Request(RouteKind kind) => kind switch
    {
        RouteKind.OpenAiChat => OpenAiChatRequest,
        RouteKind.OpenAiResponses => OpenAiResponsesRequest,
        RouteKind.AnthropicMessages => AnthropicMessagesRequest,
        RouteKind.Mcp => McpRequest,
        _ => Empty,
    };

    public static Allowlist Response(RouteKind kind) => kind switch
    {
        RouteKind.OpenAiChat => OpenAiChatResponse,
        RouteKind.OpenAiResponses => OpenAiResponsesResponse,
        RouteKind.AnthropicMessages => AnthropicMessagesResponse,
        RouteKind.Mcp => McpResponse,
        _ => Empty,
    };

    public static readonly Allowlist Empty = new();

    public static readonly Allowlist OpenAiChatRequest = new(
        "/messages/*/content",
        "/messages/*/content/*/text",
        "/tools/*/description",
        "/tools/*/function/description",
        "/instructions"
    );

    public static readonly Allowlist OpenAiChatResponse = new(
        "/choices/*/message/content",
        "/choices/*/message/content/*/text"
    );

    public static readonly Allowlist OpenAiResponsesRequest = new(
        "/input/*/content/*/text",
        "/instructions",
        "/tools/*/description"
    );

    public static readonly Allowlist OpenAiResponsesResponse = new(
        "/output/*/content/*/text"
    );

    public static readonly Allowlist AnthropicMessagesRequest = new(
        "/system",
        "/messages/*/content",
        "/messages/*/content/*/text",
        "/tools/*/description"
    );

    public static readonly Allowlist AnthropicMessagesResponse = new(
        "/content/*/text"
    );

    public static readonly Allowlist McpRequest = new(
        "/params/arguments/**"
    );

    public static readonly Allowlist McpResponse = new(
        "/result/content/*/text",
        "/result/tools/*/description",
        "/result/tools/*/inputSchema/properties/*/description",
        "/result/prompts/*/description",
        "/result/messages/*/content/*/text"
    );
}
