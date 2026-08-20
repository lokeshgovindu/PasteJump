using PasteJump.Core.PasteMode;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Guards placement beside a window the overlay must not cover. The failure this prevents is silent in the worst
/// way: the overlay renders perfectly, reports a sensible rectangle, and is invisible because something is drawn
/// above it.
/// </summary>
public class OverlayPlacementSolverTests
{
    /// <summary>The measured Start menu case: work area 1920x1032, Start menu (0,142)-(858,1032).</summary>
    private static readonly ScreenBox Work = new(0, 0, 1920, 1032);
    private static readonly ScreenBox StartMenu = new(0, 142, 858, 1032);

    [Fact]
    public void The_overlay_lands_clear_of_the_start_menu()
    {
        var (left, top) = OverlayPlacementSolver.Beside(Work, StartMenu, width: 512, height: 105);

        Assert.True(left >= StartMenu.Right, $"left {left} should be right of the Start menu at {StartMenu.Right}");
        Assert.InRange(left, Work.Left, Work.Right - 512);
        Assert.InRange(top, Work.Top, Work.Bottom - 105);
    }

    [Fact]
    public void It_never_overlaps_the_window_it_was_told_to_avoid()
    {
        foreach (var avoid in new[]
        {
            StartMenu,
            new ScreenBox(531, 142, 1389, 1032),   // a centre-aligned taskbar
            new ScreenBox(1062, 142, 1920, 1032),  // right-aligned
            new ScreenBox(0, 0, 1920, 300),        // a full-width banner across the top
            new ScreenBox(0, 800, 1920, 1032),     // and across the bottom
        })
        {
            var (left, top) = OverlayPlacementSolver.Beside(Work, avoid, width: 512, height: 105);

            var overlaps = left < avoid.Right && left + 512 > avoid.Left
                && top < avoid.Bottom && top + 105 > avoid.Top;

            Assert.False(overlaps, $"overlay at ({left},{top}) overlaps {avoid}");
        }
    }

    [Fact]
    public void It_stays_inside_the_work_area()
    {
        foreach (var avoid in new[]
        {
            StartMenu,
            new ScreenBox(1062, 142, 1920, 1032),
            new ScreenBox(0, 0, 400, 1032),
        })
        {
            var (left, top) = OverlayPlacementSolver.Beside(Work, avoid, width: 512, height: 105);

            Assert.InRange(left, Work.Left, Work.Right - 512);
            Assert.InRange(top, Work.Top, Work.Bottom - 105);
        }
    }

    /// <summary>
    /// The corner moves with the window, because the Start menu sits left, centre or right depending on the
    /// taskbar's alignment - one hard-coded corner would be wrong for two of the three.
    /// </summary>
    [Fact]
    public void It_goes_to_the_far_side_of_whichever_corner_the_window_occupies()
    {
        var (leftAligned, _) = OverlayPlacementSolver.Beside(Work, new ScreenBox(0, 142, 600, 1032), 512, 105);
        Assert.True(leftAligned >= 600, $"a left-aligned window pushes the overlay right, got {leftAligned}");

        var (rightAligned, _) = OverlayPlacementSolver.Beside(Work, new ScreenBox(1320, 142, 1920, 1032), 512, 105);
        Assert.True(rightAligned + 512 <= 1320, $"a right-aligned window pushes the overlay left, got {rightAligned}");

        var (_, topBanner) = OverlayPlacementSolver.Beside(Work, new ScreenBox(0, 0, 1920, 400), 512, 105);
        Assert.True(topBanner >= 400, $"a window across the top pushes the overlay down, got {topBanner}");
    }

    /// <summary>
    /// A window filling the work area is unwinnable - no position is both on screen and uncovered - so the answer
    /// has to be a predictable one. It must still be on screen, which is the part worth asserting.
    /// </summary>
    [Fact]
    public void A_window_filling_the_work_area_still_yields_an_on_screen_position()
    {
        var (left, top) = OverlayPlacementSolver.Beside(Work, Work, width: 512, height: 105);

        Assert.InRange(left, Work.Left, Work.Right - 512);
        Assert.InRange(top, Work.Top, Work.Bottom - 105);
    }

    /// <summary>
    /// The reason this is a corner rather than an offset from the window's edge. Windows reported the Start menu
    /// as 858px wide while it visibly reached x=1127, so a placement that trusted the rectangle left the overlay
    /// half-covered. The corner has to survive the rectangle being wrong by that much.
    /// </summary>
    [Fact]
    public void It_survives_the_avoided_rectangle_being_understated()
    {
        var reported = new ScreenBox(0, 142, 858, 1032);
        var actuallyVisible = new ScreenBox(0, 142, 1127, 1032);

        var (left, top) = OverlayPlacementSolver.Beside(Work, reported, width: 439, height: 105);

        var overlapsWhatIsReallyThere = left < actuallyVisible.Right && left + 439 > actuallyVisible.Left
            && top < actuallyVisible.Bottom && top + 105 > actuallyVisible.Top;

        Assert.False(overlapsWhatIsReallyThere, $"overlay at ({left},{top}) still lands on the real Start menu");
    }

    [Fact]
    public void An_overlay_wider_than_the_work_area_is_still_placed_on_screen()
    {
        var (left, top) = OverlayPlacementSolver.Beside(Work, StartMenu, width: 4000, height: 105);

        Assert.Equal(Work.Left, left, 1);
        Assert.InRange(top, Work.Top, Work.Bottom - 105);
    }

    [Fact]
    public void A_topmost_foreground_window_is_avoided_rather_than_centred_on()
    {
        var anchor = OverlayAnchorChooser.Choose(
            caret: null,
            foregroundWindow: (0, 142, 858, 1032),
            cursor: (60, 1000),
            foregroundIsTopmost: true);

        Assert.Equal(OverlayPlacement.OutsideWindow, anchor.Placement);
        Assert.Equal(new ScreenBox(0, 142, 858, 1032), anchor.Avoid);
    }

    /// <summary>
    /// An ordinary window is still centred on. Edge is the case that matters and it is not topmost - measured
    /// ex-style 0x00200100 against the Start menu's 0x00200008.
    /// </summary>
    [Fact]
    public void An_ordinary_foreground_window_is_still_centred_on()
    {
        var anchor = OverlayAnchorChooser.Choose(
            caret: null,
            foregroundWindow: (-4, -4, 1924, 1036),
            cursor: (60, 1000),
            foregroundIsTopmost: false);

        Assert.Equal(OverlayPlacement.CentredOn, anchor.Placement);
        Assert.Null(anchor.Avoid);
        Assert.Equal(960, anchor.X);
        Assert.Equal(516, anchor.Y);
    }

    /// <summary>A caret still wins, topmost or not - it is the position the user is actually looking at.</summary>
    [Fact]
    public void A_caret_still_wins_over_a_topmost_window()
    {
        var anchor = OverlayAnchorChooser.Choose(
            caret: (400, 300),
            foregroundWindow: (0, 142, 858, 1032),
            cursor: (60, 1000),
            foregroundIsTopmost: true);

        Assert.Equal(new OverlayAnchor(400, 300, OverlayPlacement.BelowPoint), anchor);
    }
}
