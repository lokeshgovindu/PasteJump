using System.Globalization;
using System.Reflection;

namespace PasteJump.Core;

/// <summary>
/// The application version, read once from the entry assembly.
/// <para>
/// Resolved from <see cref="AssemblyInformationalVersionAttribute"/> with a fall back to the assembly
/// version, rather than hard-coding a string that would silently diverge from the build.
/// </para>
/// </summary>
public static class AppVersion
{
    private static readonly Lazy<string> Cached = new(Resolve);

    private static readonly Lazy<string> CachedCopyright = new(ResolveCopyright);

    private static readonly Lazy<string> CachedAuthor = new(() => Metadata("Author"));

    private static readonly Lazy<string> CachedAuthorUrl = new(() => Metadata("AuthorUrl"));

    private static readonly Lazy<string> CachedRepositoryUrl = new(() => Metadata("RepositoryUrl"));

    private static readonly Lazy<DateTimeOffset?> CachedBuildTime = new(ResolveBuildTime);

    /// <summary>Version as <c>major.minor.build.revision</c>, e.g. <c>2026.1.0.0</c>.</summary>
    public static string Current => Cached.Value;

    /// <summary>
    /// Copyright line from the assembly, as set by <c>Directory.Build.props</c>.
    /// <para>
    /// Read from the attribute rather than written out again in the About window, for the same reason
    /// the version is: two copies of the same string diverge the first time only one of them is
    /// updated, and the stale one is the one on screen.
    /// </para>
    /// </summary>
    public static string Copyright => CachedCopyright.Value;

    /// <summary>
    /// The author's name, from assembly metadata. Empty when the attribute is absent.
    /// <para>
    /// An <c>AssemblyMetadata</c> attribute rather than <c>&lt;Authors&gt;</c>, which is a NuGet packaging
    /// property and emits no attribute at all, and rather than <c>AssemblyCompany</c>, which is the product
    /// name here. The About window needs the name on its own to turn that part of the copyright line into a
    /// link, and it must be the same string the copyright was built from or the match would fail.
    /// </para>
    /// </summary>
    public static string Author => CachedAuthor.Value;

    /// <summary>The author's profile URL, from assembly metadata. Empty when absent.</summary>
    public static string AuthorUrl => CachedAuthorUrl.Value;

    /// <summary>
    /// The project's repository URL, from assembly metadata. Empty when absent.
    /// <para>
    /// One definition for the two things that need it: the About window's link, and the update check, which
    /// derives the GitHub API URL from it. Spelling it in both places is how they would come to disagree.
    /// </para>
    /// </summary>
    public static string RepositoryUrl => CachedRepositoryUrl.Value;

    /// <summary>
    /// When this build was produced, or null when it cannot be established.
    /// <para>
    /// From an <c>AssemblyMetadata</c> attribute stamped by <c>PasteJump.App.csproj</c>, because the PE
    /// header's timestamp field holds a content hash under a deterministic build rather than a time. Falls
    /// back to the executable's own last-write time, which is right for a published exe and merely
    /// approximate for one that has been copied by something that did not preserve it.
    /// </para>
    /// </summary>
    public static DateTimeOffset? BuildTimestamp => CachedBuildTime.Value;

    // There is deliberately no shortened Display form. It existed only to trim a trailing ".0" for the tray
    // tooltip, and the tooltip now shows the full four-part version - it is the quickest place to read the
    // build number from without opening a window, so it should match what a bug report needs verbatim.

    private static string Resolve()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Defensive: the SDK appends "+<commit sha>" unless that is switched off in the build, and
            // a git hash in a tray tooltip is not what anyone wants to read.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    }

    private static string ResolveCopyright()
    {
        // Own assembly, not the entry assembly. The version has to come from the entry assembly so a
        // host reports its own number, but the copyright is the product's and every assembly in the
        // build carries the same one - and reading it here keeps the UI smoke harness, whose entry
        // assembly is the harness itself, from showing a blank line.
        var copyright = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyCopyrightAttribute>()
            ?.Copyright;

        return string.IsNullOrWhiteSpace(copyright) ? string.Empty : copyright;
    }

    /// <summary>
    /// One <c>AssemblyMetadata</c> value from this assembly. Own assembly rather than the entry assembly, for
    /// the same reason as the copyright: every project in the build carries these, and the UI smoke harness
    /// would otherwise show blanks because its entry assembly is the harness.
    /// </summary>
    private static string Metadata(string key)
    {
        var value = typeof(AppVersion).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal))
            ?.Value;

        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    private static DateTimeOffset? ResolveBuildTime()
    {
        // Entry assembly, unlike the metadata above: only the app project stamps this, because the value
        // changes on every evaluation and would otherwise make every project recompile on every build.
        var stamped = (Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly)
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "BuildTimestampUtc", StringComparison.Ordinal))
            ?.Value;

        if (DateTimeOffset.TryParse(
                stamped,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return parsed;
        }

        // The executable's own file time. Approximate - a copy can change it - but better than nothing for a
        // host that carries no stamp, which is every project here except the app.
        try
        {
            var path = Environment.ProcessPath;

            return string.IsNullOrEmpty(path) ? null : new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception)
        {
            // A path we cannot stat is not worth failing the About window over.
            return null;
        }
    }
}
