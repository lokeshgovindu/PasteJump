using PasteJump.Core.Paste;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Joining several clips into one paste. Small surface, but the separator parsing has the sort of edge cases
/// that produce a silently wrong paste rather than an error.
/// </summary>
public class ClipJoinerTests
{
    [Fact]
    public void Clips_are_joined_in_the_order_given()
    {
        var result = ClipJoiner.Join(["one", "two", "three"], "\n");

        Assert.Equal("one\ntwo\nthree", result.Text);
        Assert.Equal(3, result.Joined);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void One_clip_joins_to_itself_with_no_separator_added()
    {
        Assert.Equal("only", ClipJoiner.Join(["only"], ", ").Text);
    }

    /// <summary>
    /// An image contributes nothing, and the count is what lets the caller say so. Five rows producing two
    /// lines with no explanation reads as data lost.
    /// </summary>
    [Fact]
    public void A_clip_with_no_text_is_skipped_and_counted()
    {
        var result = ClipJoiner.Join(["one", null, "two", null], "\n");

        Assert.Equal("one\ntwo", result.Text);
        Assert.Equal(2, result.Joined);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public void Nothing_joinable_produces_empty_text_rather_than_a_separator()
    {
        var result = ClipJoiner.Join([null, null], "\n");

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(0, result.Joined);
        Assert.Equal(2, result.Skipped);
    }

    /// <summary>
    /// Empty is text: it is something the user copied, and dropping it would turn a deliberate blank line into
    /// nothing. Only a null - no text at all - is skipped.
    /// </summary>
    [Fact]
    public void An_empty_clip_is_joined_rather_than_skipped()
    {
        var result = ClipJoiner.Join(["one", string.Empty, "two"], "|");

        Assert.Equal("one||two", result.Text);
        Assert.Equal(3, result.Joined);
    }

    [Theory]
    [InlineData(@"\n", "\n")]
    [InlineData(@"\r\n", "\r\n")]
    [InlineData(@"\t", "\t")]
    [InlineData(", ", ", ")]
    [InlineData(" - ", " - ")]
    [InlineData(@"\\", "\\")]
    public void Escapes_in_the_setting_become_the_characters_they_name(string setting, string expected)
    {
        Assert.Equal(expected, ClipJoiner.ParseSeparator(setting));
    }

    /// <summary>
    /// A backslash beginning no escape is kept, not eaten. The alternative silently deletes a character the
    /// user typed, and a separator is arbitrary text.
    /// </summary>
    [Theory]
    [InlineData(@"\d", @"\d")]
    [InlineData(@"a\", @"a\")]
    public void A_backslash_that_names_no_escape_survives(string setting, string expected)
    {
        Assert.Equal(expected, ClipJoiner.ParseSeparator(setting));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_unset_separator_falls_back_to_a_new_line(string? setting)
    {
        Assert.Equal("\n", ClipJoiner.ParseSeparator(setting));
    }

    [Fact]
    public void The_default_setting_parses_to_a_new_line()
    {
        Assert.Equal("\n", ClipJoiner.ParseSeparator(ClipJoiner.DefaultSeparator));
    }

    [Theory]
    [InlineData("\n", "a new line")]
    [InlineData(" ", "a space")]
    [InlineData("\t", "a tab")]
    [InlineData(", ", "a comma and a space")]
    public void Common_separators_are_named_rather_than_quoted(string separator, string expected)
    {
        Assert.Equal(expected, ClipJoiner.Describe(separator));
    }

    /// <summary>
    /// An unusual separator is quoted with its control characters escaped, so a status line saying how it
    /// joined cannot itself contain a line break.
    /// </summary>
    [Fact]
    public void An_unusual_separator_is_quoted_and_escaped()
    {
        Assert.Equal("\" ---\\n\"", ClipJoiner.Describe(" ---\n"));
    }
}
