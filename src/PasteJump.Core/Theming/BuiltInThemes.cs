namespace PasteJump.Core.Theming;

/// <summary>
/// Extra themes that ship with the application, written in exactly the format a user's own theme file uses.
/// <para>
/// Deliberately not compiled <c>ResourceDictionary</c> files like Light and Dark. Expressing them as theme
/// definitions means there is one code path rather than two, and it keeps the format honest: if a built-in theme
/// can be written this way, so can anyone's. They also serve as worked examples - each one overrides a handful of
/// keys and inherits the rest, which is the shape a hand-written theme should have.
/// </para>
/// <para>
/// Light and Dark stay as XAML because they are the <em>bases</em>: something has to define all 25 keys for a
/// partial theme to inherit from.
/// </para>
/// </summary>
public static class BuiltInThemes
{
    /// <summary>
    /// A deeper, cooler dark theme - nearly black with an indigo accent, for anyone who finds the default dark
    /// palette too grey.
    /// </summary>
    private const string Midnight = """
        {
            "name": "Midnight",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#0B0F1A",
                "SurfaceRaisedBrush": "#141A2A",
                "BorderBrush": "#26304A",
                "TextBrush": "#E4E8F5",
                "MutedTextBrush": "#8B93AD",
                "AccentBrush": "#6C7BFF",
                "AccentHoverBrush": "#8390FF",
                "AccentPressedBrush": "#9AA4FF",
                "ControlBackgroundBrush": "#141A2A",
                "ControlBorderBrush": "#2E3956",
                "ControlHoverBrush": "#1D2438",
                "ControlPressedBrush": "#252E47",
                "SelectionBrush": ["#243056", "#1A2440"],
                "SelectionBorderBrush": "#4A5A8C",
                "SelectionTextBrush": "#EDF0FA",
                "HoverBorderBrush": "#1D2438",
                "SplitterLineBrush": "#2E3956",
                "ShadowColor": "#FF000008"
            }
        }
        """;

    /// <summary>
    /// A warm light theme - paper rather than white, with an amber accent. Easier on the eyes under lamplight, and
    /// the one that shows most clearly that a theme is more than a light/dark switch.
    /// </summary>
    private const string Sepia = """
        {
            "name": "Sepia",
            "basedOn": "light",
            "colors": {
                "SurfaceBrush": "#F5EEE1",
                "SurfaceRaisedBrush": "#FBF7EE",
                "BorderBrush": "#DDD0B8",
                "TextBrush": "#2C2418",
                "MutedTextBrush": "#7A6B55",
                "AccentBrush": "#A8641A",
                "AccentHoverBrush": "#BF7524",
                "AccentPressedBrush": "#D08A3C",
                "WarnBrush": "#9A6410",
                "ControlBackgroundBrush": "#FBF7EE",
                "ControlBorderBrush": "#CDBEA2",
                "ControlHoverBrush": "#EFE6D5",
                "ControlPressedBrush": "#E5D9C3",
                "ControlDisabledTextBrush": "#AFA28C",
                "SelectionBrush": ["#F0DFC0", "#E4CDA2"],
                "SelectionBorderBrush": "#C2A66E",
                "SelectionTextBrush": "#2A1F10",
                "HoverBorderBrush": "#EFE6D5",
                "ModifiedRowBrush": "#FBEFCD",
                "ScrollThumbBrush": "#C9BBA2",
                "ScrollThumbHoverBrush": "#A99A80",
                "SplitterLineBrush": "#B7A88C",
                "ShadowColor": "#FF4A3B22"
            }
        }
        """;

    /// <summary>
    /// The shipped themes, parsed. Anything that fails to parse is left out rather than throwing: this is reached
    /// during start-up, and a broken built-in theme is a bug to fix in the source, not a reason to refuse to run.
    /// A test asserting that every source parses is what catches it instead - see <c>ThemeDefinitionTests</c>.
    /// </summary>
    public static IReadOnlyList<ThemeDefinition> All { get; } = Parse();

    private static IReadOnlyList<ThemeDefinition> Parse()
    {
        var themes = new List<ThemeDefinition>();

        foreach (var json in new[] { Midnight, Sepia })
        {
            if (ThemeDefinition.TryParse(json, out var theme, out _))
            {
                themes.Add(theme);
            }
        }

        return themes;
    }

    /// <summary>The raw JSON of every built-in theme, for tests and for writing one out as a starting point.</summary>
    public static IReadOnlyList<string> Sources { get; } = [Midnight, Sepia];
}
