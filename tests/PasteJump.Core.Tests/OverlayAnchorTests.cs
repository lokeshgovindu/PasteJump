using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Guards the order the overlay's position is chosen in. The order is the whole of this logic, and getting it
/// wrong is silent: the overlay renders perfectly, on the wrong monitor.
/// </summary>
public class OverlayAnchorTests
{
    // ------------------------------------------------------------- the OverlayPosition setting

    private static readonly (int X, int Y) Caret = (400, 300);
    private static readonly (int Left, int Top, int Right, int Bottom) Window = (0, 0, 1920, 1080);
    private static readonly (int X, int Y) Mouse = (1500, 900);

    [Fact]
    public void Automatic_prefers_the_caret_then_the_window()
    {
        Assert.Equal(
            new OverlayAnchor(400, 300, OverlayPlacement.BelowPoint),
            OverlayAnchorChooser.Choose(Caret, Window, Mouse, false, PopupPosition.Automatic));

        Assert.Equal(
            new OverlayAnchor(960, 540, OverlayPlacement.CentredOn),
            OverlayAnchorChooser.Choose(null, Window, Mouse, false, PopupPosition.Automatic));
    }

    /// <summary>PasteJump's behaviour before 2026-08-19, kept as a choice rather than deleted.</summary>
    [Fact]
    public void CaretOrMouse_prefers_the_caret_then_the_pointer()
    {
        Assert.Equal(
            new OverlayAnchor(400, 300, OverlayPlacement.BelowPoint),
            OverlayAnchorChooser.Choose(Caret, Window, Mouse, false, PopupPosition.CaretOrMouse));

        Assert.Equal(
            new OverlayAnchor(1500, 900, OverlayPlacement.BelowPoint),
            OverlayAnchorChooser.Choose(null, Window, Mouse, false, PopupPosition.CaretOrMouse));
    }

    /// <summary>The one option that overrides the caret as well as the fallback.</summary>
    [Fact]
    public void MousePointer_ignores_the_caret_entirely()
    {
        Assert.Equal(
            new OverlayAnchor(1500, 900, OverlayPlacement.BelowPoint),
            OverlayAnchorChooser.Choose(Caret, Window, Mouse, false, PopupPosition.MousePointer));
    }

    [Fact]
    public void WindowCentre_ignores_the_caret_and_centres()
    {
        Assert.Equal(
            new OverlayAnchor(960, 540, OverlayPlacement.CentredOn),
            OverlayAnchorChooser.Choose(Caret, Window, Mouse, false, PopupPosition.WindowCentre));
    }

    [Fact]
    public void FixedPoint_wins_over_everything_including_a_caret()
    {
        Assert.Equal(
            new OverlayAnchor(50, 60, OverlayPlacement.BelowPoint),
            OverlayAnchorChooser.Choose(Caret, Window, Mouse, false, PopupPosition.FixedPoint, (50, 60)));
    }

    /// <summary>
    /// Half a fixed position is a mistake, not an instruction. Degrades to Automatic rather than pinning the
    /// overlay to the corner of the primary monitor, which is not a useful guess at what was meant.
    /// </summary>
    [Fact]
    public void FixedPoint_with_no_coordinates_degrades_to_automatic()
    {
        Assert.Equal(
            new OverlayAnchor(400, 300, OverlayPlacement.BelowPoint),
            OverlayAnchorChooser.Choose(Caret, Window, Mouse, false, PopupPosition.FixedPoint));
    }

    /// <summary>
    /// Not negotiable by preference, because it is not a preference: no position on top of a window Windows draws
    /// above ours can be seen, so every mode steps aside from one. Only a pinned position overrides it, since that
    /// is somebody stating exactly where they want it.
    /// <para>
    /// Caretless, which is the real situation - the Start menu exposes none. A caret deliberately still wins for
    /// the two caret modes, because an ordinary always-on-top window can be drawn over and the caret is a better
    /// signal than any fallback; <c>A_caret_still_wins_over_a_topmost_window</c> guards that.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(PopupPosition.Automatic)]
    [InlineData(PopupPosition.CaretOrMouse)]
    [InlineData(PopupPosition.MousePointer)]
    [InlineData(PopupPosition.WindowCentre)]
    public void Every_mode_steps_aside_from_a_topmost_window(PopupPosition preference)
    {
        var anchor = OverlayAnchorChooser.Choose(null, Window, Mouse, true, preference);

        Assert.Equal(OverlayPlacement.OutsideWindow, anchor.Placement);
        Assert.NotNull(anchor.Avoid);
    }

    [Fact]
    public void A_pinned_position_is_honoured_even_over_a_topmost_window()
    {
        var anchor = OverlayAnchorChooser.Choose(Caret, Window, Mouse, true, PopupPosition.FixedPoint, (7, 9));

        Assert.Equal(new OverlayAnchor(7, 9, OverlayPlacement.BelowPoint), anchor);
    }

    /// <summary>
    /// The corner is honoured like a pinned position, and carries only a monitor hint - the window being worked
    /// in, so the corner is on the screen the user is looking at rather than always the primary display.
    /// </summary>
    [Fact]
    public void BottomRight_returns_a_corner_placement_hinted_at_the_right_monitor()
    {
        var onSecondMonitor = (1920, 0, 3840, 1080);

        var anchor = OverlayAnchorChooser.Choose(Caret, onSecondMonitor, Mouse, false, PopupPosition.BottomRight);

        Assert.Equal(OverlayPlacement.WorkAreaBottomRight, anchor.Placement);
        Assert.InRange(anchor.X, 1920, 3840);
    }

