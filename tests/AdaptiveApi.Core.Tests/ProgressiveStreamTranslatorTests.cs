using System.Text;
using AdaptiveApi.Core.Abstractions;
using AdaptiveApi.Core.Model;
using AdaptiveApi.Core.Streaming;

namespace AdaptiveApi.Core.Tests;

public sealed class ProgressiveStreamTranslatorTests
{
    [Fact]
    public async Task Emits_incremental_deltas_as_buffer_grows()
    {
        var upstream = BuildSse(
            """{"choices":[{"index":0,"delta":{"role":"assistant"}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"Hello there, this is a medium-length sentence."}}]}""",
            """{"choices":[{"index":0,"delta":{"content":" Here comes more text that should flush again."}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]");

        using var source = new MemoryStream(upstream);
        using var sink = new MemoryStream();
        var translator = new PassthroughCumulative();
        var prog = new ProgressiveStreamTranslator(translator,
            new ProgressiveOptions { MinChars = 10, MaxChars = 200 });

        await prog.TranslateAsync(source, sink, LanguageCode.English, new LanguageCode("de"), default);

        var output = Encoding.UTF8.GetString(sink.ToArray());
        // Role passthrough survives.
        Assert.Contains("\"role\":\"assistant\"", output);
        // Content appears in translated output (our fake translator uppercases).
        Assert.Contains("HELLO THERE", output);
        Assert.Contains("HERE COMES MORE TEXT", output);
        // [DONE] sentinel preserved at end.
        Assert.EndsWith("data: [DONE]\n\n", output);
    }

    [Fact]
    public async Task Flushes_on_finish_reason_even_below_min_chars()
    {
        var upstream = BuildSse(
            """{"choices":[{"index":0,"delta":{"content":"tiny"}}]}""",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]");

        using var source = new MemoryStream(upstream);
        using var sink = new MemoryStream();
        await new ProgressiveStreamTranslator(new PassthroughCumulative(),
            new ProgressiveOptions { MinChars = 80, MaxChars = 200 })
            .TranslateAsync(source, sink, LanguageCode.English, new LanguageCode("de"), default);

        var output = Encoding.UTF8.GetString(sink.ToArray());
        Assert.Contains("TINY", output);
        Assert.EndsWith("data: [DONE]\n\n", output);
    }

    [Fact]
    public async Task Max_chars_forces_flush_without_terminator()
    {
        var big = new string('x', 300);
        var upstream = BuildSse(
            "{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + big + "\"}}]}",
            """{"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]");

        using var source = new MemoryStream(upstream);
        using var sink = new MemoryStream();
        await new ProgressiveStreamTranslator(new PassthroughCumulative(),
            new ProgressiveOptions { MinChars = 10, MaxChars = 100 })
            .TranslateAsync(source, sink, LanguageCode.English, new LanguageCode("de"), default);

        var output = Encoding.UTF8.GetString(sink.ToArray());
        Assert.Contains(new string('X', 300), output);
    }

    private static byte[] BuildSse(params string[] events)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
            sb.Append("data: ").Append(e).Append("\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// Uppercases content outside placeholder tags; preserves tags verbatim.
    private sealed class PassthroughCumulative : ITranslator
    {
        public string TranslatorId => "uc";
        public TranslatorCapabilities Capabilities => TranslatorCapabilities.TagHandling;
        public Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
            IReadOnlyList<TranslationRequest> r, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TranslationResult>>(
                r.Select(x => new TranslationResult(UpperOutsideTags(x.Text))).ToArray());

        private static string UpperOutsideTags(string s)
        {
            var sb = new StringBuilder();
            var i = 0;
            while (i < s.Length)
            {
                var tagStart = s.IndexOf("<adaptiveapi ", i, StringComparison.Ordinal);
                if (tagStart < 0) { sb.Append(s[i..].ToUpperInvariant()); break; }
                sb.Append(s[i..tagStart].ToUpperInvariant());
                var tagEnd = s.IndexOf("/>", tagStart, StringComparison.Ordinal) + 2;
                sb.Append(s[tagStart..tagEnd]);
                i = tagEnd;
            }
            return sb.ToString();
        }
    }
}
