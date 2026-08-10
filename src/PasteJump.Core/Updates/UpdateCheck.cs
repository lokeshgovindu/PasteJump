using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace PasteJump.Core.Updates;

/// <summary>What a published release says about itself, once the noise has been stripped out.</summary>
/// <param name="Version">The parsed version from the tag, e.g. <c>2026.1.0.1</c>.</param>
/// <param name="Tag">The tag exactly as published, e.g. <c>v2026.1.0.1</c>, for showing the user.</param>
/// <param name="PageUrl">Where a human should be sent to read about it and download it.</param>
public readonly record struct ReleaseInfo(Version Version, string Tag, string PageUrl);

/// <summary>
/// The decidable half of checking for updates: reading GitHub's answer and deciding whether it is newer.
/// <para>
/// Separated from the HTTP call and kept here because this is where the mistakes live - a tag that does not
/// parse, a release marked draft, a version with three components compared against one with four - and none of
/// that needs a network to test.
/// </para>
/// </summary>
public static class UpdateCheck
{
    /// <summary>
    /// Turns the URL of a GitHub repository into the API URL for its latest release.
    /// <para>
    /// Returns null for anything that is not a GitHub repository URL rather than guessing, so a project moved
    /// elsewhere fails visibly at the check instead of silently requesting nonsense.
    /// </para>
    /// </summary>
    public static string? LatestReleaseApiUrl(string? repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Two non-empty segments, owner and repository. Anything else is not a repository root.
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2)
        {
            return null;
        }

        var repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4]
            : parts[1];

        return $"https://api.github.com/repos/{parts[0]}/{repository}/releases/latest";
    }

    /// <summary>
    /// Reads the release out of GitHub's JSON, or returns false when there is nothing usable in it.
    /// <para>
    /// Drafts and pre-releases are rejected. <c>/releases/latest</c> already excludes them, but the same
    /// document shape comes back from other endpoints and from a hand-held test file, and offering someone a
    /// draft would be worse than offering nothing.
    /// </para>
    /// </summary>
    public static bool TryParseRelease(string? json, out ReleaseInfo release)
    {
        release = default;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            {
                return false;
            }

            if (!root.TryGetProperty("tag_name", out var tagElement)
                || tagElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var tag = tagElement.GetString() ?? string.Empty;

            if (!TryParseVersion(tag, out var version))
            {
                return false;
            }

            var pageUrl = root.TryGetProperty("html_url", out var urlElement)
                && urlElement.ValueKind == JsonValueKind.String
                    ? urlElement.GetString() ?? string.Empty
                    : string.Empty;

            release = new ReleaseInfo(version, tag, pageUrl);
            return true;
        }
        catch (JsonException)
        {
            // Not JSON at all - a proxy's error page, most likely. Indistinguishable from "no answer" as far as
            // the user is concerned, and neither is worth a stack trace.
            return false;
        }
    }

    /// <summary>
    /// Parses a release tag into a version, tolerating the conventional <c>v</c> prefix and a missing
    /// component or two.
    /// <para>
    /// <see cref="Version"/> rather than string comparison, because that is the only way <c>2026.1.0.10</c>
    /// sorts after <c>2026.1.0.9</c>.
    /// </para>
    /// </summary>
    public static bool TryParseVersion(string? tag, [NotNullWhen(true)] out Version? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var text = tag.Trim();

        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // Anything after a hyphen is a pre-release or build suffix - "2026.1.0.1-beta". Dropped rather than
        // rejected, so a tag that is otherwise readable still compares.
        var hyphen = text.IndexOf('-', StringComparison.Ordinal);

        if (hyphen > 0)
        {
            text = text[..hyphen];
        }

        return Version.TryParse(text, out version);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is newer than what is running.
    /// <para>
    /// Equal is not newer, and older is certainly not: a checker that offered a downgrade because a tag was
    /// re-pointed would be worse than one that said nothing. Missing components count as zero, which is what
    /// makes <c>2026.2</c> newer than <c>2026.1.0.0</c> - <see cref="Version"/> does that for us, and it is the
    /// reason this does not compare strings.
    /// </para>
    /// </summary>
    public static bool IsNewer(Version? running, Version? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        if (running is null)
        {
            return true;
        }

        return Normalise(candidate) > Normalise(running);
    }

    /// <inheritdoc cref="IsNewer(Version, Version)"/>
    public static bool IsNewer(string? runningVersion, string? candidateTag)
        => TryParseVersion(candidateTag, out var candidate)
            && IsNewer(TryParseVersion(runningVersion, out var running) ? running : null, candidate);

    /// <summary>
    /// Fills in unspecified components with zero, so a three-part version compares against a four-part one.
    /// <c>Version</c> treats an unspecified component as -1, which would otherwise make 2026.2 look older than
    /// 2026.2.0.0.
    /// </summary>
    private static Version Normalise(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(version.Build, 0),
        Math.Max(version.Revision, 0));

    private static bool IsTrue(JsonElement root, string property)
        => root.TryGetProperty(property, out var element)
            && element.ValueKind == JsonValueKind.True;

    /// <summary>
    /// A human-readable summary of the outcome, so the wording lives with the logic rather than in the dialog.
    /// </summary>
    public static string DescribeUpToDate(string runningVersion)
        => string.Format(
            CultureInfo.CurrentCulture,
            "PasteJump {0} is the latest version.",
            runningVersion);
}
