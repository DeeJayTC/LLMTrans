using LlmTrans.Core.Routing;
using Microsoft.AspNetCore.Http;

namespace LlmTrans.Core.Abstractions;

public interface IProviderAdapter
{
    string ProviderId { get; }
    RouteKind RouteKind { get; }

    bool Matches(HttpRequest request);

    Task HandleAsync(HttpContext context, RouteConfig route, CancellationToken ct);
}
