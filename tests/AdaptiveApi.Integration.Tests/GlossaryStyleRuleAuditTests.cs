using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveApi.Integration.Tests;

public sealed class GlossaryStyleRuleAuditTests : IClassFixture<ProxyTestFactory>
{
    private readonly ProxyTestFactory _factory;

    public GlossaryStyleRuleAuditTests(ProxyTestFactory factory)
    {
        _factory = factory;
        _factory.Upstream.Requests.Clear();
        _factory.Upstream.ResponseBody = null;
        _factory.Upstream.ResponseContentType = "application/json";
        _factory.Recording.Reset();
    }

    [Fact]
    public async Task End_to_end_do_not_translate_term_and_custom_instruction_applied_together()
    {
        using var client = _factory.CreateClient();

        // 1. Create glossary with a do-not-translate entry.
        var createGlossary = await client.PostAsJsonAsync("/admin/glossaries",
            new { id = "gl1", tenantId = Seeder.DevTenantId, name = "Product terms", deeplGlossaryId = (string?)null });
        Assert.Equal(HttpStatusCode.Created, createGlossary.StatusCode);

        var addEntries = await client.PostAsJsonAsync("/admin/glossaries/gl1/entries", new[]
        {
            new { sourceLanguage = "en", targetLanguage = "de",
                  sourceTerm = "pull request", targetTerm = "pull request",
                  caseSensitive = false, doNotTranslate = true },
            // DoNotTranslate terms must be registered per direction — add the reverse too.
            new { sourceLanguage = "de", targetLanguage = "en",
                  sourceTerm = "pull request", targetTerm = "pull request",
                  caseSensitive = false, doNotTranslate = true },
        });
        Assert.Equal(HttpStatusCode.OK, addEntries.StatusCode);

        // 2. Create a style rule with one custom instruction.
        var createStyle = await client.PostAsJsonAsync("/admin/style-rules",
            new { id = "sr1", tenantId = Seeder.DevTenantId, name = "Business formal",
                  language = "de", deeplStyleId = (string?)null, rulesJson = "{}" });
        Assert.Equal(HttpStatusCode.Created, createStyle.StatusCode);

        var addInstr = await client.PostAsJsonAsync("/admin/style-rules/sr1/instructions", new[]
        {
            new { label = "register", prompt = "Use business-formal register throughout.", ordinal = 0 },
        });
        Assert.Equal(HttpStatusCode.OK, addInstr.StatusCode);

        // 3. Point the dev route at both and use the recording translator.
        await ConfigureRouteAsync("de", "en", "Bidirectional",
            glossaryId: "gl1", styleRuleId: "sr1", translatorId: "recording");

        // 4. Issue a chat completion.
        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(
            """{"id":"r1","choices":[{"index":0,"message":{"role":"assistant","content":"Hier sind die pull request items."}}]}""");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"List all pull request items today please."}]}""",
            Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // 5a. Custom instructions + glossary mappings reached the translator verbatim.
        var seen = _factory.Recording.Seen;
        Assert.NotEmpty(seen);
        var first = seen[0];
        Assert.NotNull(first.CustomInstructions);
        Assert.True(first.CustomInstructions!.Any(s => s.IndexOf("business-formal", StringComparison.OrdinalIgnoreCase) >= 0),
            $"expected an instruction containing 'business-formal'; got: [{string.Join(" | ", first.CustomInstructions!)}]");

        // 5b. `pull request` is in the DoNotTranslate set → becomes a placeholder, never reaches target.
        Assert.NotNull(first.DoNotTranslate);
        Assert.Contains(first.DoNotTranslate!, t => t == "pull request");

        // 5c. The upstream received the translated request body; `pull request` passed through verbatim.
        var captured = Assert.Single(_factory.Upstream.Requests);
        var sent = JsonNode.Parse(captured.BodyAsString()!)!;
        var userMsg = sent["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("pull request", userMsg);

        // 5d. Audit was recorded.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdaptiveApiDbContext>();
        var audit = await db.AuditEvents.AsNoTracking()
            .OrderByDescending(e => e.Id).FirstAsync();
        Assert.Equal(200, audit.Status);
        Assert.Equal("Bidirectional", audit.Direction);
        Assert.Equal("gl1", audit.GlossaryId);
        Assert.Equal("sr1", audit.RequestStyleRuleId);
        Assert.Equal("sr1", audit.ResponseStyleRuleId);
        Assert.True(audit.RequestChars > 0);
        Assert.True(audit.ResponseChars > 0);
    }

    [Fact]
    public async Task Proxy_rule_extends_denylist_for_tool_call_args()
    {
        using var client = _factory.CreateClient();

        var createRule = await client.PostAsJsonAsync("/admin/proxy-rules", new
        {
            id = "pr1",
            tenantId = Seeder.DevTenantId,
            name = "Guard company internals",
            scopeJson = "{}",
            denylistJson = """{"toolArgKeys":["internal_note"]}""",
            priority = 10,
        });
        Assert.Equal(HttpStatusCode.Created, createRule.StatusCode);

        await ConfigureRouteAsync("de", "en", "Bidirectional",
            glossaryId: null, styleRuleId: null, translatorId: null, proxyRuleId: "pr1");

        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(
            """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"c","type":"function","function":{"name":"f","arguments":"{\"summary\":\"note contents\",\"internal_note\":\"do not translate me\"}"}}]}}]}""");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"x","messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        var root = JsonNode.Parse(body)!;
        var argsStr = root["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>();
        var args = JsonNode.Parse(argsStr)!;

        Assert.StartsWith("[de]", args["summary"]!.GetValue<string>());
        Assert.Equal("do not translate me", args["internal_note"]!.GetValue<string>());
    }

    private async Task ConfigureRouteAsync(
        string userLang, string llmLang, string direction,
        string? glossaryId, string? styleRuleId, string? translatorId, string? proxyRuleId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AdaptiveApiDbContext>();
        var r = await db.Routes.FirstAsync(x => x.Id == Seeder.DevRouteId);
        r.UserLanguage = userLang;
        r.LlmLanguage = llmLang;
        r.Direction = direction;
        r.GlossaryId = glossaryId;
        r.RequestStyleRuleId = styleRuleId;
        r.ResponseStyleRuleId = styleRuleId;
        r.TranslatorId = translatorId;
        r.ProxyRuleId = proxyRuleId;
        await db.SaveChangesAsync();
    }
}
