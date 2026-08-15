using System.Windows.Controls;
using System.Windows.Media;

namespace PasteJump.App.Services;

/// <summary>
/// Makes the icon that sits in a <see cref="MenuItem"/>'s <c>Icon</c> slot, for every menu in the application.
/// </summary>
/// <remarks>
/// <para>
/// One definition rather than one per menu. Everything here was settled once, by rendering and looking, and each
/// part of it was got wrong first - so a second copy of the recipe would be a second chance to lose one of them.
/// </para>
/// <para>
/// The codepoints themselves live with the menu that uses them (<see cref="TrayGlyph"/>, <see cref="RowGlyph"/>),
/// because choosing a glyph is a decision about that menu; how it is drawn is not.
/// </para>
/// </remarks>
internal static class MenuGlyph
{
    /// <summary>The icon font. Fluent first, MDL2 as the Windows 10 fallback.</summary>
    /// <remarks>
    /// <c>Segoe MDL2 Assets</c> has shipped since Windows 10, which is this application's floor;
    /// <c>Segoe Fluent Icons</c> is the Windows 11 successor and shares these codepoints, so it is named first.
    /// </remarks>
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>A <see cref="TextBlock"/> ready to be assigned to <see cref="MenuItem.Icon"/>.</summary>
    public static TextBlock Create(string glyph)
    {
        // No Foreground set on purpose: inheriting the item's means the glyph follows the theme and greys out with
        // a disabled row, both for free. Setting it here would need a DynamicResource per glyph and would still
        // miss the disabled case.
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = Font,

            // 16, not the 15 the tray menu shipped with for one day. Reported as poor quality, and the reason is
            // that an icon font is hinted for the sizes its designers used - 16, 20, 24 - so 15 lands between stems
            // and the rasteriser has to guess. Compared at 3x magnification across 14/15/16/18/20: 16 is the first
            // size where the gear's teeth and the keyboard's keys are distinct, and 18 is cleaner still but too
            // large beside 12px labels in a 26px row.
            FontSize = 16,
        };

        // Ideal, against the Display mode a menu sets for its text, and deliberately only here. Display snaps glyph
        // outlines to the pixel grid the way GDI did, which is what makes small TEXT crisp and what visibly distorts
        // an icon: the same comparison showed uneven stems and a blobby gear under Display at every size. Grayscale
        // rather than ClearType because subpixel antialiasing puts colour fringes on a monochrome glyph, which reads
        // as a rendering fault on the dark palettes.
        TextOptions.SetTextFormattingMode(icon, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(icon, TextRenderingMode.Grayscale);

        return icon;
    }
}

/// <summary>
/// Glyphs for the clipboard history window's row menu. Chosen the same way <see cref="TrayGlyph"/>'s were - four
/// candidates per item rendered at 16px and looked at - because a wrong codepoint is not a compile error, it is a
/// box or something faintly absurd.
/// </summary>
internal static class RowGlyph
{
    public const string Copy = "";        // two overlapping pages
    public const string Pin = "";         // a pin, outline
    public const string Unpin = "";       // the same pin, struck through - a real pair, so the direction reads
    public const string Delete = "";      // a waste basket
    public const string Filter = "";      // a funnel
    public const string ClearFilter = ""; // Clear: a plain X, which is what Windows puts on a cleared filter
}
