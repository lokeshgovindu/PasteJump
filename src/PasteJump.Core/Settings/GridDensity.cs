namespace PasteJump.Core.Settings;

/// <summary>
/// Row spacing for list views, following the Outlook / Explorer convention of three named levels
/// rather than exposing a raw pixel height.
/// <para>
/// Named levels are deliberate: a pixel box invites values that break the layout, and row height alone
/// does not make a list feel tighter - the cell padding has to move with it, which a single number
/// cannot express.
/// </para>
/// </summary>
public enum GridDensity
{
    /// <summary>Most generous spacing.</summary>
    Roomy = 0,

    /// <summary>The default. Noticeably tighter than Roomy while still comfortable.</summary>
    Cozy = 1,

    /// <summary>Tightest, for fitting the most rows on screen.</summary>
    Compact = 2,
}

/// <summary>Pixel metrics for each <see cref="GridDensity"/>, resolved in one place.</summary>
public static class GridDensityMetrics
{
    /// <summary>Row height in device-independent pixels.</summary>
    public static double RowHeight(GridDensity density) => density switch
    {
        GridDensity.Roomy => 30,
        GridDensity.Compact => 21,
        _ => 25,
    };

    /// <summary>
    /// Horizontal cell padding. Kept constant across levels: density is about vertical rhythm, and
    /// squeezing the horizontal padding as well makes text collide with the column edges.
    /// </summary>
    public static double CellPaddingX => 10;

    /// <summary>Vertical cell padding, which has to shrink with the row or the text clips.</summary>
    public static double CellPaddingY(GridDensity density) => density switch
    {
        GridDensity.Roomy => 4,
        GridDensity.Compact => 1,
        _ => 2,
    };
}
