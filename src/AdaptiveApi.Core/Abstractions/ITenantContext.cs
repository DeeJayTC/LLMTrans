namespace AdaptiveApi.Core.Abstractions;

public interface ITenantContext
{
    string? TenantId { get; }
    string? RouteId { get; }
    void Bind(string tenantId, string routeId);
}
