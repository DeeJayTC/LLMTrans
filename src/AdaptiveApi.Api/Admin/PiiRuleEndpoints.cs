using System.Text.Json;
using System.Text.RegularExpressions;
using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Api.Admin;

/// Tenant-scoped custom PII rules. CRUD plus a tester endpoint that runs a
/// candidate rule against sample text without persisting anything.
public static class PiiRuleEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/pii-rules");
        g.MapPost("/", Create);
        g.MapGet("/", List);
        g.MapGet("/{id}", Get);
        g.MapPatch("/{id}", Update);
        g.MapDelete("/{id}", Delete);
        g.MapPost("/test", Test);
    }

    public sealed record FlagsDto(bool CaseInsensitive = false, bool Multiline = false, bool LuhnValidate = false);

    public sealed record CreateDto(string Id, string TenantId, string Name, string Pattern, string Replacement,
        string? Description = null, FlagsDto? Flags = null, bool Enabled = true);

    public sealed record UpdateDto(string? Name, string? Pattern, string? Replacement,
        string? Description, FlagsDto? Flags, bool? Enabled);

    public sealed record RuleDto(string Id, string TenantId, string Name, string? Description,
        string Pattern, string Replacement, FlagsDto Flags, bool Enabled,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public sealed record TestRequest(
        string Text,
        IReadOnlyList<string>? PackSlugs = null,
        IReadOnlyList<string>? RuleIds = null,
        IReadOnlyList<string>? DisabledDetectors = null,
        FlagsDto? AdHocFlags = null,
        string? AdHocPattern = null,
        string? AdHocReplacement = null,
        string? AdHocKind = null,
        string? TenantId = null);

    public sealed record TestMatch(string Kind, string Replacement, int Start, int Length, string Match);

    public sealed record TestResponse(
        string RedactedText,
        IReadOnlyList<TestMatch> Matches,
        IReadOnlyList<string> Errors);

    private static async Task<IResult> Create(CreateDto dto, AdaptiveApiDbContext db, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == dto.TenantId, ct))
            return Results.BadRequest(new { error = "tenant_missing" });
        if (await db.PiiRules.AnyAsync(x => x.Id == dto.Id, ct))
            return Results.Conflict(new { error = "pii_rule_exists" });

        if (!IsValidRegex(dto.Pattern, out var error))
            return Results.BadRequest(new { error = "invalid_regex", message = error });

        var now = DateTimeOffset.UtcNow;
        var entity = new PiiRuleEntity
        {
            Id = dto.Id,
            TenantId = dto.TenantId,
            Name = dto.Name,
            Description = dto.Description,
            Pattern = dto.Pattern,
            Replacement = dto.Replacement,
            FlagsJson = SerializeFlags(dto.Flags),
            Enabled = dto.Enabled,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.PiiRules.Add(entity);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/admin/pii-rules/{dto.Id}", ToDto(entity));
    }

    private static async Task<IResult> List(string? tenantId, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var q = db.PiiRules.AsNoTracking();
        if (!string.IsNullOrEmpty(tenantId)) q = q.Where(x => x.TenantId == tenantId);
        var list = await q.OrderBy(x => x.Name).ToListAsync(ct);
        return Results.Ok(list.Select(ToDto).ToList());
    }

    private static async Task<IResult> Get(string id, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var r = await db.PiiRules.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? Results.NotFound() : Results.Ok(ToDto(r));
    }

    private static async Task<IResult> Update(string id, UpdateDto dto, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var r = await db.PiiRules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Results.NotFound();

        if (dto.Pattern is not null && !IsValidRegex(dto.Pattern, out var error))
            return Results.BadRequest(new { error = "invalid_regex", message = error });

        if (dto.Name is not null) r.Name = dto.Name;
        if (dto.Description is not null) r.Description = dto.Description;
        if (dto.Pattern is not null) r.Pattern = dto.Pattern;
        if (dto.Replacement is not null) r.Replacement = dto.Replacement;
        if (dto.Flags is not null) r.FlagsJson = SerializeFlags(dto.Flags);
        if (dto.Enabled.HasValue) r.Enabled = dto.Enabled.Value;
        r.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(r));
    }

    private static async Task<IResult> Delete(string id, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var r = await db.PiiRules.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Results.NotFound();
        db.PiiRules.Remove(r);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// Run a candidate detector configuration against sample text and report what
    /// would be redacted. Persists nothing. Lets the admin UI validate a regex
    /// before saving and lets users preview the effect of toggling packs.
    private static async Task<IResult> Test(TestRequest req, AdaptiveApiDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(req.Text))
            return Results.Ok(new TestResponse(string.Empty, Array.Empty<TestMatch>(), Array.Empty<string>()));

        var errors = new List<string>();
        var detectors = new List<PiiDetector>();
        var disabled = new HashSet<string>(req.DisabledDetectors ?? Array.Empty<string>(), StringComparer.Ordinal);

        if (req.PackSlugs is { Count: > 0 })
        {
            var packs = await db.PiiPacks.AsNoTracking()
                .Where(p => req.PackSlugs.Contains(p.Slug))
                .OrderBy(p => p.Ordinal)
                .ToListAsync(ct);
            foreach (var pack in packs)
            {
                foreach (var d in PiiDetectorSerializer.Deserialize(pack.DetectorsJson))
                {
                    if (disabled.Contains($"{pack.Slug}:{d.Kind}")) continue;
                    try { detectors.Add(PiiDetectorSerializer.ToDetector(d)); }
                    catch (RegexParseException ex) { errors.Add($"{pack.Slug}:{d.Kind}: {ex.Message}"); }
                }
            }
        }

        if (req.RuleIds is { Count: > 0 })
        {
            var query = db.PiiRules.AsNoTracking().Where(r => req.RuleIds.Contains(r.Id) && r.Enabled);
            if (!string.IsNullOrEmpty(req.TenantId))
                query = query.Where(r => r.TenantId == req.TenantId);
            var rules = await query.ToListAsync(ct);
            foreach (var rule in rules)
            {
                if (TryCompileRule(rule, out var det, out var err))
                    detectors.Add(det);
                else errors.Add($"{rule.Name}: {err}");
            }
        }

        if (!string.IsNullOrEmpty(req.AdHocPattern) && !string.IsNullOrEmpty(req.AdHocReplacement))
        {
            try
            {
                var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                if (req.AdHocFlags?.CaseInsensitive == true) opts |= RegexOptions.IgnoreCase;
                if (req.AdHocFlags?.Multiline == true) opts |= RegexOptions.Multiline;
                detectors.Add(new PiiDetector(
                    req.AdHocKind ?? "AD_HOC",
                    req.AdHocReplacement,
                    new Regex(req.AdHocPattern, opts),
                    req.AdHocFlags?.LuhnValidate ?? false));
            }
            catch (RegexParseException ex) { errors.Add($"adHoc: {ex.Message}"); }
        }

        var matches = new List<TestMatch>();
        var working = req.Text;
        foreach (var det in detectors)
        {
            foreach (Match m in det.Regex.Matches(req.Text))
            {
                if (det.LuhnValidate && !LuhnValid(m.Value)) continue;
                matches.Add(new TestMatch(det.Kind, det.Replacement, m.Index, m.Length, m.Value));
            }
            working = det.Regex.Replace(working, m =>
            {
                if (det.LuhnValidate && !LuhnValid(m.Value)) return m.Value;
                return det.Replacement;
            });
        }

        return Results.Ok(new TestResponse(working, matches, errors));
    }

    private static bool TryCompileRule(PiiRuleEntity rule, out PiiDetector detector, out string? error)
    {
        try
        {
            var opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            var luhn = false;
            if (!string.IsNullOrWhiteSpace(rule.FlagsJson))
            {
                using var doc = JsonDocument.Parse(rule.FlagsJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("caseInsensitive", out var ci) && ci.ValueKind == JsonValueKind.True)
                        opts |= RegexOptions.IgnoreCase;
                    if (root.TryGetProperty("multiline", out var ml) && ml.ValueKind == JsonValueKind.True)
                        opts |= RegexOptions.Multiline;
                    if (root.TryGetProperty("luhnValidate", out var lv) && lv.ValueKind == JsonValueKind.True)
                        luhn = true;
                }
            }
            detector = new PiiDetector(rule.Name, rule.Replacement, new Regex(rule.Pattern, opts), luhn);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            detector = default!;
            error = ex.Message;
            return false;
        }
    }

    private static bool IsValidRegex(string pattern, out string? error)
    {
        try
        {
            _ = new Regex(pattern);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? SerializeFlags(FlagsDto? flags)
    {
        if (flags is null) return null;
        if (!flags.CaseInsensitive && !flags.Multiline && !flags.LuhnValidate) return null;
        return JsonSerializer.Serialize(new
        {
            caseInsensitive = flags.CaseInsensitive,
            multiline = flags.Multiline,
            luhnValidate = flags.LuhnValidate,
        });
    }

    private static FlagsDto ParseFlags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FlagsDto();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new FlagsDto(
                CaseInsensitive: root.TryGetProperty("caseInsensitive", out var ci) && ci.ValueKind == JsonValueKind.True,
                Multiline: root.TryGetProperty("multiline", out var ml) && ml.ValueKind == JsonValueKind.True,
                LuhnValidate: root.TryGetProperty("luhnValidate", out var lv) && lv.ValueKind == JsonValueKind.True);
        }
        catch (JsonException) { return new FlagsDto(); }
    }

    private static RuleDto ToDto(PiiRuleEntity r) => new(
        r.Id, r.TenantId, r.Name, r.Description,
        r.Pattern, r.Replacement, ParseFlags(r.FlagsJson),
        r.Enabled, r.CreatedAt, r.UpdatedAt);

    private static bool LuhnValid(string candidate)
    {
        var digits = 0;
        var sum = 0;
        var even = false;
        for (var i = candidate.Length - 1; i >= 0; i--)
        {
            var c = candidate[i];
            if (!char.IsDigit(c)) continue;
            var d = c - '0';
            if (even)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            even = !even;
            digits++;
        }
        return digits >= 12 && sum % 10 == 0;
    }
}
