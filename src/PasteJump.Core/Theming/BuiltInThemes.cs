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

    /// <summary>Catppuccin Mocha. Soft pastels on a violet-tinted charcoal - the gentlest of the dark set.</summary>
    private const string CatppuccinMocha = """
        {
            "name": "Catppuccin Mocha",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#1E1E2E",
                "SurfaceRaisedBrush": "#272739",
                "BorderBrush": "#45475A",
                "TextBrush": "#CDD6F4",
                "MutedTextBrush": "#A6ADC8",
                "AccentBrush": "#89B4FA",
                "AccentHoverBrush": "#9EC1FB",
                "AccentPressedBrush": "#B4CEFC",
                "AccentTextBrush": "#1E1E2E",
                "WarnBrush": "#F9E2AF",
                "DangerBrush": "#F38BA8",
                "ControlBackgroundBrush": "#272739",
                "ControlBorderBrush": "#45475A",
                "ControlHoverBrush": "#313244",
                "ControlPressedBrush": "#3B3D51",
                "ControlDisabledTextBrush": "#6C7086",
                "SelectionBrush": ["#3B3D57", "#313248"],
                "SelectionBorderBrush": "#B4BEFE",
                "SelectionTextBrush": "#EFF1F8",
                "HoverBorderBrush": "#313244",
                "ModifiedRowBrush": "#3B3A45",
                "ScrollThumbBrush": "#45475A",
                "ScrollThumbHoverBrush": "#585B70",
                "SplitterLineBrush": "#45475A",
                "ShadowColor": "#FF11111B"
            }
        }
        """;

    /// <summary>Catppuccin Latte. The light sibling - cool greys with the same saturated accents.</summary>
    private const string CatppuccinLatte = """
        {
            "name": "Catppuccin Latte",
            "basedOn": "light",
            "colors": {
                "SurfaceBrush": "#EFF1F5",
                "SurfaceRaisedBrush": "#FFFFFF",
                "BorderBrush": "#CCD0DA",
                "TextBrush": "#4C4F69",
                "MutedTextBrush": "#6C6F85",
                "AccentBrush": "#1E66F5",
                "AccentHoverBrush": "#3B7BF7",
                "AccentPressedBrush": "#5B92F9",
                "WarnBrush": "#DF8E1D",
                "DangerBrush": "#D20F39",
                "ControlBackgroundBrush": "#FFFFFF",
                "ControlBorderBrush": "#BCC0CC",
                "ControlHoverBrush": "#E6E9EF",
                "ControlPressedBrush": "#DCE0E8",
                "ControlDisabledTextBrush": "#9CA0B0",
                "SelectionBrush": ["#DCE6FB", "#C6D6F8"],
                "SelectionBorderBrush": "#7287FD",
                "SelectionTextBrush": "#1E2030",
                "HoverBorderBrush": "#E6E9EF",
                "ModifiedRowBrush": "#FAF0DC",
                "ScrollThumbBrush": "#BCC0CC",
                "ScrollThumbHoverBrush": "#9CA0B0",
                "SplitterLineBrush": "#ACB0BE",
                "ShadowColor": "#FF5C5F77"
            }
        }
        """;

    /// <summary>Tokyo Night. Deep indigo with cool blue accents.</summary>
    private const string TokyoNight = """
        {
            "name": "Tokyo Night",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#1A1B26",
                "SurfaceRaisedBrush": "#20212E",
                "BorderBrush": "#2F344D",
                "TextBrush": "#C0CAF5",
                "MutedTextBrush": "#787C99",
                "AccentBrush": "#7AA2F7",
                "AccentHoverBrush": "#8FB1F8",
                "AccentPressedBrush": "#A5C1FA",
                "AccentTextBrush": "#16161E",
                "WarnBrush": "#E0AF68",
                "DangerBrush": "#F7768E",
                "ControlBackgroundBrush": "#20212E",
                "ControlBorderBrush": "#343A55",
                "ControlHoverBrush": "#292E42",
                "ControlPressedBrush": "#32384F",
                "ControlDisabledTextBrush": "#565F89",
                "SelectionBrush": ["#2E3550", "#262C43"],
                "SelectionBorderBrush": "#7DCFFF",
                "SelectionTextBrush": "#C0CAF5",
                "HoverBorderBrush": "#292E42",
                "ModifiedRowBrush": "#33323E",
                "ScrollThumbBrush": "#343A55",
                "ScrollThumbHoverBrush": "#4A5178",
                "SplitterLineBrush": "#343A55",
                "ShadowColor": "#FF0D0E14"
            }
        }
        """;

    /// <summary>One Dark. Atom's palette, the most familiar dark grey-blue of the lot.</summary>
    private const string OneDark = """
        {
            "name": "One Dark",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#282C34",
                "SurfaceRaisedBrush": "#31353F",
                "BorderBrush": "#3E4451",
                "TextBrush": "#ABB2BF",
                "MutedTextBrush": "#828997",
                "AccentBrush": "#61AFEF",
                "AccentHoverBrush": "#75BAF1",
                "AccentPressedBrush": "#8CC6F4",
                "AccentTextBrush": "#21252B",
                "WarnBrush": "#E5C07B",
                "DangerBrush": "#E06C75",
                "ControlBackgroundBrush": "#31353F",
                "ControlBorderBrush": "#4B5263",
                "ControlHoverBrush": "#383D48",
                "ControlPressedBrush": "#414855",
                "ControlDisabledTextBrush": "#5C6370",
                "SelectionBrush": ["#3E4451", "#353B47"],
                "SelectionBorderBrush": "#98C379",
                "SelectionTextBrush": "#DCDFE4",
                "HoverBorderBrush": "#383D48",
                "ModifiedRowBrush": "#3D3B33",
                "ScrollThumbBrush": "#4B5263",
                "ScrollThumbHoverBrush": "#5C6370",
                "SplitterLineBrush": "#4B5263",
                "ShadowColor": "#FF14171C"
            }
        }
        """;

    /// <summary>Rose Pine. Muted plum and rose - the most distinctive palette here, and still readable.</summary>
    private const string RosePine = """
        {
            "name": "Rose Pine",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#191724",
                "SurfaceRaisedBrush": "#1F1D2E",
                "BorderBrush": "#403D52",
                "TextBrush": "#E0DEF4",
                "MutedTextBrush": "#908CAA",
                "AccentBrush": "#EBBCBA",
                "AccentHoverBrush": "#F0CBC9",
                "AccentPressedBrush": "#F4D9D8",
                "AccentTextBrush": "#191724",
                "WarnBrush": "#F6C177",
                "DangerBrush": "#EB6F92",
                "ControlBackgroundBrush": "#1F1D2E",
                "ControlBorderBrush": "#403D52",
                "ControlHoverBrush": "#26233A",
                "ControlPressedBrush": "#302D44",
                "ControlDisabledTextBrush": "#6E6A86",
                "SelectionBrush": ["#33304A", "#2A273D"],
                "SelectionBorderBrush": "#9CCFD8",
                "SelectionTextBrush": "#E0DEF4",
                "HoverBorderBrush": "#26233A",
                "ModifiedRowBrush": "#38313C",
                "ScrollThumbBrush": "#403D52",
                "ScrollThumbHoverBrush": "#56526E",
                "SplitterLineBrush": "#403D52",
                "ShadowColor": "#FF100E1A"
            }
        }
        """;

    /// <summary>Everforest Dark. Green-grey and low-contrast, the easiest of the dark themes on tired eyes.</summary>
    private const string EverforestDark = """
        {
            "name": "Everforest Dark",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#2D353B",
                "SurfaceRaisedBrush": "#343F44",
                "BorderBrush": "#4A555B",
                "TextBrush": "#D3C6AA",
                "MutedTextBrush": "#9DA9A0",
                "AccentBrush": "#A7C080",
                "AccentHoverBrush": "#B6CB95",
                "AccentPressedBrush": "#C5D6AB",
                "AccentTextBrush": "#2D353B",
                "WarnBrush": "#DBBC7F",
                "DangerBrush": "#E67E80",
                "ControlBackgroundBrush": "#343F44",
                "ControlBorderBrush": "#4A555B",
                "ControlHoverBrush": "#3D484D",
                "ControlPressedBrush": "#465258",
                "ControlDisabledTextBrush": "#7A8478",
                "SelectionBrush": ["#475258", "#3D484D"],
                "SelectionBorderBrush": "#83C092",
                "SelectionTextBrush": "#E4DCC8",
                "HoverBorderBrush": "#3D484D",
                "ModifiedRowBrush": "#48413A",
                "ScrollThumbBrush": "#4A555B",
                "ScrollThumbHoverBrush": "#5C6A72",
                "SplitterLineBrush": "#4A555B",
                "ShadowColor": "#FF1E2326"
            }
        }
        """;

    /// <summary>Kanagawa. Ink-wash blues over sumi black, with a paper-white foreground.</summary>
    private const string Kanagawa = """
        {
            "name": "Kanagawa",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#1F1F28",
                "SurfaceRaisedBrush": "#2A2A37",
                "BorderBrush": "#363646",
                "TextBrush": "#DCD7BA",
                "MutedTextBrush": "#A9A18B",
                "AccentBrush": "#7E9CD8",
                "AccentHoverBrush": "#92ACDF",
                "AccentPressedBrush": "#A7BCE6",
                "AccentTextBrush": "#1F1F28",
                "WarnBrush": "#FF9E3B",
                "DangerBrush": "#C34043",
                "ControlBackgroundBrush": "#2A2A37",
                "ControlBorderBrush": "#43435A",
                "ControlHoverBrush": "#323240",
                "ControlPressedBrush": "#3A3A4C",
                "ControlDisabledTextBrush": "#727169",
                "SelectionBrush": ["#2D4F67", "#223249"],
                "SelectionBorderBrush": "#6A9589",
                "SelectionTextBrush": "#E6E0C8",
                "HoverBorderBrush": "#323240",
                "ModifiedRowBrush": "#3B3A32",
                "ScrollThumbBrush": "#43435A",
                "ScrollThumbHoverBrush": "#54546D",
                "SplitterLineBrush": "#43435A",
                "ShadowColor": "#FF16161D"
            }
        }
        """;

    /// <summary>
    /// GitHub Dark, the counterpart of GitHub Light and for the same reason: the least mannered dark theme here.
    /// </summary>
    /// <remarks>
    /// Added after someone asked why the light one had no pair -- there was no reason, only that this list grew a
    /// light default and never a dark one. Values are GitHub's own dark defaults from Primer: canvas #0D1117,
    /// the subtle surface above it #161B22, foreground #E6EDF3, accent #58A6FF.
    /// </remarks>
    private const string GitHubDark = """
        {
            "name": "GitHub Dark",
            "basedOn": "dark",
            "colors": {
                "SurfaceBrush": "#0D1117",
                "SurfaceRaisedBrush": "#161B22",
                "BorderBrush": "#30363D",
                "TextBrush": "#E6EDF3",
                "MutedTextBrush": "#848D97",
                "AccentBrush": "#58A6FF",
                "AccentHoverBrush": "#79B8FF",
                "AccentPressedBrush": "#A5D6FF",
                "AccentTextBrush": "#0D1117",
                "WarnBrush": "#D29922",
                "DangerBrush": "#F85149",
                "ControlBackgroundBrush": "#161B22",
                "ControlBorderBrush": "#30363D",
                "ControlHoverBrush": "#1F242C",
                "ControlPressedBrush": "#262C36",
                "ControlDisabledTextBrush": "#6E7681",
                "SelectionBrush": ["#1C3A5E", "#132B45"],
                "SelectionBorderBrush": "#388BFD",
                "SelectionTextBrush": "#E6EDF3",
                "HoverBorderBrush": "#1F242C",
                "ModifiedRowBrush": "#33270A",
                "ScrollThumbBrush": "#30363D",
                "ScrollThumbHoverBrush": "#484F58",
                "SplitterLineBrush": "#30363D",
                "ShadowColor": "#FF010409"
            }
        }
        """;

    /// <summary>
    /// GitHub Light. Not a mood but a default - the cleanest light theme here, and the one that looks least like a
    /// theme at all.
    /// </summary>
    private const string GitHubLight = """
        {
            "name": "GitHub Light",
            "basedOn": "light",
            "colors": {
                "SurfaceBrush": "#F6F8FA",
                "SurfaceRaisedBrush": "#FFFFFF",
                "BorderBrush": "#D0D7DE",
                "TextBrush": "#1F2328",
                "MutedTextBrush": "#656D76",
                "AccentBrush": "#0969DA",
                "AccentHoverBrush": "#1F7BE8",
                "AccentPressedBrush": "#3D8DF0",
                "WarnBrush": "#9A6700",
                "DangerBrush": "#CF222E",
                "ControlBackgroundBrush": "#FFFFFF",
                "ControlBorderBrush": "#D0D7DE",
                "ControlHoverBrush": "#EFF2F5",
                "ControlPressedBrush": "#E4E8ED",
                "ControlDisabledTextBrush": "#8C959F",
                "SelectionBrush": ["#DDF4FF", "#C6E9FF"],
                "SelectionBorderBrush": "#54AEFF",
                "SelectionTextBrush": "#0A3069",
                "HoverBorderBrush": "#EFF2F5",
                "ModifiedRowBrush": "#FFF8C5",
                "ScrollThumbBrush": "#CFD5DB",
                "ScrollThumbHoverBrush": "#AFB8C1",
                "SplitterLineBrush": "#C2C9D0",
                "ShadowColor": "#FF57606A"
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
        CatppuccinMocha,
        CatppuccinLatte,
        TokyoNight,
        OneDark,
        Monokai,
        Nord,
        Dracula,
        RosePine,
        EverforestDark,
        Kanagawa,
        GruvboxDark,
        Zenburn,
        GitHubLight,
        GitHubDark,
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
