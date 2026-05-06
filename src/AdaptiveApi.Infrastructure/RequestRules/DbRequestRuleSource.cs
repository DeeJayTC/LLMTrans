using System.Text.Json;
using System.Text.RegularExpressions;
using AdaptiveApi.Core.Pipeline;
using AdaptiveApi.Core.RequestRules;
using AdaptiveApi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AdaptiveApi.Infrastructure.RequestRules;

/// Loads <see cref="RequestRuleEntity"/> rows from the DB, compiles them
/// through <see cref="SafeRegex"/>, and caches the compiled view per request
/// scope so the per-hook calls don't re-query.
public interface IRequestRuleSource
{
    /// Returns enabled rules for the request's tenant + route, in priority
    /// order, compiled and ready to apply. Skips rows whose pattern fails to
    /// compile (logs a warning but doesn't fail the request).
    Task<IReadOnlyList<CompiledRequestRule>> GetForRequestAsync(
        string tenantId, string routeId, CancellationToken ct);
}

public sealed class DbRequestRuleSource : IRequestRuleSource
{
    private readonly AdaptiveApiDbContext _db;
    private readonly ILogger<DbRequestRuleSource> _log;
    private IReadOnlyList<CompiledRequestRule>? _cached;
    private (string Tenant, string Route)? _cachedKey;

    public DbRequestRuleSource(AdaptiveApiDbContext db, ILogger<DbRequestRuleSource> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<IReadOnlyList<CompiledRequestRule>> GetForRequestAsync(
        string tenantId, string routeId, CancellationToken ct)
    {
        if (_cachedKey == (tenantId, routeId) && _cached is not null)
            return _cached;

        // Two queries logically: tenant-wide rules (RouteId == null) and
        // route-specific. Single round-trip with an OR — typical row count is
        // tiny, no point optimising further.
        var rows = await _db.RequestRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Enabled
                        && (r.RouteId == null || r.RouteId == routeId))
            .OrderBy(r => r.Priority).ThenBy(r => r.Id)
            .ToListAsync(ct);

        var compiled = new List<CompiledRequestRule>(rows.Count);
        foreach (var r in rows)
        {
            if (TryCompile(r, out var c)) compiled.Add(c);
        }
        _cached = compiled;
        _cachedKey = (tenantId, routeId);
        return compiled;
    }

    private bool TryCompile(RequestRuleEntity r, out CompiledRequestRule compiled)
    {
        compiled = default!;
        if (!TryParseScope(r.Scope, out var scope))
        {
            _log.LogWarning("request rule {Id} has unknown scope '{Scope}', skipping", r.Id, r.Scope);
            return false;
        }
        if (!TryParseAction(r.Action, out var action))
        {
            _log.LogWarning("request rule {Id} has unknown action '{Action}', skipping", r.Id, r.Action);
            return false;
        }

        var opts = RegexOptions.None;
        if (!string.IsNullOrEmpty(r.FlagsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(r.FlagsJson);
                if (doc.RootElement.TryGetProperty("caseInsensitive", out var ci) && ci.ValueKind == JsonValueKind.True)
                    opts |= RegexOptions.IgnoreCase;
                if (doc.RootElement.TryGetProperty("multiline", out var ml) && ml.ValueKind == JsonValueKind.True)
                    opts |= RegexOptions.Multiline;
            }
            catch (JsonException) { /* leave defaults */ }
        }

        Regex regex;
        try { regex = SafeRegex.Compile(r.Pattern, opts); }
        catch (Exception ex) when (ex is ArgumentException or RegexParseException)
        {
            _log.LogWarning(ex, "request rule {Id} pattern rejected, skipping", r.Id);
            return false;
        }

        compiled = new CompiledRequestRule(
            Id: r.Id,
            TenantId: r.TenantId,
            RouteId: r.RouteId,
            Name: r.Name,
            Scope: scope,
            HeaderName: r.HeaderName,
            Match: regex,
            Action: action,
            BlockStatus: r.BlockStatus,
            ActionPayload: r.ActionPayload,
            ActionHeader: r.ActionHeader,
            Priority: r.Priority);
        return true;
    }

    private static bool TryParseScope(string s, out RequestRuleScope scope) => s switch
    {
        "request-body" => Out(RequestRuleScope.RequestBody, out scope),
        "response-body" => Out(RequestRuleScope.ResponseBody, out scope),
        "request-header" => Out(RequestRuleScope.RequestHeader, out scope),
        "response-header" => Out(RequestRuleScope.ResponseHeader, out scope),
        "path" => Out(RequestRuleScope.Path, out scope),
        _ => Out(default, out scope, ok: false),
    };

    private static bool TryParseAction(string s, out RequestRuleAction action) => s switch
    {
        "block" => Out(RequestRuleAction.Block, out action),
        "replace" => Out(RequestRuleAction.Replace, out action),
        "set-header" => Out(RequestRuleAction.SetHeader, out action),
        "log" => Out(RequestRuleAction.Log, out action),
        _ => Out(default, out action, ok: false),
    };

    private static bool Out<T>(T value, out T target, bool ok = true)
    {
        target = value;
        return ok;
    }
}
