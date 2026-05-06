using System.Text;
using AdaptiveApi.Core.RequestRules;
using AdaptiveApi.Plugins.SDK.Hooks;
using Microsoft.Extensions.Logging;

namespace AdaptiveApi.Infrastructure.RequestRules;

/// Built-in hook that runs UI-managed regex rules on every proxied request,
/// without an actual plugin DLL. Implements the same SDK hook contracts a
/// third-party plugin would, so it lights up across every adapter via the
/// existing dispatcher.
///
/// All three hook contracts share one source-of-truth executor; the three
/// classes below differ only in which scope they apply at which point.
public sealed class RequestRuleRequestHook : IRequestTranslationHook
{
    private readonly IRequestRuleSource _source;
    private readonly ILogger<RequestRuleRequestHook> _log;
    public RequestRuleRequestHook(IRequestRuleSource source, ILogger<RequestRuleRequestHook> log)
    {
        _source = source;
        _log = log;
    }

    public string PluginId => "builtin.request-rules";

    public async Task<HookResult> BeforeAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct)
    {
        var rules = await _source.GetForRequestAsync(ctx.TenantId, ctx.RouteId, ct);
        if (rules.Count == 0) return HookResult.Continue();

        // Headers / path are evaluated in this single pre-translation pass.
        // Body rules are also applied here so a Block fires before any
        // expensive translation work happens downstream.
        if (TryHeaderShortCircuit(rules, ctx, RequestRuleScope.RequestHeader, out var hdrResult))
            return hdrResult;
        if (TryPathShortCircuit(rules, ctx, out var pathResult))
            return pathResult;

        var bodyResult = RequestRuleExecutor.ApplyToBody(rules, RequestRuleScope.RequestBody, body);
        ApplyHeaders(ctx, bodyResult.HeadersToSet);
        LogMatches(rules, RequestRuleScope.RequestBody, body, ctx);

        if (bodyResult.Blocked) return ToShortCircuit(bodyResult);
        return bodyResult.ModifiedBody is not null
            ? HookResult.Modify(bodyResult.ModifiedBody)
            : HookResult.Continue();
    }

    public Task<HookResult> AfterAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct)
        => Task.FromResult(HookResult.Continue());

    private bool TryHeaderShortCircuit(IReadOnlyList<CompiledRequestRule> rules,
        PipelineHookContext ctx, RequestRuleScope scope, out HookResult result)
    {
        var http = ctx.HttpContext;
        foreach (var rule in rules)
        {
            if (rule.Scope != scope) continue;
            if (string.IsNullOrEmpty(rule.HeaderName)) continue;
            var value = http.Request.Headers.TryGetValue(rule.HeaderName!, out var v) ? v.ToString() : "";
            var sub = RequestRuleExecutor.ApplyToString(new[] { rule }, scope, value);
            if (sub.Blocked) { result = ToShortCircuit(sub); return true; }
            ApplyHeaders(ctx, sub.HeadersToSet);
        }
        result = default!;
        return false;
    }

    private bool TryPathShortCircuit(IReadOnlyList<CompiledRequestRule> rules,
        PipelineHookContext ctx, out HookResult result)
    {
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        var sub = RequestRuleExecutor.ApplyToString(rules, RequestRuleScope.Path, path);
        if (sub.Blocked) { result = ToShortCircuit(sub); return true; }
        ApplyHeaders(ctx, sub.HeadersToSet);
        result = default!;
        return false;
    }

    private void LogMatches(IReadOnlyList<CompiledRequestRule> rules, RequestRuleScope scope,
        byte[] bodyBytes, PipelineHookContext ctx)
    {
        if (bodyBytes.Length == 0) return;
        var s = Encoding.UTF8.GetString(bodyBytes);
        foreach (var r in RequestRuleExecutor.CollectLogMatches(rules, scope, s))
        {
            _log.LogInformation(
                "request rule {RuleId} ({Name}) matched on {Scope} for route {RouteId}",
                r.Id, r.Name, r.Scope, ctx.RouteId);
        }
    }

    private static void ApplyHeaders(PipelineHookContext ctx, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;
        // Defer to OnStarting so headers reach the response even when later
        // hooks short-circuit and write directly.
        ctx.HttpContext.Response.OnStarting(() =>
        {
            foreach (var kv in headers)
                ctx.HttpContext.Response.Headers[kv.Key] = kv.Value;
            return Task.CompletedTask;
        });
    }

    private static HookResult ToShortCircuit(RequestRuleResult r) =>
        HookResult.ShortCircuit(
            r.BlockStatus ?? 403,
            Encoding.UTF8.GetBytes(r.BlockBody ?? "{\"error\":\"blocked\"}"),
            r.BlockContentType ?? "application/json");
}

/// Mirror of the request-side hook for response bodies + response headers.
public sealed class RequestRuleResponseHook : IResponseTranslationHook
{
    private readonly IRequestRuleSource _source;
    private readonly ILogger<RequestRuleResponseHook> _log;
    public RequestRuleResponseHook(IRequestRuleSource source, ILogger<RequestRuleResponseHook> log)
    {
        _source = source;
        _log = log;
    }

    public string PluginId => "builtin.request-rules";

    public async Task<HookResult> BeforeAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct)
    {
        var rules = await _source.GetForRequestAsync(ctx.TenantId, ctx.RouteId, ct);
        if (rules.Count == 0) return HookResult.Continue();

        var bodyResult = RequestRuleExecutor.ApplyToBody(rules, RequestRuleScope.ResponseBody, body);
        if (bodyResult.HeadersToSet is { Count: > 0 } hdrs)
        {
            foreach (var kv in hdrs)
                ctx.HttpContext.Response.Headers[kv.Key] = kv.Value;
        }
        if (body.Length > 0)
        {
            var s = Encoding.UTF8.GetString(body);
            foreach (var r in RequestRuleExecutor.CollectLogMatches(rules, RequestRuleScope.ResponseBody, s))
            {
                _log.LogInformation(
                    "response rule {RuleId} ({Name}) matched on response-body for route {RouteId}",
                    r.Id, r.Name, ctx.RouteId);
            }
        }
        if (bodyResult.Blocked)
        {
            // A "block" on the response side rewrites the body instead of
            // short-circuiting (the upstream already ran). Surfacing a
            // 403 here would be misleading.
            return HookResult.Modify(Encoding.UTF8.GetBytes(bodyResult.BlockBody ?? "{}"));
        }
        return bodyResult.ModifiedBody is not null
            ? HookResult.Modify(bodyResult.ModifiedBody)
            : HookResult.Continue();
    }

    public Task<HookResult> AfterAsync(PipelineHookContext ctx, byte[] body, CancellationToken ct)
        => Task.FromResult(HookResult.Continue());
}
