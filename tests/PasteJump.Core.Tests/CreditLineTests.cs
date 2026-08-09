using PasteJump.Core;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Splitting the copyright line around the author's name, which is what lets the About window render that part
/// of it as a link to their profile.
/// </summary>
public sealed class CreditLineTests
{
    [Fact]
    public void The_name_is_isolated_with_the_text_either_side_of_it()
    {
        var credit = CreditLineSplitter.Split("Copyright (c) 2026 Lokesh Govindu", "Lokesh Govindu");

        Assert.True(credit.HasAuthor);
        Assert.Equal("Copyright (c) 2026 ", credit.Prefix);
        Assert.Equal("Lokesh Govindu", credit.Author);
        Assert.Equal(string.Empty, credit.Suffix);
    }

    /// <summary>
    /// The suffix is where an off-by-one would hide: it starts after the name, so getting it wrong either
    /// repeats the last letter or eats the first character of what follows.
    /// </summary>
    [Fact]
    public void Text_after_the_name_is_kept_exactly()
    {
        var credit = CreditLineSplitter.Split("(c) 2026 Lokesh Govindu. All rights reserved.", "Lokesh Govindu");

        Assert.Equal("(c) 2026 ", credit.Prefix);
        Assert.Equal("Lokesh Govindu", credit.Author);
        Assert.Equal(". All rights reserved.", credit.Suffix);
    }

    /// <summary>
    /// Reassembling the three parts must give the original back, whatever the input. That is the property the
    /// About window depends on - anything else shows as a duplicated or a missing word.
    /// </summary>
    [Theory]
    [InlineData("Copyright (c) 2026 Lokesh Govindu", "Lokesh Govindu")]
    [InlineData("Lokesh Govindu 2026", "Lokesh Govindu")]
    [InlineData("Copyright (c) 2026 Someone Else", "Lokesh Govindu")]
    [InlineData("", "Lokesh Govindu")]
    [InlineData("Copyright (c) 2026 Lokesh Govindu", "")]
    public void The_parts_always_reassemble_into_the_original(string copyright, string author)
    {
        var credit = CreditLineSplitter.Split(copyright, author);

        Assert.Equal(copyright, credit.Prefix + credit.Author + credit.Suffix);
    }

    /// <summary>
    /// A name that is not in the line leaves the whole line as plain text rather than linking something
    /// arbitrary. Same for no name at all, which is what a host with no author metadata sees.
    /// </summary>
    [Theory]
    [InlineData("Copyright (c) 2026 Someone Else", "Lokesh Govindu")]
    [InlineData("Copyright (c) 2026 Lokesh Govindu", "")]
    [InlineData("Copyright (c) 2026 Lokesh Govindu", null)]
    public void An_unmatched_name_leaves_the_line_unlinked(string copyright, string? author)
    {
        var credit = CreditLineSplitter.Split(copyright, author);

        Assert.False(credit.HasAuthor);
        Assert.Equal(copyright, credit.Prefix);
    }

    /// <summary>
    /// Case matters. The comparison is ordinal because both strings are built from the same literal in the same
    /// build, so a near-miss means the assumption has broken and a silent fuzzy match would hide that.
    /// </summary>
    [Fact]
    public void The_match_is_case_sensitive()
        => Assert.False(CreditLineSplitter.Split("Copyright (c) 2026 lokesh govindu", "Lokesh Govindu").HasAuthor);

    [Fact]
    public void A_null_copyright_is_an_empty_line_rather_than_a_throw()
    {
        var credit = CreditLineSplitter.Split(null, "Lokesh Govindu");

        Assert.False(credit.HasAuthor);
        Assert.Equal(string.Empty, credit.Prefix);
    }

    /// <summary>
    /// And the real values line up: the copyright in Directory.Build.props contains the author it names. If
    /// this ever fails, the About window has quietly stopped linking the name.
    /// </summary>
    [Fact]
    public void The_assemblys_own_copyright_contains_its_own_author()
    {
        Assert.NotEqual(string.Empty, AppVersion.Author);
        Assert.True(CreditLineSplitter.Split(AppVersion.Copyright, AppVersion.Author).HasAuthor);
    }

    [Fact]
    public void The_author_url_is_an_absolute_https_uri()
    {
        Assert.True(Uri.TryCreate(AppVersion.AuthorUrl, UriKind.Absolute, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
    }
}
