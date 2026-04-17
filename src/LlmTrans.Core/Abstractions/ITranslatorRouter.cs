using LlmTrans.Core.Routing;

namespace LlmTrans.Core.Abstractions;

public interface ITranslatorRouter
{
    ITranslator Resolve(RouteConfig route);
}
