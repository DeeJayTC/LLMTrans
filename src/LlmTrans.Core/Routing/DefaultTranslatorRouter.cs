using LlmTrans.Core.Abstractions;

namespace LlmTrans.Core.Routing;

public sealed class DefaultTranslatorRouter : ITranslatorRouter
{
    private readonly IReadOnlyDictionary<string, ITranslator> _byId;
    private readonly ITranslator _default;

    public DefaultTranslatorRouter(IEnumerable<ITranslator> translators, string defaultId)
    {
        _byId = translators.ToDictionary(t => t.TranslatorId, StringComparer.OrdinalIgnoreCase);
        if (!_byId.TryGetValue(defaultId, out var def))
            throw new InvalidOperationException($"default translator '{defaultId}' not registered");
        _default = def;
    }

    public ITranslator Resolve(RouteConfig route)
    {
        if (!string.IsNullOrEmpty(route.TranslatorId)
            && _byId.TryGetValue(route.TranslatorId, out var explicitly))
            return explicitly;
        return _default;
    }
}
