using LlmTrans.Core.Streaming;

namespace LlmTrans.Core.Tests;

public sealed class SentenceBufferTests
{
    [Fact]
    public void Holds_content_below_min_chars()
    {
        var buf = new SentenceBuffer(minChars: 40);
        buf.Append("Short.");
        Assert.False(buf.TryFlushSentence(out _));
    }

    [Fact]
    public void Flushes_complete_sentence_once_min_chars_met()
    {
        var buf = new SentenceBuffer(minChars: 20);
        buf.Append("This is a complete sentence. And a start of another");
        Assert.True(buf.TryFlushSentence(out var seg));
        Assert.Equal("This is a complete sentence.", seg);
        Assert.Equal(" And a start of another", FlushPeek(buf));
    }

    [Fact]
    public void Multiple_sentences_flush_up_to_last_terminator()
    {
        var buf = new SentenceBuffer(minChars: 10);
        buf.Append("Hi there. How are you? I am fine!");
        Assert.True(buf.TryFlushSentence(out var seg));
        Assert.EndsWith("fine!", seg.TrimEnd());
    }

    [Fact]
    public void FlushAll_empties_buffer_even_mid_sentence()
    {
        var buf = new SentenceBuffer();
        buf.Append("partial without terminator");
        Assert.Equal("partial without terminator", buf.FlushAll());
        Assert.False(buf.HasContent);
    }

    [Fact]
    public void Newline_counts_as_terminator()
    {
        var buf = new SentenceBuffer(minChars: 5);
        buf.Append("line one\nline two continues");
        Assert.True(buf.TryFlushSentence(out var seg));
        Assert.Equal("line one\n", seg);
    }

    private static string FlushPeek(SentenceBuffer buf) => buf.FlushAll();
}
