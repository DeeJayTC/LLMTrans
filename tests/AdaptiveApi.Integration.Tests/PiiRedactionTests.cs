using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AdaptiveApi.Integration.Tests;

public sealed class PiiRedactionTests : IClassFixture<ProxyTestFactory>
{
    private readonly ProxyTestFactory _factory;

    public PiiRedactionTests(ProxyTestFactory factory)
    {
        _factory = factory;
        _factory.Upstream.Requests.Clear();
        _factory.Upstream.ResponseBody = null;
        _factory.Upstream.ResponseContentType = "application/json";
    }

    [Fact]
    public async Task Request_pii_is_redacted_before_upstream_receives_it()
    {
        using var client = _factory.CreateClient();

        // 1. Create a proxy rule with PII redaction on.
        var createRule = await client.PostAsJsonAsync("/admin/proxy-rules", new
        {
            id = "pii_rule",
            tenantId = Seeder.DevTenantId,
            name = "Redact user PII",
            scopeJson = "{}",
            priority = 10,
            redactPii = true,
        });
        Assert.Equal(HttpStatusCode.Created, createRule.StatusCode);

        // 2. Bind it to the dev route with DE → EN translation.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AdaptiveApiDbContext>();
            var r = await db.Routes.FirstAsync(x => x.Id == Seeder.DevRouteId);
            r.UserLanguage = "de";
            r.LlmLanguage = "en";
            r.Direction = "Bidirectional";
            r.ProxyRuleId = "pii_rule";
            r.TranslatorId = "fake-brackets";
            await db.SaveChangesAsync();
        }

        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(
            """{"id":"r1","choices":[{"index":0,"message":{"role":"assistant","content":"Got it."}}]}""");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent("""
        {
          "model":"gpt-4o-mini",
          "messages":[{"role":"user","content":"Please email ceo@acme.com and call +1 555-867-5309, card 4242 4242 4242 4242."}]
        }
        """, Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // 3. Upstream body must contain NO PII.
        var captured = Assert.Single(_factory.Upstream.Requests);
        var sentBody = captured.BodyAsString()!;
        Assert.DoesNotContain("ceo@acme.com", sentBody);
        Assert.DoesNotContain("555-867-5309", sentBody);
        Assert.DoesNotContain("4242 4242 4242 4242", sentBody);

        // Redaction substitutes are present.
        Assert.Contains("[redacted-email]", sentBody);
        Assert.Contains("[redacted-phone]", sentBody);
        Assert.Contains("[redacted-card]", sentBody);
    }

    [Fact]
    public async Task Redaction_off_by_default_when_no_proxy_rule_set()
    {
        using var client = _factory.CreateClient();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AdaptiveApiDbContext>();
            var r = await db.Routes.FirstAsync(x => x.Id == Seeder.DevRouteId);
            r.UserLanguage = "de";
            r.LlmLanguage = "en";
            r.Direction = "Bidirectional";
            r.ProxyRuleId = null;
            r.TranslatorId = "fake-brackets";
            await db.SaveChangesAsync();
        }

        _factory.Upstream.ResponseBody = Encoding.UTF8.GetBytes(
            """{"id":"r","choices":[{"message":{"role":"assistant","content":"ok"}}]}""");

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v1/{ProxyTestFactory.FixedToken}/chat/completions");
        req.Content = new StringContent(
            """{"model":"x","messages":[{"role":"user","content":"Reach me at alice@example.com"}]}""",
            Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var captured = Assert.Single(_factory.Upstream.Requests);
        // alice@example.com is itself protected as a URL/email placeholder by the tokenizer,
        // so it DOES reach upstream verbatim when redaction is off — the tokenizer preserves
        // the literal bytes via its placeholder/reinjection round-trip.
        Assert.Contains("alice@example.com", captured.BodyAsString()!);
    }
}
