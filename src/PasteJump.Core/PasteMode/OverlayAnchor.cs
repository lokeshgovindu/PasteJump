using PasteJump.Core.Settings;

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

    /// <summary>
    /// Beside <see cref="OverlayAnchor.Avoid"/>, never on top of it. For a window that is itself topmost, which
    /// we cannot rely on being able to draw above - the Start menu being the case that proved it.
    /// <see cref="OverlayPlacementSolver"/> picks the side.
    /// </summary>
    OutsideWindow,
}

/// <summary>Where to put the overlay, in physical screen pixels, and how to sit against that point.</summary>
/// <param name="Avoid">
/// Set only for <see cref="OverlayPlacement.OutsideWindow"/>: the window the overlay must stay clear of. The
/// point is that window's centre, so a caller that ignores this still puts the overlay somewhere sensible.
/// </param>
public readonly record struct OverlayAnchor(
    int X,
    int Y,
    OverlayPlacement Placement,
    ScreenBox? Avoid = null);

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
    /// <param name="foregroundIsTopmost">
    /// Whether the window being pasted into has <c>WS_EX_TOPMOST</c>. When it does, the overlay is placed beside
    /// it rather than on it: Windows draws the Start menu above ordinary topmost windows, so centring on such a
    /// window can leave the overlay perfectly rendered and completely invisible. See
    /// <see cref="OverlayPlacementSolver"/> for the measurement that established this.
    /// </param>
    /// <param name="preference">
    /// What the user asked for. <see cref="OverlayPosition.Automatic"/> is the caret-then-window behaviour the
    /// rest of this method describes; the others override the fallback, and one overrides the caret as well.
    /// </param>
    /// <param name="fixedPoint">
    /// The pinned position, for <see cref="OverlayPosition.FixedPoint"/>. Null when either coordinate is unset,
    /// which degrades to <see cref="OverlayPosition.Automatic"/> rather than anchoring to (0,0).
    /// </param>
    public static OverlayAnchor Choose(
        (int X, int Y)? caret,
        (int Left, int Top, int Right, int Bottom)? foregroundWindow,
        (int X, int Y) cursor,
        bool foregroundIsTopmost = false,
        OverlayPosition preference = OverlayPosition.Automatic,
        (int X, int Y)? fixedPoint = null)
    {
        if (preference == OverlayPosition.FixedPoint)
        {
            // Honoured absolutely, including over a window Windows draws above ours. Somebody who pins the overlay
            // to a spot has said where they want it, and second-guessing that leaves no way to say "there, always".
            if (fixedPoint is { } pinned)
            {
                return new OverlayAnchor(pinned.X, pinned.Y, OverlayPlacement.BelowPoint);
            }

            // Degraded to Automatic outright rather than falling through, which would land on the window centre
            // and quietly make "fixed position, unset" mean something different from every other unset setting.
            preference = OverlayPosition.Automatic;
        }

        var window = foregroundWindow is { } rect && rect.Right > rect.Left && rect.Bottom > rect.Top
            ? rect
            : ((int Left, int Top, int Right, int Bottom)?)null;

        // The caret is checked BEFORE the topmost rule, and that order matters. An always-on-top editor does have
        // a caret, and the overlay can normally be drawn above an ordinary topmost window - it is only the shell's
        // own surfaces that outrank us. Stepping aside there would move the overlay away from the one signal that
        // is better than any fallback. The Start menu, which is what the topmost rule exists for, exposes no caret
        // anyway, so nothing is lost.
        if (preference is (OverlayPosition.Automatic or OverlayPosition.CaretOrMouse) && caret is { } point)
        {
            return new OverlayAnchor(point.X, point.Y, OverlayPlacement.BelowPoint);
        }

        // Otherwise, applies whatever the preference: it is not a preference but the difference between being seen
        // and not. No position on top of the Start menu can be seen, so every remaining mode steps aside from one.
        if (foregroundIsTopmost && window is { } avoid)
        {
            return new OverlayAnchor(
                avoid.Left + ((avoid.Right - avoid.Left) / 2),
                avoid.Top + ((avoid.Bottom - avoid.Top) / 2),
                OverlayPlacement.OutsideWindow,
                new ScreenBox(avoid.Left, avoid.Top, avoid.Right, avoid.Bottom));
        }

        if (preference == OverlayPosition.MousePointer)
        {
            return new OverlayAnchor(cursor.X, cursor.Y, OverlayPlacement.BelowPoint);
        }

        if (preference == OverlayPosition.CaretOrMouse)
        {
            return new OverlayAnchor(cursor.X, cursor.Y, OverlayPlacement.BelowPoint);
        }

        // Reached by Automatic with no caret to use, and by WindowCentre always. A rectangle with no area was
        // already discarded above: a window mid-creation, or anything that has told Windows it occupies nothing.
        if (window is { } target)
        {
            return new OverlayAnchor(
                target.Left + ((target.Right - target.Left) / 2),
                target.Top + ((target.Bottom - target.Top) / 2),
                OverlayPlacement.CentredOn);
        }

        // Nothing else is known - no caret, no usable window. The pointer is always somewhere.
        return new OverlayAnchor(cursor.X, cursor.Y, OverlayPlacement.BelowPoint);
    }
}
