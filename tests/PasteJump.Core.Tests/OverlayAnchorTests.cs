using PasteJump.Core.PasteMode;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Guards the order the overlay's position is chosen in. The order is the whole of this logic, and getting it
/// wrong is silent: the overlay renders perfectly, on the wrong monitor.
/// </summary>
public class OverlayAnchorTests
{
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
