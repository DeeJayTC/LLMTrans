using System.Text;
using System.Text.Json.Nodes;
using LlmTrans.Core.Abstractions;
using LlmTrans.Core.Model;
using LlmTrans.Core.Streaming;

namespace LlmTrans.Core.Tests;

public sealed class OpenAiStreamTranslatorTests
{
    [Fact]
    public async Task Translates_content_deltas_and_preserves_role_and_finish_events()
    {
        var upstream = BuildSse(
            """{"id":"x","choices":[{"index":0,"delta":{"role":"assistant"}}]}""",
            """{"id":"x","choices":[{"index":0,"delta":{"content":"Hello, this is a complete sentence"}}]}""",
            """{"id":"x","choices":[{"index":0,"delta":{"content":". And another."}}]}""",
            """{"id":"x","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
            "[DONE]");

        var translator = new UppercaseTranslator();
        using var source = new MemoryStream(upstream);
        using var sink = new MemoryStream();

        var m = await new OpenAiStreamTranslator(translator).TranslateAsync(
            source, sink, LanguageCode.English, new LanguageCode("de"), default,
            minChars: 10);

        var output = Encoding.UTF8.GetString(sink.ToArray());

        // The role-only event was forwarded verbatim.
        Assert.Contains("\"role\":\"assistant\"", output);

        // Translated content appeared in uppercase (our fake translator).
        Assert.Contains("HELLO, THIS IS A COMPLETE SENTENCE", output);

        // The finish event came through with content removed.
        Assert.Contains("\"finish_reason\":\"stop\"", output);

        // [DONE] sentinel preserved at end.
        Assert.EndsWith("data: [DONE]\n\n", output);

        Assert.Equal(0, m.IntegrityFailures);
    }

    [Fact]
    public async Task Buffers_small_deltas_until_sentence_boundary()
    {
        // Deltas that individually are far below minChars; only flush once sentence completes.
        var upstream = BuildSse(
            """{"choices":[{"index":0,"delta":{"content":"Hel"}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"lo"}}]}""",
            """{"choices":[{"index":0,"delta":{"content":", world"}}]}""",
            """{"choices":[{"index":0,"delta":{"content":"!"}}]}""",
            "[DONE]");

        using var source = new MemoryStream(upstream);
        using var sink = new MemoryStream();
        await new OpenAiStreamTranslator(new UppercaseTranslator()).TranslateAsync(
            source, sink, LanguageCode.English, new LanguageCode("de"), default,
            minChars: 5);

        var output = Encoding.UTF8.GetString(sink.ToArray());
        Assert.Contains("HELLO, WORLD!", output);
        Assert.EndsWith("data: [DONE]\n\n", output);
    }

    [Fact]
    public async Task Integrity_failure_falls_back_to_source_and_increments_metric()
    {
        var upstream = BuildSse(
            """{"choices":[{"index":0,"delta":{"content":"Explain `user.id` at https://example.com now, please."}}]}""",
            "[DONE]");

        using var source = new MemoryStream(upstream);
        using var sink = new MemoryStream();
        var m = await new OpenAiStreamTranslator(new TagStrippingTranslator()).TranslateAsync(
            source, sink, LanguageCode.English, new LanguageCode("de"), default,
            minChars: 5);

        Assert.True(m.IntegrityFailures > 0);
        var output = Encoding.UTF8.GetString(sink.ToArray());
        // On integrity failure, fallback emits the source unchanged.
        // (Content passes through JSON encoding so backtick literal stays as-is, URL slashes may escape.)
        Assert.Contains("user.id", output);
        Assert.Contains("example.com", output);
    }

    [Fact]
    public async Task Flushes_residual_on_stream_end_without_done()
    {
        var upstream = BuildSse(
            """{"choices":[{"index":0,"delta":{"content":"trailing content without terminator"}}]}""");

        using var source = new MemoryStream(upstream);
        using var sink = new MemoryStream();
        await new OpenAiStreamTranslator(new UppercaseTranslator()).TranslateAsync(
            source, sink, LanguageCode.English, new LanguageCode("de"), default,
            minChars: 80);

        var output = Encoding.UTF8.GetString(sink.ToArray());
        Assert.Contains("TRAILING CONTENT WITHOUT TERMINATOR", output);
    }

    private static byte[] BuildSse(params string[] events)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
            sb.Append("data: ").Append(e).Append("\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private sealed class UppercaseTranslator : ITranslator
    {
        public string TranslatorId => "uc";
        public TranslatorCapabilities Capabilities => TranslatorCapabilities.TagHandling;
        public Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(IReadOnlyList<TranslationRequest> r, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TranslationResult>>(
                r.Select(x => new TranslationResult(PreservingUppercase(x.Text))).ToArray());

        private static string PreservingUppercase(string s)
        {
            var sb = new StringBuilder();
            var i = 0;
            while (i < s.Length)
            {
                var tagStart = s.IndexOf("<llmtrans ", i, StringComparison.Ordinal);
                if (tagStart < 0) { sb.Append(s[i..].ToUpperInvariant()); break; }
                sb.Append(s[i..tagStart].ToUpperInvariant());
                var tagEnd = s.IndexOf("/>", tagStart, StringComparison.Ordinal) + 2;
                sb.Append(s[tagStart..tagEnd]);
                i = tagEnd;
            }
            return sb.ToString();
        }
    }

    private sealed class TagStrippingTranslator : ITranslator
    {
        public string TranslatorId => "stripper";
        public TranslatorCapabilities Capabilities => TranslatorCapabilities.TagHandling;
        public Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(IReadOnlyList<TranslationRequest> r, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TranslationResult>>(
                r.Select(x => new TranslationResult(
                    System.Text.RegularExpressions.Regex.Replace(x.Text, "<llmtrans[^/]*/>", "")))
                 .ToArray());
    }
}
