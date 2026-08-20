namespace PasteJump.Core.PasteMode;

/// <summary>
/// Places the overlay clear of a window it must not sit on top of, in a corner of the work area.
/// </summary>
/// <remarks>
/// <para>
/// Exists because of one measured fact: <b>Windows draws the Start menu above ordinary topmost windows</b>, so
/// the overlay cannot be seen there however it is positioned within that window. Measured 2026-08-20 - the
/// overlay reported itself at (173,534)-(685,639) with the Start menu open and a z-order walk found nothing
/// above it overlapping, yet cropping a screenshot to exactly that rectangle showed the Start menu. The window
/// enumeration order is not the compositor's band order, so <c>GetWindow</c> cannot answer this and a screenshot
/// is the only honest witness.
/// </para>
/// <para>
/// The trigger is <c>WS_EX_TOPMOST</c> on the window being pasted into, not a list of shell process names. That
/// is the property which actually matters - if the window we would cover is itself topmost, we cannot rely on
/// being above it - and it needs no maintenance as Windows renames its shell surfaces. An always-on-top
/// application that we *could* have covered is therefore placed beside instead, which is a safe degradation:
/// still visible, still predictable, and it stops the overlay covering a window the user deliberately pinned.
/// </para>
/// </remarks>
public static class OverlayPlacementSolver
{
    /// <summary>Gap left between the overlay and the window it is avoiding.</summary>
    private const double Margin = 8;

    /// <summary>
    /// The overlay's top-left, in the same units as the arguments, chosen so the overlay does not overlap
    /// <paramref name="avoid"/> and stays inside <paramref name="work"/>.
    /// </summary>
    /// <param name="work">The work area of the monitor to stay within.</param>
    /// <param name="avoid">The window the overlay must not be drawn on top of.</param>
    /// <param name="width">The overlay's width.</param>
    /// <param name="height">The overlay's height.</param>
    /// <remarks>
    /// One of the four work-area corners: the least-covered, and among equally uncovered ones the furthest from
    /// the window being avoided. Corners rather than an offset from the window edge because the Start menu's
    /// reported rectangle is not its visible extent - see the note in the body.
    /// <para>
    /// A topmost window filling the whole work area is unwinnable - no position is both on screen and uncovered -
    /// and there the least-covered corner is simply the top-right. Predictable beats clever when every answer is
    /// wrong.
    /// </para>
    /// </remarks>
    public static (double Left, double Top) Beside(
        ScreenBox work,
        ScreenBox avoid,
        double width,
        double height)
    {
        // A CORNER, not a position computed from the avoided window's edges - and that is the whole lesson of
        // this method. Windows under-reports the Start menu's extent: measured 2026-08-20, GetWindowRect on the
        // foreground window said 858px wide while the panel visibly reached x=1127 in a screenshot of the same
        // moment. Placing the overlay 8px past the reported edge therefore left half of it hidden. A corner is
        // right even when the rectangle is 256px wrong, because it only has to be on the far side.
        var candidates = new (double Left, double Top)[]
        {
            (work.Right - width - Margin, work.Top + Margin),          // top-right
            (work.Left + Margin, work.Top + Margin),                   // top-left
            (work.Right - width - Margin, work.Bottom - height - Margin), // bottom-right
            (work.Left + Margin, work.Bottom - height - Margin),        // bottom-left
        };

        var avoidCentreX = (avoid.Left + avoid.Right) / 2;
        var avoidCentreY = (avoid.Top + avoid.Bottom) / 2;

        var bestLeft = candidates[0].Left;
        var bestTop = candidates[0].Top;
        var bestOverlap = double.MaxValue;
        var bestDistance = double.MinValue;

        foreach (var (left, top) in candidates)
        {
            var overlap = Overlap(new ScreenBox(left, top, left + width, top + height), avoid);

            var centreX = left + (width / 2);
            var centreY = top + (height / 2);
            var distance = ((centreX - avoidCentreX) * (centreX - avoidCentreX))
                + ((centreY - avoidCentreY) * (centreY - avoidCentreY));

            // Least covered wins; among equally uncovered corners the furthest one, so the overlay is not merely
            // clear of the window but obviously clear of it.
            if (overlap < bestOverlap || (overlap == bestOverlap && distance > bestDistance))
            {
                bestOverlap = overlap;
                bestDistance = distance;
                bestLeft = left;
                bestTop = top;
            }
        }

        return (
            Clamp(bestLeft, work.Left, work.Right - width),
            Clamp(bestTop, work.Top, work.Bottom - height));
    }

    private static double Overlap(ScreenBox a, ScreenBox b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);

        return width <= 0 || height <= 0 ? 0 : width * height;
    }

    private static double Clamp(double value, double min, double max)
        => max < min ? min : Math.Clamp(value, min, max);
}

/// <summary>A rectangle in screen coordinates, in whatever units the caller is working in.</summary>
public readonly record struct ScreenBox(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;
}
