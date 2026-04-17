using LlmTrans.Core.Routing;
using LlmTrans.Core.Rules;

namespace LlmTrans.Core.Abstractions;

public interface IRuleResolver
{
    Task<ResolvedRules> ResolveAsync(RouteConfig route, CancellationToken ct);
}
