using Microsoft.AspNetCore.Http;

namespace AdaptiveApi.Core.Proxy;

/// Sole chokepoint for inbound → upstream and upstream → client header propagation.
/// Hop-by-hop headers are dropped; adaptiveapi extension headers are stripped before
/// reaching upstream; every other header flows byte-identical.
public static class HeaderForwarder
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Proxy-Connection",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
        "Host",
        "Expect",
        "Content-Length",
    };

    private static readonly HashSet<string> AdaptiveApiExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-AdaptiveApi-Target-Lang",
        "X-AdaptiveApi-Source-Lang",
        "X-AdaptiveApi-Glossary",
        "X-AdaptiveApi-Style-Rule",
        "X-AdaptiveApi-Model-Type",
        "X-AdaptiveApi-Translator",
        "X-AdaptiveApi-Mode",
        "X-AdaptiveApi-Stream-Strategy",
        "X-AdaptiveApi-Debug",
    };

    public static bool IsHopByHop(string header) => HopByHop.Contains(header);

    public static bool IsAdaptiveApiExtension(string header) => AdaptiveApiExtension.Contains(header);

    public static void CopyInboundToUpstream(IHeaderDictionary inbound, HttpRequestMessage upstream, HttpContent? contentForContentHeaders = null)
    {
        foreach (var (name, values) in inbound)
        {
            if (IsHopByHop(name) || IsAdaptiveApiExtension(name))
                continue;

            if (!upstream.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values))
            {
                contentForContentHeaders?.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
    }

    public static void CopyUpstreamToClient(HttpResponseMessage upstream, IHeaderDictionary clientHeaders)
    {
        foreach (var (name, values) in upstream.Headers)
        {
            if (IsHopByHop(name)) continue;
            clientHeaders[name] = values.ToArray();
        }
        foreach (var (name, values) in upstream.Content.Headers)
        {
            if (IsHopByHop(name)) continue;
            clientHeaders[name] = values.ToArray();
        }
    }
}
