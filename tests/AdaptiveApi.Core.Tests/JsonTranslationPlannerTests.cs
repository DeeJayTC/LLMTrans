using System.Text.Json.Nodes;
using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Core.Routing;

namespace AdaptiveApi.Core.Tests;

public sealed class JsonTranslationPlannerTests
{
    [Fact]
    public void Openai_chat_request_plans_message_contents_but_not_keys_or_roles()
    {
        var root = JsonNode.Parse(
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"ok"}]}""")!;

        var sites = JsonTranslationPlanner.Plan(root, AllowlistCatalog.OpenAiChatRequest);

        Assert.Equal(2, sites.Count);
        Assert.Contains(sites, s => s.PathExpression == "/messages/0/content" && s.Source == "hi");
        Assert.Contains(sites, s => s.PathExpression == "/messages/1/content" && s.Source == "ok");
    }

    [Fact]
    public void Apply_mutates_original_tree()
    {
        var root = JsonNode.Parse("""{"messages":[{"role":"user","content":"hi"}]}""")!;
        var sites = JsonTranslationPlanner.Plan(root, AllowlistCatalog.OpenAiChatRequest);
        var site = Assert.Single(sites);
        site.Apply("hallo");
        Assert.Equal("hallo", root["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Openai_response_planner_finds_choice_contents()
    {
        var root = JsonNode.Parse(
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"Hello, world!"}}]}""")!;

        var sites = JsonTranslationPlanner.Plan(root, AllowlistCatalog.OpenAiChatResponse);
        var site = Assert.Single(sites);
        Assert.Equal("/choices/0/message/content", site.PathExpression);
        Assert.Equal("Hello, world!", site.Source);
    }

    [Fact]
    public void Ignores_structural_keys_like_role_and_model()
    {
        var root = JsonNode.Parse(
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}""")!;
        var sites = JsonTranslationPlanner.Plan(root, AllowlistCatalog.OpenAiChatRequest);
        Assert.DoesNotContain(sites, s => s.Source == "gpt-4o-mini");
        Assert.DoesNotContain(sites, s => s.Source == "user");
    }

    [Fact]
    public void Tool_description_is_planned()
    {
        var root = JsonNode.Parse(
            """{"tools":[{"type":"function","function":{"name":"x","description":"Do a thing"}}]}""")!;
        var sites = JsonTranslationPlanner.Plan(root, AllowlistCatalog.OpenAiChatRequest);
        var site = Assert.Single(sites);
        Assert.Equal("Do a thing", site.Source);
    }

    [Theory]
    [InlineData(RouteKind.OpenAiChat)]
    [InlineData(RouteKind.AnthropicMessages)]
    public void Allowlist_catalog_returns_non_empty(RouteKind kind)
    {
        Assert.NotNull(AllowlistCatalog.Request(kind));
        Assert.NotNull(AllowlistCatalog.Response(kind));
    }
}
