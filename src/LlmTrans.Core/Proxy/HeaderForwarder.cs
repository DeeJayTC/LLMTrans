using Microsoft.AspNetCore.Http;

namespace LlmTrans.Core.Proxy;

/// Sole chokepoint for inbound → upstream and upstream → client header propagation.
/// Hop-by-hop headers are dropped; llmtrans extension headers are stripped before
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

    private static readonly HashSet<string> LlmTransExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-LlmTrans-Target-Lang",
        "X-LlmTrans-Source-Lang",
        "X-LlmTrans-Glossary",
        "X-LlmTrans-Style-Rule",
        "X-LlmTrans-Model-Type",
        "X-LlmTrans-Translator",
        "X-LlmTrans-Mode",
        "X-LlmTrans-Stream-Strategy",
        "X-LlmTrans-Debug",
    };

    public static bool IsHopByHop(string header) => HopByHop.Contains(header);

    public static bool IsLlmTransExtension(string header) => LlmTransExtension.Contains(header);

    public static void CopyInboundToUpstream(IHeaderDictionary inbound, HttpRequestMessage upstream, HttpContent? contentForContentHeaders = null)
    {
        foreach (var (name, values) in inbound)
        {
            if (IsHopByHop(name) || IsLlmTransExtension(name))
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
