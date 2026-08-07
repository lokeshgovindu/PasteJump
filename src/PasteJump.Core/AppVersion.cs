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

    /// <summary>Version as <c>major.minor.build.revision</c>, e.g. <c>2026.1.0.0</c>.</summary>
    public static string Current => Cached.Value;

    /// <summary>Short form for display, dropping a trailing zero revision: <c>2026.1.0</c>.</summary>
    public static string Display
    {
        get
        {
            var value = Current;
            return value.EndsWith(".0", StringComparison.Ordinal) && value.Count(static c => c == '.') == 3
                ? value[..^2]
                : value;
        }
    }

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
}
