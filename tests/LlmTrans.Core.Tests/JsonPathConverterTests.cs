using LlmTrans.Providers.Generic;

namespace LlmTrans.Core.Tests;

public sealed class JsonPathConverterTests
{
    [Theory]
    [InlineData("$.message", "/message")]
    [InlineData("$.chat_history[*].message", "/chat_history/*/message")]
    [InlineData("$.tools[*].description", "/tools/*/description")]
    [InlineData("$.tool_calls[*].parameters.*", "/tool_calls/*/parameters/*")]
    [InlineData("$.generations[0].text", "/generations/*/text")]
    [InlineData("$.data..text", "/data/**/text")]
    [InlineData("$", "")]
    [InlineData("message", "/message")]
    public void Converts_jsonpath_to_slash_pattern(string input, string expected)
    {
        Assert.Equal(expected, JsonPathConverter.ToSlashPattern(input));
    }

    [Fact]
    public void Builds_allowlist_that_matches_expected_paths()
    {
        var a = JsonPathConverter.ToAllowlist(new[] { "$.messages[*].content", "$.tools[*].description" });
        Assert.True(a.IsAllowed(new[] { "messages", "0", "content" }));
        Assert.True(a.IsAllowed(new[] { "tools", "3", "description" }));
        Assert.False(a.IsAllowed(new[] { "messages", "0", "role" }));
    }
}
