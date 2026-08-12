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
    /// Solarized, dark. Ethan Schoonover's palette, the most widely reimplemented of these - chosen for its
    /// deliberately low-contrast background rather than for novelty.
    /// </summary>
    private const string SolarizedDark = """
        {
            "name": "Solarized Dark",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#002B36",
                "SurfaceRaisedBrush": "#073642",
                "BorderBrush": "#0F4B58",
                "TextBrush": "#93A1A1",
                "MutedTextBrush": "#657B83",
                "AccentBrush": "#268BD2",
                "AccentHoverBrush": "#3A9BDE",
                "AccentPressedBrush": "#57ABE6",
                "AccentTextBrush": "#FDF6E3",
                "WarnBrush": "#B58900",
                "DangerBrush": "#DC322F",
                "ControlBackgroundBrush": "#073642",
                "ControlBorderBrush": "#125361",
                "ControlHoverBrush": "#0B4451",
                "ControlPressedBrush": "#124E5C",
                "ControlDisabledTextBrush": "#4E6B72",
                "SelectionBrush": ["#0F4B58", "#0A3D48"],
                "SelectionBorderBrush": "#2AA198",
                "SelectionTextBrush": "#EEE8D5",
                "HoverBorderBrush": "#0B4451",
                "ModifiedRowBrush": "#0E3F42",
                "ScrollThumbBrush": "#12545F",
                "ScrollThumbHoverBrush": "#1B6C78",
                "SplitterLineBrush": "#12545F",
                "ShadowColor": "#FF001318"
            }
        }
        """;

    /// <summary>Solarized, light. The same hues against its cream background.</summary>
    private const string SolarizedLight = """
        {
            "name": "Solarized Light",
            "basedOn": "light",
            "colors": {
                "SurfaceBrush": "#FDF6E3",
                "SurfaceRaisedBrush": "#FFFCF2",
                "BorderBrush": "#E4DCC4",
                "TextBrush": "#586E75",
                "MutedTextBrush": "#93A1A1",
                "AccentBrush": "#268BD2",
                "AccentHoverBrush": "#3A9BDE",
                "AccentPressedBrush": "#57ABE6",
                "WarnBrush": "#B58900",
                "DangerBrush": "#DC322F",
                "ControlBackgroundBrush": "#FFFCF2",
                "ControlBorderBrush": "#D6CDB4",
                "ControlHoverBrush": "#F4EDD8",
                "ControlPressedBrush": "#EAE2CB",
                "ControlDisabledTextBrush": "#B4AE99",
                "SelectionBrush": ["#DCE8E0", "#C8DCD4"],
                "SelectionBorderBrush": "#2AA198",
                "SelectionTextBrush": "#073642",
                "HoverBorderBrush": "#EFE8D2",
                "ModifiedRowBrush": "#FBEFCB",
                "ScrollThumbBrush": "#D2C9AF",
                "ScrollThumbHoverBrush": "#B6AC90",
                "SplitterLineBrush": "#C6BCA0",
                "ShadowColor": "#FF6B6247"
            }
        }
        """;

    /// <summary>Monokai. The one the request came with a screenshot of - dark grey-green with a magenta accent.</summary>
    private const string Monokai = """
        {
            "name": "Monokai",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#272822",
                "SurfaceRaisedBrush": "#31322B",
                "BorderBrush": "#464740",
                "TextBrush": "#F8F8F2",
                "MutedTextBrush": "#A6A69B",
                "AccentBrush": "#F92672",
                "AccentHoverBrush": "#FB4B8B",
                "AccentPressedBrush": "#FC6FA2",
                "AccentTextBrush": "#FFF8F8",
                "WarnBrush": "#E6DB74",
                "DangerBrush": "#F92672",
                "ControlBackgroundBrush": "#31322B",
                "ControlBorderBrush": "#54554C",
                "ControlHoverBrush": "#3B3C34",
                "ControlPressedBrush": "#45463D",
                "ControlDisabledTextBrush": "#75715E",
                "SelectionBrush": ["#49483E", "#3C3B33"],
                "SelectionBorderBrush": "#A6E22E",
                "SelectionTextBrush": "#F8F8F2",
                "HoverBorderBrush": "#3B3C34",
                "ModifiedRowBrush": "#403F32",
                "ScrollThumbBrush": "#54554C",
                "ScrollThumbHoverBrush": "#75715E",
                "SplitterLineBrush": "#54554C",
                "ShadowColor": "#FF14150F"
            }
        }
        """;

    /// <summary>Nord. Arctic blue-greys, the calmest of the dark set.</summary>
    private const string Nord = """
        {
            "name": "Nord",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#2E3440",
                "SurfaceRaisedBrush": "#3B4252",
                "BorderBrush": "#4C566A",
                "TextBrush": "#ECEFF4",
                "MutedTextBrush": "#9BA6B8",
                "AccentBrush": "#88C0D0",
                "AccentHoverBrush": "#9FCEDB",
                "AccentPressedBrush": "#B4DCE6",
                "AccentTextBrush": "#2E3440",
                "WarnBrush": "#EBCB8B",
                "DangerBrush": "#BF616A",
                "ControlBackgroundBrush": "#3B4252",
                "ControlBorderBrush": "#4C566A",
                "ControlHoverBrush": "#434C5E",
                "ControlPressedBrush": "#4C566A",
                "ControlDisabledTextBrush": "#6C7A94",
                "SelectionBrush": ["#4C566A", "#434C5E"],
                "SelectionBorderBrush": "#81A1C1",
                "SelectionTextBrush": "#ECEFF4",
                "HoverBorderBrush": "#434C5E",
                "ModifiedRowBrush": "#434A3E",
                "ScrollThumbBrush": "#4C566A",
                "ScrollThumbHoverBrush": "#5E6B85",
                "SplitterLineBrush": "#4C566A",
                "ShadowColor": "#FF15181F"
            }
        }
        """;

    /// <summary>Dracula. Purple-leaning dark, higher contrast than Nord.</summary>
    private const string Dracula = """
        {
            "name": "Dracula",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#282A36",
                "SurfaceRaisedBrush": "#343746",
                "BorderBrush": "#4A4D62",
                "TextBrush": "#F8F8F2",
                "MutedTextBrush": "#9EA3BE",
                "AccentBrush": "#BD93F9",
                "AccentHoverBrush": "#CBA8FB",
                "AccentPressedBrush": "#D8BEFC",
                "AccentTextBrush": "#21222C",
                "WarnBrush": "#FFB86C",
                "DangerBrush": "#FF5555",
                "ControlBackgroundBrush": "#343746",
                "ControlBorderBrush": "#565A73",
                "ControlHoverBrush": "#3D4152",
                "ControlPressedBrush": "#464B5F",
                "ControlDisabledTextBrush": "#6272A4",
                "SelectionBrush": ["#44475A", "#3A3D4E"],
                "SelectionBorderBrush": "#8BE9FD",
                "SelectionTextBrush": "#F8F8F2",
                "HoverBorderBrush": "#3D4152",
                "ModifiedRowBrush": "#414436",
                "ScrollThumbBrush": "#565A73",
                "ScrollThumbHoverBrush": "#6272A4",
                "SplitterLineBrush": "#565A73",
                "ShadowColor": "#FF14151C"
            }
        }
        """;

    /// <summary>Gruvbox, dark. Warm retro browns - the dark theme for anyone who dislikes blue-grey.</summary>
    private const string GruvboxDark = """
        {
            "name": "Gruvbox Dark",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#282828",
                "SurfaceRaisedBrush": "#32302F",
                "BorderBrush": "#504945",
                "TextBrush": "#EBDBB2",
                "MutedTextBrush": "#A89984",
                "AccentBrush": "#D79921",
                "AccentHoverBrush": "#E3A72F",
                "AccentPressedBrush": "#FABD2F",
                "AccentTextBrush": "#282828",
                "WarnBrush": "#FE8019",
                "DangerBrush": "#CC241D",
                "ControlBackgroundBrush": "#32302F",
                "ControlBorderBrush": "#5A524C",
                "ControlHoverBrush": "#3C3836",
                "ControlPressedBrush": "#45403D",
                "ControlDisabledTextBrush": "#7C6F64",
                "SelectionBrush": ["#504945", "#453F3B"],
                "SelectionBorderBrush": "#98971A",
                "SelectionTextBrush": "#FBF1C7",
                "HoverBorderBrush": "#3C3836",
                "ModifiedRowBrush": "#463B24",
                "ScrollThumbBrush": "#5A524C",
                "ScrollThumbHoverBrush": "#7C6F64",
                "SplitterLineBrush": "#5A524C",
                "ShadowColor": "#FF141414"
            }
        }
        """;

    /// <summary>Zenburn. Deliberately low-contrast olive greys, the oldest palette here.</summary>
    private const string Zenburn = """
        {
            "name": "Zenburn",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#3F3F3F",
                "SurfaceRaisedBrush": "#4A4A4A",
                "BorderBrush": "#5F5F5F",
                "TextBrush": "#DCDCCC",
                "MutedTextBrush": "#9FAFAF",
                "AccentBrush": "#8CD0D3",
                "AccentHoverBrush": "#A2DBDE",
                "AccentPressedBrush": "#B8E5E7",
                "AccentTextBrush": "#2B2B2B",
                "WarnBrush": "#E0CF9F",
                "DangerBrush": "#CC9393",
                "ControlBackgroundBrush": "#4A4A4A",
                "ControlBorderBrush": "#6A6A6A",
                "ControlHoverBrush": "#535353",
                "ControlPressedBrush": "#5C5C5C",
                "ControlDisabledTextBrush": "#8A8A7A",
                "SelectionBrush": ["#5F5F5F", "#525252"],
                "SelectionBorderBrush": "#7F9F7F",
                "SelectionTextBrush": "#F0F0E0",
                "HoverBorderBrush": "#535353",
                "ModifiedRowBrush": "#54503C",
                "ScrollThumbBrush": "#6A6A6A",
                "ScrollThumbHoverBrush": "#8A8A8A",
                "SplitterLineBrush": "#6A6A6A",
                "ShadowColor": "#FF232323"
            }
        }
        """;

    /// <summary>
    /// The raw JSON of every shipped theme, in the order the settings dialog lists them, for tests and for writing
    /// one out as a starting point.
    /// <para>
    /// Declared <b>before</b> <see cref="All"/> and that is load-bearing, not style: static field initialisers run in
    /// declaration order, so with these the other way round <c>Parse</c> read a null list and every theme silently
    /// vanished behind a <c>TypeInitializationException</c>. Three tests caught it at once.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Sources { get; } =
    [
        Midnight,
        Sepia,
        SolarizedDark,
        SolarizedLight,
        Monokai,
        Nord,
        Dracula,
        GruvboxDark,
        Zenburn,
    ];

    /// <summary>
    /// The shipped themes, parsed. Anything that fails to parse is left out rather than throwing: this is reached
    /// during start-up, and a broken built-in theme is a bug to fix in the source, not a reason to refuse to run.
    /// A test asserting that every source parses is what catches it instead - see <c>ThemeDefinitionTests</c>.
    /// </summary>
    public static IReadOnlyList<ThemeDefinition> All { get; } = Parse();

    private static IReadOnlyList<ThemeDefinition> Parse()
    {
        var themes = new List<ThemeDefinition>();

        foreach (var json in Sources)
        {
            if (ThemeDefinition.TryParse(json, out var theme, out _))
            {
                themes.Add(theme);
            }
        }

        return themes;
    }
}
