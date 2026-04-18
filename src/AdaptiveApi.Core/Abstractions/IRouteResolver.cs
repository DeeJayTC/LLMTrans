using AdaptiveApi.Core.Routing;

namespace AdaptiveApi.Core.Abstractions;

public interface IRouteResolver
{
    Task<RouteConfig?> ResolveByTokenAsync(string token, CancellationToken ct);
}
