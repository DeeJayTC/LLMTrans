using System.Text.RegularExpressions;
using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdaptiveApi.Api.Admin;

/// CRUD + tester for tenant-managed regex rules. The runtime hook lives in
/// the infrastructure layer; these endpoints are the data-plane surface the
/// admin UI talks to.
public static class RequestRuleEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/request-rules");
        g.MapGet("", List);
        g.MapPost("", Create);
        g.MapPatch("/{id}", Update);
        g.MapDelete("/{id}", Delete);
        g.MapPost("/test", Test);
    }

    public sealed record RuleDto(
        string Id, string TenantId, string? RouteId,
        string Name, string? Description,
        string Scope, string? HeaderName,
        string Pattern, string? FlagsJson,
        string Action, int? BlockStatus,
        string? ActionPayload, string? ActionHeader,
        int Priority, bool Enabled,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    public sealed record CreateRuleDto(
        string Id, string TenantId, string? RouteId,
        string Name, string? Description,
        string Scope, string? HeaderName,
        string Pattern, string? FlagsJson,
        string Action, int? BlockStatus,
        string? ActionPayload, string? ActionHeader,
        int Priority, bool? Enabled);

    public sealed record UpdateRuleDto(
        string? RouteId, string? Name, string? Description,
        string? Scope, string? HeaderName,
        string? Pattern, string? FlagsJson,
        string? Action, int? BlockStatus,
        string? ActionPayload, string? ActionHeader,
        int? Priority, bool? Enabled);

    private static async Task<IResult> List(
        AdaptiveApiDbContext db, string? tenantId, CancellationToken ct)
    {
        var q = db.RequestRules.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(tenantId)) q = q.Where(r => r.TenantId == tenantId);
        var rows = await q.OrderBy(r => r.Priority).ThenBy(r => r.Id).ToListAsync(ct);
        return Results.Ok(rows.Select(ToDto));
    }

    private static async Task<IResult> Create(
        [FromBody] CreateRuleDto dto, AdaptiveApiDbContext db, CancellationToken ct)
    {
        if (await db.RequestRules.AnyAsync(r => r.Id == dto.Id, ct))
            return Results.Conflict(new { error = "rule_exists" });
        if (!await db.Tenants.AnyAsync(t => t.Id == dto.TenantId, ct))
            return Results.BadRequest(new { error = "tenant_missing" });

        // Up-front compile so a tenant can't persist a pattern the executor
        // will reject at runtime — bad regex shows as a 400 here instead.
        if (CompileError(dto.Pattern, dto.FlagsJson) is { } err)
            return Results.BadRequest(new { error = "invalid_pattern", message = err });

        var now = DateTimeOffset.UtcNow;
        db.RequestRules.Add(new RequestRuleEntity
        {
            Id = dto.Id,
            TenantId = dto.TenantId,
            RouteId = NullIfEmpty(dto.RouteId),
            Name = dto.Name,
            Description = dto.Description,
            Scope = dto.Scope,
            HeaderName = NullIfEmpty(dto.HeaderName),
            Pattern = dto.Pattern,
            FlagsJson = dto.FlagsJson,
            Action = dto.Action,
            BlockStatus = dto.BlockStatus,
            ActionPayload = dto.ActionPayload,
            ActionHeader = NullIfEmpty(dto.ActionHeader),
            Priority = dto.Priority,
            Enabled = dto.Enabled ?? true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        var saved = await db.RequestRules.FirstAsync(r => r.Id == dto.Id, ct);
        return Results.Created($"/admin/request-rules/{dto.Id}", ToDto(saved));
    }

    private static async Task<IResult> Update(
        string id, [FromBody] UpdateRuleDto dto, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var row = await db.RequestRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return Results.NotFound();

        var pattern = dto.Pattern ?? row.Pattern;
        var flags = dto.FlagsJson ?? row.FlagsJson;
        if (dto.Pattern is not null || dto.FlagsJson is not null)
        {
            if (CompileError(pattern, flags) is { } err)
                return Results.BadRequest(new { error = "invalid_pattern", message = err });
        }

        if (dto.RouteId is not null) row.RouteId = NullIfEmpty(dto.RouteId);
        if (dto.Name is not null) row.Name = dto.Name;
        if (dto.Description is not null) row.Description = dto.Description;
        if (dto.Scope is not null) row.Scope = dto.Scope;
        if (dto.HeaderName is not null) row.HeaderName = NullIfEmpty(dto.HeaderName);
        if (dto.Pattern is not null) row.Pattern = dto.Pattern;
        if (dto.FlagsJson is not null) row.FlagsJson = dto.FlagsJson;
        if (dto.Action is not null) row.Action = dto.Action;
        if (dto.BlockStatus is not null) row.BlockStatus = dto.BlockStatus;
        if (dto.ActionPayload is not null) row.ActionPayload = dto.ActionPayload;
        if (dto.ActionHeader is not null) row.ActionHeader = NullIfEmpty(dto.ActionHeader);
        if (dto.Priority is not null) row.Priority = dto.Priority.Value;
        if (dto.Enabled is not null) row.Enabled = dto.Enabled.Value;
        row.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(row));
    }

    private static async Task<IResult> Delete(
        string id, AdaptiveApiDbContext db, CancellationToken ct)
    {
        var row = await db.RequestRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return Results.NotFound();
        db.RequestRules.Remove(row);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    public sealed record TestDto(string Pattern, string? FlagsJson, string SampleText, string? Replacement);

    public sealed record TestResponse(bool Match, string? Replaced, string? Error);

    private static IResult Test([FromBody] TestDto body)
    {
        var opts = ParseFlags(body.FlagsJson);
        Regex rx;
        try { rx = SafeRegex.Compile(body.Pattern, opts); }
        catch (Exception ex) when (ex is ArgumentException or RegexParseException)
        {
            return Results.Ok(new TestResponse(false, null, ex.Message));
        }

        try
        {
            var m = rx.IsMatch(body.SampleText);
            string? replaced = null;
            if (m && body.Replacement is { } r) replaced = rx.Replace(body.SampleText, r);
            return Results.Ok(new TestResponse(m, replaced, null));
        }
        catch (RegexMatchTimeoutException)
        {
            return Results.Ok(new TestResponse(false, null, "match timed out (pattern likely pathological)"));
        }
    }

    private static string? CompileError(string pattern, string? flagsJson)
    {
        var opts = ParseFlags(flagsJson);
        try { _ = SafeRegex.Compile(pattern, opts); return null; }
        catch (Exception ex) when (ex is ArgumentException or RegexParseException)
        {
            return ex.Message;
        }
    }

    private static RegexOptions ParseFlags(string? flagsJson)
    {
        var opts = RegexOptions.None;
        if (string.IsNullOrEmpty(flagsJson)) return opts;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(flagsJson);
            if (doc.RootElement.TryGetProperty("caseInsensitive", out var ci) && ci.ValueKind == System.Text.Json.JsonValueKind.True)
                opts |= RegexOptions.IgnoreCase;
            if (doc.RootElement.TryGetProperty("multiline", out var ml) && ml.ValueKind == System.Text.Json.JsonValueKind.True)
                opts |= RegexOptions.Multiline;
        }
        catch (System.Text.Json.JsonException) { }
        return opts;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static RuleDto ToDto(RequestRuleEntity r) => new(
        r.Id, r.TenantId, r.RouteId, r.Name, r.Description,
        r.Scope, r.HeaderName, r.Pattern, r.FlagsJson,
        r.Action, r.BlockStatus, r.ActionPayload, r.ActionHeader,
        r.Priority, r.Enabled, r.CreatedAt, r.UpdatedAt);
}