    [Fact]
    public void BottomRight_falls_back_to_the_pointer_as_its_monitor_hint()
    {
        var anchor = OverlayAnchorChooser.Choose(null, null, Mouse, false, PopupPosition.BottomRight);

        Assert.Equal(OverlayPlacement.WorkAreaBottomRight, anchor.Placement);
        Assert.Equal(Mouse.X, anchor.X);
    }

    /// <summary>
    /// The copy notification keeps the mouse as its default, unlike the overlay. A copy is often made with the
    /// mouse - select, then Ctrl+C - so the pointer genuinely is where the user was looking, and that documented
    /// decision is not reversed by adding the choice.
    /// </summary>
    [Fact]
    public void The_copy_notification_still_defaults_to_the_pointer()
    {
        var settings = new PasteJumpSettings();

        Assert.Equal(PopupPosition.MousePointer, settings.CopyNotificationPosition);
        Assert.Equal(PopupPosition.Automatic, settings.OverlayPosition);
    }

    /// <summary>Automatic is the zero value, so a file written before this setting existed reads as it behaved.</summary>
    [Fact]
    public void Automatic_is_the_default_and_the_zero_value()
    {
        Assert.Equal(PopupPosition.Automatic, default(PopupPosition));
        Assert.Equal(PopupPosition.Automatic, new PasteJumpSettings().OverlayPosition);
    }

    /// <summary>Nothing may fall through to an unplaced overlay, whatever the mode and however little is known.</summary>
    [Theory]
    [InlineData(PopupPosition.Automatic)]
    [InlineData(PopupPosition.CaretOrMouse)]
    [InlineData(PopupPosition.MousePointer)]
    [InlineData(PopupPosition.WindowCentre)]
    [InlineData(PopupPosition.FixedPoint)]
    public void Every_mode_still_places_the_overlay_when_nothing_is_known(PopupPosition preference)
    {
        var anchor = OverlayAnchorChooser.Choose(null, null, Mouse, false, preference);

        Assert.Equal(new OverlayAnchor(1500, 900, OverlayPlacement.BelowPoint), anchor);
    }

    [Fact]
    public void A_caret_wins_over_the_window_and_the_mouse()
    {
        var anchor = OverlayAnchorChooser.Choose(
            caret: (400, 300),
            foregroundWindow: (0, 0, 1920, 1080),
            cursor: (1500, 900));

        Assert.Equal(new OverlayAnchor(400, 300, OverlayPlacement.BelowPoint), anchor);
    }

    [Fact]
    public void With_no_caret_the_overlay_is_centred_on_the_window_being_pasted_into()
    {
        var anchor = OverlayAnchorChooser.Choose(
            caret: null,
            foregroundWindow: (100, 200, 900, 800),
            cursor: (1500, 900));

        Assert.Equal(new OverlayAnchor(500, 500, OverlayPlacement.CentredOn), anchor);
    }

    /// <summary>
    /// The reported bug, in the shape it was reported. Edge exposes no Win32 caret, so before this the anchor was
    /// the mouse - and with Edge on the second monitor and the pointer left on the first, the overlay rendered
    /// 1,900px away from the window being pasted into. Measured on the real thing 2026-08-19: Edge at
    /// (1916,-4)-(3844,1036), pointer at (58,996), overlay drawn at (62,575).
    /// </summary>
    [Fact]
    public void The_mouse_on_another_monitor_no_longer_drags_the_overlay_off_the_window()
    {
        var anchor = OverlayAnchorChooser.Choose(
            caret: null,
            foregroundWindow: (1916, -4, 3844, 1036),
            cursor: (58, 996));

        Assert.Equal(OverlayPlacement.CentredOn, anchor.Placement);

        // Inside the window, which is the property that matters - not the exact midpoint.
        Assert.InRange(anchor.X, 1916, 3844);
        Assert.InRange(anchor.Y, -4, 1036);
    }

    [Fact]
    public void With_no_caret_and_no_window_the_mouse_is_still_the_last_resort()
    {
        var anchor = OverlayAnchorChooser.Choose(
            caret: null,
            foregroundWindow: null,
            cursor: (1500, 900));

        Assert.Equal(new OverlayAnchor(1500, 900, OverlayPlacement.BelowPoint), anchor);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]        // no foreground window has ever reported this, but a rectangle with no area
    [InlineData(50, 50, 50, 200)]   // zero width
    [InlineData(50, 50, 200, 50)]   // zero height
    [InlineData(200, 200, 50, 50)]  // inverted, which is not a window at all
    public void A_window_with_no_area_is_not_something_to_centre_on(int left, int top, int right, int bottom)
    {
        var anchor = OverlayAnchorChooser.Choose(
            caret: null,
            foregroundWindow: (left, top, right, bottom),
            cursor: (1500, 900));

        Assert.Equal(new OverlayAnchor(1500, 900, OverlayPlacement.BelowPoint), anchor);
    }

    /// <summary>
    /// A caret that could not be converted to screen coordinates arrives here as null, not as (0,0). Interop
    /// checks <c>ClientToScreen</c>'s return value for exactly this reason - Edge reports a caret window that has
    /// already been destroyed - and the top-left corner of the primary monitor would be a worse answer than the
    /// window the user is typing into.
    /// </summary>
    [Fact]
    public void A_caret_that_could_not_be_placed_falls_through_to_the_window()
    {
        var anchor = OverlayAnchorChooser.Choose(
            caret: null,
            foregroundWindow: (0, 0, 1920, 1080),
            cursor: (1500, 900));

        Assert.Equal(new OverlayAnchor(960, 540, OverlayPlacement.CentredOn), anchor);
    }
}
