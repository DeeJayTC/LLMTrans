using System.Text.Json.Nodes;
using LlmTrans.Core.Abstractions;
using LlmTrans.Core.Model;
using LlmTrans.Core.Pipeline;

namespace LlmTrans.Core.Tests;

public sealed class TranslationPipelineTests
{
    [Fact]
    public async Task Translates_allowlisted_string_leaves_and_preserves_structure()
    {
        var root = JsonNode.Parse(
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hello world"}]}""")!;

        var translator = new UppercaseTranslator();
        var pipeline = new TranslationPipeline(translator);
        var stats = await pipeline.TranslateInPlaceAsync(root, AllowlistCatalog.OpenAiChatRequest,
            new PipelineOptions { Source = LanguageCode.English, Target = new LanguageCode("de") }, default);

        Assert.Equal("HELLO WORLD", root["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("user", root["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("gpt-4o-mini", root["model"]!.GetValue<string>());
        Assert.Equal(1, stats.SitesPlanned);
        Assert.Equal(1, stats.SitesTranslated);
        Assert.Equal(0, stats.IntegrityFailures);
    }

    [Fact]
    public async Task Protects_code_and_urls_via_placeholders()
    {
        var root = JsonNode.Parse(
            """{"messages":[{"role":"user","content":"Explain `user.id` at https://example.com now."}]}""")!;

        var translator = new UppercaseTranslator();
        var pipeline = new TranslationPipeline(translator);
        await pipeline.TranslateInPlaceAsync(root, AllowlistCatalog.OpenAiChatRequest,
            new PipelineOptions { Source = LanguageCode.English, Target = new LanguageCode("de") }, default);

        var translated = root["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("`user.id`", translated);
        Assert.Contains("https://example.com", translated);
    }

    [Fact]
    public async Task Same_language_route_is_noop()
    {
        var root = JsonNode.Parse("""{"messages":[{"role":"user","content":"hello"}]}""")!;
        var pipeline = new TranslationPipeline(new UppercaseTranslator());
        var stats = await pipeline.TranslateInPlaceAsync(root, AllowlistCatalog.OpenAiChatRequest,
            new PipelineOptions { Source = LanguageCode.English, Target = LanguageCode.English }, default);

        Assert.Equal(0, stats.SitesPlanned);
        Assert.Equal("hello", root["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Integrity_failure_keeps_source_intact()
    {
        var root = JsonNode.Parse(
            """{"messages":[{"role":"user","content":"Look at `x.y`."}]}""")!;
        var pipeline = new TranslationPipeline(new StripTagsTranslator());
        var stats = await pipeline.TranslateInPlaceAsync(root, AllowlistCatalog.OpenAiChatRequest,
            new PipelineOptions { Source = LanguageCode.English, Target = new LanguageCode("de") }, default);

        Assert.Equal(1, stats.IntegrityFailures);
        Assert.Equal("Look at `x.y`.", root["messages"]![0]!["content"]!.GetValue<string>());
    }

    private sealed class UppercaseTranslator : ITranslator
    {
        public string TranslatorId => "uppercase";
        public TranslatorCapabilities Capabilities => TranslatorCapabilities.TagHandling;
        public Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
            IReadOnlyList<TranslationRequest> requests, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TranslationResult>>(
                requests.Select(r => new TranslationResult(UppercaseOutsideTags(r.Text))).ToArray());

        private static string UppercaseOutsideTags(string s)
        {
            var sb = new System.Text.StringBuilder();
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

    private sealed class StripTagsTranslator : ITranslator
    {
        public string TranslatorId => "stripping";
        public TranslatorCapabilities Capabilities => TranslatorCapabilities.TagHandling;
        public Task<IReadOnlyList<TranslationResult>> TranslateBatchAsync(
            IReadOnlyList<TranslationRequest> requests, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TranslationResult>>(
                requests.Select(r => new TranslationResult(
                    System.Text.RegularExpressions.Regex.Replace(r.Text, "<llmtrans[^/]*/>", "")))
                .ToArray());
    }
}
