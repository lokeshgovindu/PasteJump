namespace PasteJump.Core.Theming;

/// <summary>What kind of resource a palette key holds, which decides what a theme file may say for it.</summary>
public enum PaletteEntryKind
{
    /// <summary>A solid colour. One value.</summary>
    Brush,

    /// <summary>
    /// A vertical two-stop gradient. Accepts one colour (flat) or two (top and bottom).
    /// <para>
    /// One key is like this - the row selection fill - and it is a gradient on purpose: a flat pale fill reads as
    /// "slightly different row" and gets lost against an alternating background.
    /// </para>
    /// </summary>
    Gradient,

    /// <summary>
    /// A bare <c>Color</c> rather than a brush, because the consumer takes a colour: <c>DropShadowEffect.Color</c>.
    /// A theme file writes it the same way; the difference only matters to whatever builds the dictionary.
    /// </summary>
    Color,
}

/// <summary>One entry in the palette contract.</summary>
/// <param name="Name">The resource key, exactly as the XAML palettes and every <c>DynamicResource</c> spell it.</param>
/// <param name="Kind">What may be written for it.</param>
/// <param name="Description">What it colours, for the settings dialog and for anyone writing a theme by hand.</param>
public readonly record struct PaletteKey(string Name, PaletteEntryKind Kind, string Description);

/// <summary>
/// The palette contract: every key a theme may set, and what it colours.
/// <para>
/// This list is the thing that makes user-authored themes possible at all. The palettes are
/// <c>ResourceDictionary</c> files and everything else refers to them through <c>DynamicResource</c>, so a key
/// that a theme fails to supply resolves to <em>nothing</em> and the control renders unstyled - silently, with no
/// error anywhere. Naming the keys here turns that into something checkable: a theme file may only mention keys on
/// this list, a partial theme inherits the rest from its base, and the UI smoke harness asserts the real
/// dictionaries define exactly these and no more.
/// </para>
/// <para>
/// In <c>Core</c> deliberately, with no WPF types: a colour here is four bytes, and turning it into a brush is the
/// App's job. That is what lets the parsing and the rules be tested without a message loop.
/// </para>
/// </summary>
public static class PaletteKeys
{
    /// <summary>
    /// Every key, in the order a theme file and the settings dialog should present them - surfaces first, then
    /// text, then the semantic colours, then control chrome. Grouped rather than alphabetical because someone
    /// writing a theme works outwards from the window background.
    /// </summary>
    public static IReadOnlyList<PaletteKey> All { get; } =
    [
        new("SurfaceBrush", PaletteEntryKind.Brush, "Window background"),
        new("SurfaceRaisedBrush", PaletteEntryKind.Brush, "Raised panels, tooltips and dialogs"),
        new("BorderBrush", PaletteEntryKind.Brush, "Panel edges"),

        new("TextBrush", PaletteEntryKind.Brush, "Ordinary text"),
        new("MutedTextBrush", PaletteEntryKind.Brush, "Secondary text and inline help"),

        new("AccentBrush", PaletteEntryKind.Brush, "Accent: links, chips, the default button's fill"),
        new("WarnBrush", PaletteEntryKind.Brush, "Warnings"),
        new("DangerBrush", PaletteEntryKind.Brush, "Destructive actions, DELETE ALL, the POP chip"),

        new("AccentHoverBrush", PaletteEntryKind.Brush, "Accent fill under the pointer"),
        new("AccentPressedBrush", PaletteEntryKind.Brush, "Accent fill while pressed"),
        new("AccentTextBrush", PaletteEntryKind.Brush, "Text on an accent fill"),

        new("ControlBackgroundBrush", PaletteEntryKind.Brush, "Text boxes, combo boxes, grid headers"),
        new("ControlBorderBrush", PaletteEntryKind.Brush, "Control outlines"),
        new("ControlHoverBrush", PaletteEntryKind.Brush, "Control under the pointer"),
        new("ControlPressedBrush", PaletteEntryKind.Brush, "Control while pressed"),
        new("ControlDisabledTextBrush", PaletteEntryKind.Brush, "Text in a disabled control"),

        new("SelectionBrush", PaletteEntryKind.Gradient, "Selected row fill - one colour, or two for a gradient"),
        new("SelectionBorderBrush", PaletteEntryKind.Brush, "Selected row outline"),
        new("SelectionTextBrush", PaletteEntryKind.Brush, "Text in a selected row"),
        new("HoverBorderBrush", PaletteEntryKind.Brush, "Row under the pointer"),
        new("ModifiedRowBrush", PaletteEntryKind.Brush, "Settings row whose value differs from its default"),

        new("ScrollThumbBrush", PaletteEntryKind.Brush, "Scroll bar thumb"),
        new("ScrollThumbHoverBrush", PaletteEntryKind.Brush, "Scroll bar thumb under the pointer"),
        new("SplitterLineBrush", PaletteEntryKind.Brush, "The line between the list and the preview pane"),

        new("ShadowColor", PaletteEntryKind.Color, "Overlay and toast drop shadow"),
    ];

    private static readonly Dictionary<string, PaletteKey> ByName =
        All.ToDictionary(static key => key.Name, StringComparer.Ordinal);

    /// <summary>Every key's name, for a quick membership test or a message listing them.</summary>
    public static IReadOnlyCollection<string> Names => ByName.Keys;

    /// <summary>
    /// The key of that exact name, or null. <strong>Case-sensitive</strong>, deliberately: these are WPF resource
    /// keys, and WPF's own lookup is case-sensitive - accepting <c>surfacebrush</c> here would produce a theme
    /// that validated and then did nothing.
    /// </summary>
    public static PaletteKey? Find(string name)
        => name is not null && ByName.TryGetValue(name, out var key) ? key : null;
}
