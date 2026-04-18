using AdaptiveApi.Core.Abstractions;

namespace AdaptiveApi.Infrastructure.Routing;

public sealed class TenantContext : ITenantContext
{
    public string? TenantId { get; private set; }
    public string? RouteId { get; private set; }

    public void Bind(string tenantId, string routeId)
    {
        if (TenantId is not null)
            throw new InvalidOperationException("tenant context already bound for this scope");
        TenantId = tenantId;
        RouteId = routeId;
    }
}
