using PasteJump.Core.PasteMode;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Line and character counts for the overlay's facts line. Arithmetic with more edge cases than it looks, and a
/// line count that is quietly off by one is the sort of thing nobody notices until the number is absurd.
/// </summary>
public class TextMetricsTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("one line", 1)]
    [InlineData("a\nb", 2)]
    [InlineData("a\r\nb", 2)]
    [InlineData("a\rb", 2)]
    [InlineData("a\nb\nc", 3)]
    public void Lines_are_counted_the_way_a_person_counts_them(string? text, int expected)
        => Assert.Equal(expected, TextMetrics.CountLines(text));

    /// <summary>
    /// A trailing break does not open a line of its own. Every editor agrees, and splitting on the separator -
    /// the obvious implementation - gets this wrong by one for the very common case of text copied from a file.
    /// </summary>
    [Theory]
    [InlineData("a\n", 1)]
    [InlineData("a\r\n", 1)]
    [InlineData("a\nb\n", 2)]
    public void A_trailing_break_does_not_add_a_line(string text, int expected)
        => Assert.Equal(expected, TextMetrics.CountLines(text));

    /// <summary>
    /// Two trailing breaks genuinely are a blank last line, so only the final one is discounted. This is the case
    /// that proves the rule above is "discount one", not "ignore trailing breaks".
    /// </summary>
    [Fact]
    public void A_blank_last_line_still_counts()
        => Assert.Equal(2, TextMetrics.CountLines("a\n\n"));

    /// <summary>Mixed separators arrive constantly - the clipboard takes text from anywhere.</summary>
    [Fact]
    public void Mixed_separators_are_each_counted_once()
        => Assert.Equal(4, TextMetrics.CountLines("a\r\nb\nc\rd"));

    [Fact]
    public void Nothing_reads_as_empty()
        => Assert.Equal("empty", TextMetrics.Describe(string.Empty));

    [Fact]
    public void One_line_is_singular()
        => Assert.Equal("1 line · 5 chars", TextMetrics.Describe("hello"));

    [Fact]
    public void Several_lines_are_plural_and_thousands_are_grouped()
    {
        var text = string.Join('\n', Enumerable.Repeat(new string('x', 999), 4));

        // 4 lines of 999 characters plus 3 separators.
        Assert.Equal("4 lines · 3,999 chars", TextMetrics.Describe(text));
    }

    /// <summary>
    /// A clip longer than the stored preview cap says so with a <c>+</c> on both numbers, rather than stating a
    /// count that is simply wrong. Admitting the limit is the honest option and it is also the useful one - the
    /// reader learns the clip is bigger than what is shown.
    /// </summary>
    [Fact]
    public void A_truncated_preview_marks_both_numbers()
    {
        var facts = TextMetrics.Describe("a\nb", truncated: true);

        Assert.Equal("2+ lines · 3+ chars", facts);
    }
}
