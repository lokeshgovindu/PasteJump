namespace PasteJump.Core.PasteMode;

/// <summary>How the overlay sits against its anchor point.</summary>
public enum OverlayPlacement
{
    /// <summary>
    /// Just below and to the right of the point. The point is a caret or a mouse pointer - something small that
    /// the overlay must not cover.
    /// </summary>
    BelowPoint,

    /// <summary>
    /// Centred on the point. The point is the middle of a whole window, so sitting below it would put the overlay
    /// in the lower half of that window rather than in the middle of it.
    /// </summary>
    CentredOn,
}

/// <summary>Where to put the overlay, in physical screen pixels, and how to sit against that point.</summary>
public readonly record struct OverlayAnchor(int X, int Y, OverlayPlacement Placement);

/// <summary>
/// Decides where the overlay goes from the three things Windows can tell us: the caret, the window being pasted
/// into, and the mouse.
/// </summary>
/// <remarks>
/// In Core and pure because the preference order is the whole of it, and getting that order wrong is invisible
/// until somebody reports it from an application nobody tested. Which is what happened: the mouse used to be the
/// only fallback, and <b>Edge exposes no Win32 caret at all</b> - measured 2026-08-19 with a focused, blinking
/// textarea, <c>GetGUIThreadInfo</c> returns <c>hwndCaret == 0</c>. So every gesture in a browser anchored on the
/// pointer, and with Edge maximised on the second monitor and the pointer left on the first the overlay rendered
/// perfectly ~1,900px away from the window being pasted into. Reported as "I cannot see the paste overlay in
/// Edge"; it was never hidden, only elsewhere.
/// <para>
/// It is not an Edge defect and not Edge-specific. A Win32 caret exists only in edit and richedit controls -
/// verified against the Run dialog, which reports an <c>Edit</c> window and a 1x15 caret rect - while anything
/// that draws its own caret reports none: Edge and every Chromium browser, Electron, WPF, WinUI and Visual
/// Studio all measured at <c>hwndCaret == 0</c>. Browsers merely make it obvious, because browsing is
/// mouse-driven and the pointer ends up wherever you last clicked.
/// </para>
/// </remarks>
public static class OverlayAnchorChooser
{
    /// <summary>
    /// The anchor to use, preferring the caret, then the window being pasted into, and only then the mouse.
    /// </summary>
    /// <param name="caret">The caret's screen position, or null when the focused control exposes none.</param>
    /// <param name="foregroundWindow">
    /// Screen rectangle of the window being pasted into, or null when there is no usable one - no foreground
    /// window at all, or a minimised one, which Windows parks off-screen at -32000.
    /// </param>
    /// <param name="cursor">The mouse position, which is always available and is therefore the last resort.</param>
    /// <remarks>
    /// The window beats the mouse rather than the other way round because it is the one thing that cannot be
    /// wrong: the overlay describes what releasing Ctrl will paste <i>into that window</i>, so drawing it over
    /// that window is always on the monitor the user is looking at. The mouse can be anywhere - on another
    /// monitor, over the taskbar, or parked where a click happened minutes ago.
    /// </remarks>
    public static OverlayAnchor Choose(
        (int X, int Y)? caret,
        (int Left, int Top, int Right, int Bottom)? foregroundWindow,
        (int X, int Y) cursor)
    {
        if (caret is { } point)
        {
            return new OverlayAnchor(point.X, point.Y, OverlayPlacement.BelowPoint);
        }

        // A rectangle with no area is not a window to centre on. It is reachable: a window mid-creation, and
        // anything that has told Windows it occupies nothing.
        if (foregroundWindow is { } window && window.Right > window.Left && window.Bottom > window.Top)
        {
            return new OverlayAnchor(
                window.Left + ((window.Right - window.Left) / 2),
                window.Top + ((window.Bottom - window.Top) / 2),
                OverlayPlacement.CentredOn);
        }

        return new OverlayAnchor(cursor.X, cursor.Y, OverlayPlacement.BelowPoint);
    }
}
