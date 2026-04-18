using AdaptiveApi.Core.Routing;

namespace AdaptiveApi.Core.Abstractions;

public interface ITranslatorRouter
{
    ITranslator Resolve(RouteConfig route);
}
