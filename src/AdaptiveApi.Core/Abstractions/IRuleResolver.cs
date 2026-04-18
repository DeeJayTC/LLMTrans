using AdaptiveApi.Core.Routing;
using AdaptiveApi.Core.Rules;

namespace AdaptiveApi.Core.Abstractions;

public interface IRuleResolver
{
    Task<ResolvedRules> ResolveAsync(RouteConfig route, CancellationToken ct);
}
