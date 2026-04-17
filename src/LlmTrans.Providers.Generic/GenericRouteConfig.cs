using System.Text.Json.Serialization;

namespace LlmTrans.Providers.Generic;

/// Declarative configuration for one generic HTTP+JSON upstream.
/// Stored as JSON in `generic_routes.config_json`; parsed into this record at resolve time.
public sealed class GenericRouteConfig
{
    [JsonPropertyName("upstream")] public GenericUpstream Upstream { get; set; } = new();
    [JsonPropertyName("request")] public GenericSide Request { get; set; } = new();
    [JsonPropertyName("response")] public GenericResponse Response { get; set; } = new();
    /// `bidirectional` (default) | `request-only` | `response-only` | `off`.
    [JsonPropertyName("direction")] public string Direction { get; set; } = "bidirectional";
}

public sealed class GenericUpstream
{
    /// Base URL to forward the request to. Any tail captured from /generic/<token>/<tail>
    /// is appended; query string is preserved.
    [JsonPropertyName("urlTemplate")] public string UrlTemplate { get; set; } = "";
    [JsonPropertyName("method")] public string? Method { get; set; }
    [JsonPropertyName("additionalHeaders")]
    public Dictionary<string, string>? AdditionalHeaders { get; set; }
}

public class GenericSide
{
    /// JSONPath-like strings (e.g. "$.messages[*].content") naming the translatable leaves.
    [JsonPropertyName("translateJsonPaths")] public List<string> TranslateJsonPaths { get; set; } = new();
}

public sealed class GenericResponse : GenericSide
{
    /// `none` | `sse` | `ndjson` | `chunked-json`. Only `none` and `sse` are implemented in M7.
    [JsonPropertyName("streaming")] public string Streaming { get; set; } = "none";
    /// When `streaming=sse`, JSONPath-like string naming the translatable field INSIDE each event.
    [JsonPropertyName("eventPath")] public string? EventPath { get; set; }
    /// Paths to translate in the non-streaming response or the terminal SSE event.
    [JsonPropertyName("finalPaths")] public List<string> FinalPaths { get; set; } = new();
}
