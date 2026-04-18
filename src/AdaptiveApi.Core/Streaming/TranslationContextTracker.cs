namespace AdaptiveApi.Core.Streaming;

/// Accumulates translated source chunks as running context for subsequent
/// translation calls (mainly DeepL's `context` parameter). The system context
/// (admin-defined, static) is always prepended; the remaining budget is filled
/// with accumulated conversation history, newest-first so the most relevant
/// context is preserved when the 4 000-character cap kicks in.
internal sealed class TranslationContextTracker
{
    internal const int MaxContextChars = 4000;

    private readonly string? _systemContext;
    private readonly List<string> _chunks = new();
    private int _totalChunkChars;

    public TranslationContextTracker(string? systemContext)
    {
        // Truncate system context itself if it exceeds the limit.
        if (!string.IsNullOrEmpty(systemContext) && systemContext.Length > MaxContextChars)
            systemContext = systemContext[..MaxContextChars];
        _systemContext = systemContext;
    }

    /// Record a source chunk that has been translated. This becomes part of the
    /// context for all future translations in this stream.
    public void Append(string sourceChunk)
    {
        if (string.IsNullOrEmpty(sourceChunk)) return;
        _chunks.Add(sourceChunk);
        _totalChunkChars += sourceChunk.Length;
    }

    /// Build the context string for the next translation call.
    /// Layout: [systemContext\n\n]chunk1 chunk2 … chunkN — trimmed to fit within
    /// `MaxContextChars`. When the history exceeds the budget, older chunks are
    /// dropped from the front so the most recent context is preserved.
    public string? Build()
    {
        if (_systemContext is null && _chunks.Count == 0) return null;

        var systemLen = _systemContext?.Length ?? 0;
        // Reserve 2 chars for the "\n\n" separator when both parts are present.
        var separatorLen = (systemLen > 0 && _chunks.Count > 0) ? 2 : 0;
        var budget = MaxContextChars - systemLen - separatorLen;

        if (budget <= 0 && systemLen > 0)
            return _systemContext![..MaxContextChars];

        // Walk chunks newest-first to build the history tail that fits.
        var historyParts = new List<string>();
        var used = 0;
        for (var i = _chunks.Count - 1; i >= 0; i--)
        {
            var chunk = _chunks[i];
            var spaceNeeded = chunk.Length + (historyParts.Count > 0 ? 1 : 0); // +1 for space separator
            if (used + spaceNeeded > budget) break;
            historyParts.Add(chunk);
            used += spaceNeeded;
        }

        historyParts.Reverse();
        var history = string.Join(' ', historyParts);

        if (systemLen > 0 && history.Length > 0)
            return _systemContext + "\n\n" + history;
        if (systemLen > 0)
            return _systemContext;
        return history.Length > 0 ? history : null;
    }
}
