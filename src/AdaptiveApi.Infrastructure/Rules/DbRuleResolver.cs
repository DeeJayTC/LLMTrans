using System.Text.Json;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Core.Routing;
using AdaptiveApi.Core.Rules;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Infrastructure.Rules;

public sealed class DbRuleResolver : IRuleResolver
{
    private readonly AdaptiveApiDbContext _db;

    public DbRuleResolver(AdaptiveApiDbContext db) => _db = db;

    public async Task<ResolvedRules> ResolveAsync(RouteConfig route, CancellationToken ct)
    {
        IReadOnlyList<GlossaryTerm> glossary = Array.Empty<GlossaryTerm>();
        string? deeplGlossaryId = null;
        if (!string.IsNullOrEmpty(route.GlossaryId))
        {
            var g = await _db.Glossaries.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == route.GlossaryId && x.TenantId == route.TenantId, ct);
            if (g is not null)
            {
                deeplGlossaryId = g.DeeplGlossaryId;
                glossary = await _db.GlossaryEntries.AsNoTracking()
                    .Where(e => e.GlossaryId == g.Id)
                    .Select(e => new GlossaryTerm(
                        e.SourceLanguage, e.TargetLanguage,
                        e.SourceTerm, e.TargetTerm,
                        e.DoNotTranslate, e.CaseSensitive))
                    .ToListAsync(ct);
            }
        }

        var requestStyle = await ResolveStyleBindingAsync(route.RequestStyleRuleId, route.TenantId, ct);
        var responseStyle = route.ResponseStyleRuleId == route.RequestStyleRuleId
            ? requestStyle
            : await ResolveStyleBindingAsync(route.ResponseStyleRuleId, route.TenantId, ct);

        var reqAllowlist = AllowlistCatalog.Request(route.Kind);
        var respAllowlist = AllowlistCatalog.Response(route.Kind);
        var denylist = ToolArgsDenylist.Default;
        var formality = Formality.Default;
        var redactPii = false;
        string? systemContext = null;

        if (!string.IsNullOrEmpty(route.ProxyRuleId))
        {
            var pr = await _db.ProxyRules.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == route.ProxyRuleId && x.TenantId == route.TenantId, ct);
            if (pr is not null)
            {
                (reqAllowlist, respAllowlist) = ApplyAllowlistOverrides(pr, reqAllowlist, respAllowlist);
                denylist = ApplyDenylistOverrides(pr, denylist);
                if (!string.IsNullOrEmpty(pr.Formality)
                    && Enum.TryParse<Formality>(pr.Formality, true, out var f))
                    formality = f;
                redactPii = pr.RedactPii;
                systemContext = pr.SystemContext;
            }
        }

        return new ResolvedRules(
            Glossary: glossary,
            DeeplGlossaryId: deeplGlossaryId,
            RequestStyle: requestStyle,
            ResponseStyle: responseStyle,
            RequestAllowlist: reqAllowlist,
            ResponseAllowlist: respAllowlist,
            ToolArgsDenylist: denylist,
            Formality: formality,
            RedactPii: redactPii,
            SystemContext: systemContext);
    }

    private async Task<StyleBinding> ResolveStyleBindingAsync(string? styleRuleId, string tenantId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(styleRuleId)) return StyleBinding.Empty;

        var sr = await _db.StyleRules.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == styleRuleId && x.TenantId == tenantId, ct);
        if (sr is null) return StyleBinding.Empty;

        var instructions = await _db.CustomInstructions.AsNoTracking()
            .Where(c => c.StyleRuleId == sr.Id)
            .OrderBy(c => c.Ordinal)
            .Select(c => c.Prompt)
            .ToListAsync(ct);

        return new StyleBinding(sr.Id, sr.DeeplStyleId, instructions);
    }

    private static (Allowlist Req, Allowlist Resp) ApplyAllowlistOverrides(
        ProxyRuleEntity rule, Allowlist baseReq, Allowlist baseResp)
    {
        if (string.IsNullOrWhiteSpace(rule.AllowlistJson)) return (baseReq, baseResp);

        try
        {
            var doc = JsonDocument.Parse(rule.AllowlistJson);
            var root = doc.RootElement;

            var req = ExtractPatterns(root, "request");
            var resp = ExtractPatterns(root, "response");

            var reqAll = req.Count > 0 ? new Allowlist(req.ToArray()) : baseReq;
            var respAll = resp.Count > 0 ? new Allowlist(resp.ToArray()) : baseResp;
            return (reqAll, respAll);
        }
        catch (JsonException)
        {
            return (baseReq, baseResp);
        }
    }

    private static List<string> ExtractPatterns(JsonElement root, string field)
    {
        var list = new List<string>();
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(field, out var arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in arr.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String)
                    list.Add(v.GetString()!);
        }
        return list;
    }

    private static ToolArgsDenylist ApplyDenylistOverrides(ProxyRuleEntity rule, ToolArgsDenylist baseDenylist)
    {
        if (string.IsNullOrWhiteSpace(rule.DenylistJson)) return baseDenylist;
        try
        {
            var doc = JsonDocument.Parse(rule.DenylistJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return baseDenylist;

            var extraKeys = new List<string>();
            if (root.TryGetProperty("toolArgKeys", out var k) && k.ValueKind == JsonValueKind.Array)
                foreach (var v in k.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String) extraKeys.Add(v.GetString()!);

            if (extraKeys.Count == 0) return baseDenylist;

            var defaultKeys = new[]
            {
                "id","uuid","email","slug","key","locale","tz","url","href","path",
            };
            var patterns = new[]
            {
                new System.Text.RegularExpressions.Regex("^.*_id$", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                new System.Text.RegularExpressions.Regex("^.*_code$", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                new System.Text.RegularExpressions.Regex("^.*Id$"),
                new System.Text.RegularExpressions.Regex("^.*Code$"),
            };
            return new ToolArgsDenylist(defaultKeys.Concat(extraKeys), patterns);
        }
        catch (JsonException)
        {
            return baseDenylist;
        }
    }
}
