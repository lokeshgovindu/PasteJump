namespace PasteJump.Core.Settings;

/// <summary>
/// A remembered window size, fitted to the screen it is about to open on.
/// </summary>
/// <remarks>
/// In Core and pure because the interesting half is arithmetic with a rule in it, and the rule is what would
/// otherwise bite: a size remembered on a 3840x2160 monitor must not open a window taller than a laptop's screen,
/// where the resize grip and often the buttons would be off the bottom with no way to reach them.
/// </remarks>
public static class WindowGeometry
{
    /// <summary>
    /// The size to open at: the remembered one, never smaller than the window's own minimum and never larger than
    /// the work area it has to fit into.
    /// </summary>
    /// <param name="width">Remembered width, in device-independent pixels.</param>
    /// <param name="height">Remembered height.</param>
    /// <param name="workWidth">Width of the work area - the screen minus the taskbar.</param>
    /// <param name="workHeight">Height of the work area.</param>
    /// <param name="minWidth">The window's own minimum, which wins over a work area smaller than it.</param>
    /// <param name="minHeight">As <paramref name="minWidth"/>.</param>
    /// <remarks>
    /// The minimum wins deliberately. On a work area shorter than the window can be, honouring the work area would
    /// mean handing WPF a height below MinHeight, which it ignores anyway - so the choice is between a window
    /// slightly too tall and a rule that pretends to have been applied.
    /// </remarks>
    public static (double Width, double Height) FitTo(
        double width,
        double height,
        double workWidth,
        double workHeight,
        double minWidth,
        double minHeight)
        => (Fit(width, workWidth, minWidth), Fit(height, workHeight, minHeight));

    private static double Fit(double wanted, double available, double minimum)
    {
        // An unusable work area - which is what a disconnected monitor reports - leaves the wanted size alone
        // rather than collapsing the window to its minimum.
        if (available <= 0)
        {
            return Math.Max(wanted, minimum);
        }

        return Math.Max(Math.Min(wanted, available), minimum);
    }
}
