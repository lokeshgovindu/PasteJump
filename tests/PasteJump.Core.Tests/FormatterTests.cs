using PasteJump.Core.Formatting;
using Xunit;

namespace PasteJump.Core.Tests;

public class FormatterTests
{
    [Fact]
    public void Original_LeavesTextAloneAndKeepsAllFormats()
    {
        var formatter = new OriginalFormatter();

        Assert.Equal("  Hello   World  ", formatter.Apply("  Hello   World  "));
        Assert.False(formatter.TextOnlyOutput);
    }

    [Fact]
    public void PlainText_KeepsTextButNarrowsOutputToTextOnly()
    {
        var formatter = new PlainTextFormatter();

        Assert.Equal("Hello", formatter.Apply("Hello"));

        // The narrowing is the whole feature: leaving HTML/RTF in place would make rich targets
        // paste the original formatted content regardless.
        Assert.True(formatter.TextOnlyOutput);
    }

    [Theory]
    [InlineData("a   b", "a b")]
    [InlineData("  leading and trailing  ", "leading and trailing")]
    [InlineData("line one\nline two", "line one line two")]
    [InlineData("tabs\t\tcollapse", "tabs collapse")]
    [InlineData("", "")]
    public void CollapseWhitespace_NormalisesRuns(string input, string expected)
        => Assert.Equal(expected, new CollapseWhitespaceFormatter().Apply(input));

    [Theory]
    [InlineData("hello world. this is a test.", "Hello world. This is a test.")]
    [InlineData("SHOUTING TEXT", "Shouting text")]
    [InlineData("what? yes! ok.", "What? Yes! Ok.")]
    [InlineData("", "")]
    public void SentenceCase_CapitalisesSentenceStarts(string input, string expected)
        => Assert.Equal(expected, new SentenceCaseFormatter().Apply(input));

    [Fact]
    public void SentenceCase_TreatsNewlinesAsSentenceBoundaries()
    {
        var result = new SentenceCaseFormatter().Apply("first line\nsecond line");

        Assert.Equal("First line\nSecond line", result);
    }

    [Fact]
    public void Unindent_StripsCommonLeadingWhitespace()
    {
        var input = "        if (x)\n            return;\n        done();";
        var expected = "if (x)\n    return;\ndone();";

        Assert.Equal(expected, new UnindentFormatter().Apply(input));
    }

    [Fact]
    public void Unindent_IgnoresBlankLinesWhenMeasuringIndent()
    {
        var input = "    one\n\n    two";
        var expected = "one\n\ntwo";

        Assert.Equal(expected, new UnindentFormatter().Apply(input));
    }

    [Fact]
    public void Unindent_LeavesAlreadyFlushTextUnchanged()
    {
        var input = "no indent\n  some indent";

        Assert.Equal(input, new UnindentFormatter().Apply(input));
    }

    [Fact]
    public void Unindent_NormalisesCrLfSoNoStrayCarriageReturnsSurvive()
    {
        var result = new UnindentFormatter().Apply("    a\r\n    b");

        Assert.Equal("a\nb", result);
        Assert.DoesNotContain('\r', result);
    }

    [Fact]
    public void Registry_AlwaysStartsWithOriginal()
    {
        var registry = new FormatterRegistry();

        Assert.Equal("original", registry.Default.Id);
        Assert.Equal("original", registry.All[0].Id);
    }

    [Fact]
    public void Registry_CyclesAndWraps()
    {
        var registry = new FormatterRegistry();
        var current = registry.Default;

        for (var i = 0; i < registry.All.Count; i++)
        {
            current = registry.Next(current);
        }

        Assert.Equal(registry.Default.Id, current.Id);
    }

    [Fact]
    public void Registry_UnknownIdFallsBackToDefaultRatherThanThrowing()
    {
        var registry = new FormatterRegistry();

        // A settings file naming a formatter that no longer exists must not break startup.
        Assert.Equal(registry.Default.Id, registry.Resolve("was-a-plugin-once").Id);
        Assert.Equal(registry.Default.Id, registry.Resolve(null).Id);
    }

    [Fact]
    public void Registry_ResolveIsCaseInsensitive()
    {
        var registry = new FormatterRegistry();

        Assert.Equal("plain", registry.Resolve("PLAIN").Id);
    }
}
