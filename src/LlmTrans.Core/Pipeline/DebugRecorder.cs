using LlmTrans.Core.Abstractions;
using LlmTrans.Core.Model;

namespace LlmTrans.Core.Pipeline;

/// Opt-in per-request collector that captures the raw texts handed to and returned
/// from the translator, alongside the HTTP bodies flowing in and out of the proxy.
/// Attach via `PipelineOptions.Debug`. The adapter layer owns lifetime (scoped to
/// one request) and decides how to surface the captured data to the client.
///
/// Nothing in the llmtrans core persists a DebugRecorder. It lives only on the hot
/// path of a single in-flight request.
public sealed class DebugRecorder
{
    /// Up to this many chars per individual captured string. Clamps memory when a
    /// caller sends a huge prompt but debug is on.
    public int MaxPerFieldChars { get; init; } = 8 * 1024;

    public List<TranslationTrace> Translations { get; } = new();

    public string? RequestBodyPreTranslation { get; set; }
    public string? RequestBodyPostTranslation { get; set; }
    public string? UpstreamResponseBody { get; set; }
    public string? FinalResponseBody { get; set; }

    public void RecordTranslation(
        string direction,
        LanguageCode source,
        LanguageCode target,
        IReadOnlyList<TranslationRequest> requests,
        IReadOnlyList<TranslationResult> results)
    {
        var pairs = new List<TranslationTracePair>(Math.Min(requests.Count, results.Count));
        for (var i = 0; i < requests.Count && i < results.Count; i++)
        {
            pairs.Add(new TranslationTracePair(
                Source: Truncate(requests[i].Text),
                Target: Truncate(results[i].Text)));
        }
        Translations.Add(new TranslationTrace(
            Direction: direction,
            SourceLanguage: source.Value,
            TargetLanguage: target.Value,
            Pairs: pairs));
    }

    internal string? Truncate(string? value) =>
        value is null ? null
        : value.Length <= MaxPerFieldChars ? value
        : value[..MaxPerFieldChars] + $"\n…[truncated at {MaxPerFieldChars} chars]";

    public void SetRequestPre(string body) => RequestBodyPreTranslation = Truncate(body);
    public void SetRequestPost(string body) => RequestBodyPostTranslation = Truncate(body);
    public void SetUpstreamResponse(string body) => UpstreamResponseBody = Truncate(body);
    public void SetFinalResponse(string body) => FinalResponseBody = Truncate(body);
}

public sealed record TranslationTrace(
    string Direction,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<TranslationTracePair> Pairs);

public sealed record TranslationTracePair(string? Source, string? Target);
