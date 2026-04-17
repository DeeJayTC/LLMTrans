using LlmTrans.Core.Routing;

namespace LlmTrans.Core.Abstractions;

public interface IRouteResolver
{
    Task<RouteConfig?> ResolveByTokenAsync(string token, CancellationToken ct);
}
