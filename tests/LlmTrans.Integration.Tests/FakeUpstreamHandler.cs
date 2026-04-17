using System.Net;
using System.Net.Http.Headers;

namespace LlmTrans.Integration.Tests;

public sealed class FakeUpstreamHandler : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = new();
    public byte[]? ResponseBody { get; set; }
    public string ResponseContentType { get; set; } = "text/event-stream";
    public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
    public Dictionary<string, string> AdditionalResponseHeaders { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        byte[]? body = null;
        if (request.Content is not null)
            body = await request.Content.ReadAsByteArrayAsync(ct);

        Requests.Add(new CapturedRequest(
            Method: request.Method,
            RequestUri: request.RequestUri!,
            Headers: request.Headers.ToDictionary(h => h.Key, h => string.Join(',', h.Value)),
            ContentHeaders: request.Content?.Headers
                .ToDictionary(h => h.Key, h => string.Join(',', h.Value))
                ?? new Dictionary<string, string>(),
            BodyBytes: body));

        var response = new HttpResponseMessage(ResponseStatus);
        var responseBody = ResponseBody ?? Array.Empty<byte>();
        response.Content = new ByteArrayContent(responseBody);
        response.Content.Headers.Remove("Content-Type");
        response.Content.Headers.TryAddWithoutValidation("Content-Type", ResponseContentType);

        foreach (var (k, v) in AdditionalResponseHeaders)
            response.Headers.TryAddWithoutValidation(k, v);

        return response;
    }
}

public sealed record CapturedRequest(
    HttpMethod Method,
    Uri RequestUri,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> ContentHeaders,
    byte[]? BodyBytes)
{
    public string? BodyAsString() =>
        BodyBytes is null ? null : System.Text.Encoding.UTF8.GetString(BodyBytes);

    public string? AuthorizationScheme()
    {
        if (!Headers.TryGetValue("Authorization", out var v)) return null;
        var sp = v.IndexOf(' ');
        return sp < 0 ? v : v[..sp];
    }

    public string? AuthorizationParameter()
    {
        if (!Headers.TryGetValue("Authorization", out var v)) return null;
        var sp = v.IndexOf(' ');
        return sp < 0 ? null : v[(sp + 1)..];
    }

    public bool HasHeader(string name) =>
        Headers.ContainsKey(name) || ContentHeaders.ContainsKey(name);
}
