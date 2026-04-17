using LlmTrans.Core.Pipeline;

namespace LlmTrans.Core.Tests;

public sealed class JsonPathPatternTests
{
    [Theory]
    [InlineData("/messages/*/content", new[] { "messages", "0", "content" }, true)]
    [InlineData("/messages/*/content", new[] { "messages", "0", "role" }, false)]
    [InlineData("/messages/*/content/*/text", new[] { "messages", "0", "content", "0", "text" }, true)]
    [InlineData("/messages/*/content/*/text", new[] { "messages", "0", "content", "0", "type" }, false)]
    [InlineData("/params/arguments/**", new[] { "params", "arguments", "nested", "a", "b" }, true)]
    [InlineData("/params/arguments/**", new[] { "params", "arguments" }, true)]
    [InlineData("/params/arguments/**", new[] { "params", "other" }, false)]
    public void Matches_as_expected(string pattern, string[] path, bool expected)
    {
        var p = new JsonPathPattern(pattern);
        Assert.Equal(expected, p.Matches(path));
    }
}
