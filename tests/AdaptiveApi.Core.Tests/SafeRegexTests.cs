using System.Text.RegularExpressions;
using AdaptiveApi.Core.Pipeline;

namespace AdaptiveApi.Core.Tests;

public sealed class SafeRegexTests
{
    [Fact]
    public void Compiles_normal_pattern()
    {
        var rx = SafeRegex.Compile("hello", RegexOptions.IgnoreCase);
        Assert.Matches(rx, "Hello, world");
    }

    [Fact]
    public void Rejects_oversize_pattern()
    {
        var huge = new string('a', SafeRegex.MaxPatternLength + 1);
        Assert.Throws<ArgumentException>(() => SafeRegex.Compile(huge, RegexOptions.None));
    }

    [Fact]
    public void Rejects_deeply_nested_quantifier_shape()
    {
        // Textbook ReDoS: each layer of quantified groups multiplies
        // backtracking. We refuse 4+ layers up-front rather than relying on
        // the timeout to catch it after the fact.
        Assert.Throws<ArgumentException>(() =>
            SafeRegex.Compile("((((a+)+)+)+)+", RegexOptions.None));
    }

    [Fact]
    public void Times_out_pathological_match()
    {
        // (a+)+$ on a long non-matching string is the canonical ReDoS shape.
        // SafeRegex still compiles it (only 2 layers), but the per-match
        // timeout aborts the runaway evaluation rather than hanging the
        // thread.
        var rx = SafeRegex.Compile("(a+)+$", RegexOptions.None);
        var input = new string('a', 32) + "b";
        Assert.Throws<RegexMatchTimeoutException>(() => rx.IsMatch(input));
    }

    [Fact]
    public void Empty_pattern_rejected()
    {
        Assert.Throws<ArgumentException>(() => SafeRegex.Compile("", RegexOptions.None));
    }
}
