using System.Text.RegularExpressions;

namespace AdaptiveApi.Core.RequestRules;

/// Compiled, immutable view of a tenant-supplied request rule. The DB row
/// (`RequestRuleEntity`) is the storage shape; this is the runtime shape the
/// executor evaluates per request.
public sealed record CompiledRequestRule(
    string Id,
    string TenantId,
    string? RouteId,
    string Name,
    RequestRuleScope Scope,
    string? HeaderName,
    Regex Match,
    RequestRuleAction Action,
    int? BlockStatus,
    string? ActionPayload,
    string? ActionHeader,
    int Priority);

/// Where a rule is matched against. Each scope corresponds to a hook point
/// the executor wires into:
///   * <see cref="RequestBody"/> / <see cref="ResponseBody"/> match the body bytes (UTF-8 string).
///   * <see cref="RequestHeader"/> / <see cref="ResponseHeader"/> match a single header value.
///   * <see cref="Path"/> matches the inbound HTTP path.
public enum RequestRuleScope
{
    RequestBody,
    ResponseBody,
    RequestHeader,
    ResponseHeader,
    Path,
}

/// What the rule does when it matches.
///   * <see cref="Block"/> — short-circuits with <c>BlockStatus</c> + <c>ActionPayload</c> body.
///   * <see cref="Replace"/> — substitutes matched groups in the body using <c>ActionPayload</c>.
///   * <see cref="SetHeader"/> — sets <c>ActionHeader</c> = <c>ActionPayload</c> on the outbound message.
///   * <see cref="Log"/> — writes a structured log entry; everything else passes through.
public enum RequestRuleAction
{
    Block,
    Replace,
    SetHeader,
    Log,
}

/// Outcome of applying a single rule to a body. The executor threads bodies
/// through rules in priority order; a <see cref="Blocked"/> result stops the
/// chain.
public sealed record RequestRuleResult(
    bool Blocked,
    int? BlockStatus,
    string? BlockBody,
    string? BlockContentType,
    byte[]? ModifiedBody,
    IReadOnlyDictionary<string, string>? HeadersToSet);
