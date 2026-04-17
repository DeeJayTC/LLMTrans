using System.Text;

namespace LlmTrans.Core.Streaming;

/// Accumulates text deltas and emits sentence-complete segments when ready.
/// `TryFlush` is pure/deterministic — time-based soft flush is the caller's responsibility
/// (so tests don't depend on wall clock).
public sealed class SentenceBuffer
{
    private static readonly char[] Terminators = { '.', '!', '?', ';', '\n' };

    private readonly StringBuilder _buffer = new();
    private readonly int _minChars;
    private readonly int _maxChars;

    public SentenceBuffer(int minChars = 80, int maxChars = 64 * 1024)
    {
        if (minChars < 1) throw new ArgumentOutOfRangeException(nameof(minChars));
        _minChars = minChars;
        _maxChars = maxChars;
    }

    public int Length => _buffer.Length;
    public bool HasContent => _buffer.Length > 0;

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _buffer.Append(text);
    }

    /// Flush the buffer up to the last sentence terminator, iff that portion is ≥ `minChars`.
    public bool TryFlushSentence(out string segment)
    {
        if (_buffer.Length < _minChars)
        {
            segment = string.Empty;
            return false;
        }

        var lastTerm = FindLastTerminatorIndex(_buffer);
        if (lastTerm < 0)
        {
            segment = string.Empty;
            return false;
        }

        var cut = lastTerm + 1;
        if (cut < _minChars)
        {
            segment = string.Empty;
            return false;
        }

        segment = _buffer.ToString(0, cut);
        _buffer.Remove(0, cut);
        return true;
    }

    /// Forced mid-sentence flush (buffer overflow, soft timeout). Caller decides when.
    public string FlushAll()
    {
        if (_buffer.Length == 0) return string.Empty;
        var s = _buffer.ToString();
        _buffer.Clear();
        return s;
    }

    public bool ShouldOverflowFlush() => _buffer.Length >= _maxChars;

    private static int FindLastTerminatorIndex(StringBuilder sb)
    {
        for (var i = sb.Length - 1; i >= 0; i--)
            if (Array.IndexOf(Terminators, sb[i]) >= 0)
                return i;
        return -1;
    }
}
