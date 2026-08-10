using PasteJump.Core;
using PasteJump.Core.Updates;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Reading GitHub's answer and deciding whether it is newer.
/// <para>
/// Tested without a network because none of the mistakes here need one: a tag with a <c>v</c> on it, a release
/// marked draft, a version with three components compared against one with four, and above all the comparison
/// itself - <c>2026.1.0.10</c> sorts after <c>2026.1.0.9</c> only if it is not compared as a string.
/// </para>
/// </summary>
public sealed class UpdateCheckTests
{
    // ----------------------------------------------------------- the API URL

    [Theory]
    [InlineData("https://github.com/lokeshgovindu/PasteJump")]
    [InlineData("https://github.com/lokeshgovindu/PasteJump/")]
    [InlineData("https://github.com/lokeshgovindu/PasteJump.git")]
    public void The_api_url_is_derived_from_the_repository_url(string repository)
        => Assert.Equal(
            "https://api.github.com/repos/lokeshgovindu/PasteJump/releases/latest",
            UpdateCheck.LatestReleaseApiUrl(repository));

    /// <summary>
    /// Anything that is not a GitHub repository root returns null rather than a guess, so a project moved
    /// elsewhere fails visibly instead of quietly requesting nonsense.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://example.com/owner/repo")]
    [InlineData("https://github.com/lokeshgovindu")]
    public void A_url_that_is_not_a_github_repository_is_refused(string? repository)
        => Assert.Null(UpdateCheck.LatestReleaseApiUrl(repository));

    /// <summary>The URL this build actually carries has to work, or the feature is dead on arrival.</summary>
    [Fact]
    public void This_builds_own_repository_url_yields_an_api_url()
    {
        Assert.NotEqual(string.Empty, AppVersion.RepositoryUrl);
        Assert.NotNull(UpdateCheck.LatestReleaseApiUrl(AppVersion.RepositoryUrl));
    }

    // ------------------------------------------------------------- tag parsing

    [Theory]
    [InlineData("v2026.1.0.1", "2026.1.0.1")]
    [InlineData("2026.1.0.1", "2026.1.0.1")]
    [InlineData("V2026.1.0.1", "2026.1.0.1")]
    [InlineData("  v2026.1.0.1  ", "2026.1.0.1")]
    [InlineData("v2026.1.0.1-beta", "2026.1.0.1")]
    [InlineData("2026.2", "2026.2")]
    public void A_tag_parses_into_a_version(string tag, string expected)
    {
        Assert.True(UpdateCheck.TryParseVersion(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("release-candidate")]
    public void A_tag_that_is_not_a_version_is_refused(string? tag)
        => Assert.False(UpdateCheck.TryParseVersion(tag, out _));

    // -------------------------------------------------------------- comparison

    [Theory]
    [InlineData("2026.1.0.0", "v2026.1.0.1")]
    [InlineData("2026.1.0.9", "v2026.1.0.10")]   // string comparison would get this one wrong
    [InlineData("2026.1.0.0", "v2026.2.0.0")]
    [InlineData("2026.1.0.0", "v2026.2")]        // fewer components, still newer
    public void A_newer_release_is_recognised(string running, string tag)
        => Assert.True(UpdateCheck.IsNewer(running, tag));

    /// <summary>
    /// The same version is not an update, and neither is an older one: offering a downgrade because a tag was
    /// re-pointed would be worse than saying nothing.
    /// </summary>
    [Theory]
    [InlineData("2026.1.0.1", "v2026.1.0.1")]
    [InlineData("2026.1.0.1", "2026.1.0.1")]
    [InlineData("2026.1.0.1", "v2026.1.0.0")]
    [InlineData("2026.2.0.0", "v2026.1.9.9")]
    public void The_same_or_an_older_release_is_not_an_update(string running, string tag)
        => Assert.False(UpdateCheck.IsNewer(running, tag));

    /// <summary>
    /// Trailing zeros are the same version, not a newer one - <c>Version</c> treats an unspecified component as
    /// -1, which would make 2026.2 look OLDER than 2026.2.0.0 without normalising.
    /// </summary>
    [Fact]
    public void Missing_components_count_as_zero_rather_than_as_less()
    {
        Assert.False(UpdateCheck.IsNewer("2026.2.0.0", "v2026.2"));
        Assert.False(UpdateCheck.IsNewer("2026.2", "v2026.2.0.0"));
    }

    [Theory]
    [InlineData("2026.1.0.0", "not-a-version")]
    [InlineData("2026.1.0.0", null)]
    public void An_unreadable_tag_is_never_an_update(string running, string? tag)
        => Assert.False(UpdateCheck.IsNewer(running, tag));

    // ------------------------------------------------------------ JSON parsing

    private const string RealisticJson = """
        {
          "tag_name": "v2026.1.0.5",
          "name": "PasteJump 2026.1.0.5",
          "draft": false,
          "prerelease": false,
          "html_url": "https://github.com/lokeshgovindu/PasteJump/releases/tag/v2026.1.0.5",
          "published_at": "2026-08-11T09:00:00Z"
        }
        """;

    [Fact]
    public void A_release_is_read_out_of_the_json()
    {
        Assert.True(UpdateCheck.TryParseRelease(RealisticJson, out var release));

        Assert.Equal(Version.Parse("2026.1.0.5"), release.Version);
        Assert.Equal("v2026.1.0.5", release.Tag);
        Assert.Equal(
            "https://github.com/lokeshgovindu/PasteJump/releases/tag/v2026.1.0.5",
            release.PageUrl);
    }

    /// <summary>
    /// Drafts and pre-releases are refused. <c>/releases/latest</c> excludes them already, but the same shape
    /// comes back from other endpoints, and offering somebody a draft is worse than offering nothing.
    /// </summary>
    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    public void A_draft_or_prerelease_is_refused(string flag)
    {
        var json = RealisticJson.Replace($"\"{flag}\": false", $"\"{flag}\": true", StringComparison.Ordinal);

        Assert.False(UpdateCheck.TryParseRelease(json, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{ "tag_name": "nightly" }""")]
    public void Anything_unusable_is_refused_rather_than_thrown(string? json)
        => Assert.False(UpdateCheck.TryParseRelease(json, out _));

    /// <summary>A release with no page URL still parses; the caller falls back to the releases list.</summary>
    [Fact]
    public void A_missing_page_url_is_tolerated()
    {
        Assert.True(UpdateCheck.TryParseRelease("""{ "tag_name": "v2026.3.0.0" }""", out var release));

        Assert.Equal(Version.Parse("2026.3.0.0"), release.Version);
        Assert.Equal(string.Empty, release.PageUrl);
    }
}
