namespace PasteJump.Core.Theming;

/// <summary>
/// The three theme names the application always understands, whatever theme files exist.
/// <para>
/// Named constants rather than literals scattered about, because these strings are three things at once: the value
/// stored in settings, the label shown in the dialog, and the names a theme file may not take. A literal in one
/// place and a constant in another is how those three drift apart.
/// </para>
/// </summary>
public static class ThemeNames
{
    /// <summary>Follow the Windows "choose your mode" app setting, and track changes to it live.</summary>
    public const string System = "System";

    /// <summary>The built-in light palette, which every light theme inherits from.</summary>
    public const string Light = "Light";

    /// <summary>The built-in dark palette, which every dark theme inherits from.</summary>
    public const string Dark = "Dark";

    /// <summary>All three, in the order the settings dialog should offer them.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } = [System, Light, Dark];

    /// <summary>
    /// Whether this name is one of the three, compared the way settings are: case-insensitively, so a hand-edited
    /// <c>"theme": "dark"</c> works.
    /// </summary>
    public static bool IsBuiltIn(string? name)
        => BuiltIn.Any(known => string.Equals(known, name, StringComparison.OrdinalIgnoreCase));
}
