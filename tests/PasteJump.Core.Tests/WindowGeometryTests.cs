using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Fitting a remembered window size to the screen it is about to open on.
/// </summary>
/// <remarks>
/// The history window remembers its size now, which introduces a way to be unusable that did not exist while the
/// size was a constant: a size saved on a large monitor, restored on a smaller one, opens a window whose resize grip
/// and buttons are off the screen. These are the cases that rule out.
/// </remarks>
public class WindowGeometryTests
{
    [Fact]
    public void A_size_that_fits_is_left_alone()
    {
        var (width, height) = WindowGeometry.FitTo(1260, 770, 1920, 1040, 680, 400);

        Assert.Equal(1260, width);
        Assert.Equal(770, height);
    }

    [Fact]
    public void A_size_larger_than_the_work_area_is_brought_down_to_it()
    {
        // Saved on a 4K monitor, opened on a laptop.
        var (width, height) = WindowGeometry.FitTo(3400, 2000, 1366, 728, 680, 400);

        Assert.Equal(1366, width);
        Assert.Equal(728, height);
    }

    [Fact]
    public void The_windows_own_minimum_wins_over_a_smaller_work_area()
    {
        // Honouring a work area below MinHeight would hand WPF a height it ignores, so the rule would only look
        // as though it had been applied.
        var (width, height) = WindowGeometry.FitTo(1260, 770, 500, 300, 680, 400);

        Assert.Equal(680, width);
        Assert.Equal(400, height);
    }

    [Fact]
    public void A_size_smaller_than_the_minimum_is_raised_to_it()
    {
        var (width, height) = WindowGeometry.FitTo(200, 100, 1920, 1040, 680, 400);

        Assert.Equal(680, width);
        Assert.Equal(400, height);
    }

    [Fact]
    public void An_unusable_work_area_does_not_collapse_the_window()
    {
        // What a disconnected or not-yet-ready monitor reports. Keeping the wanted size is the only sane answer:
        // clamping to zero would open a window with nothing in it.
        var (width, height) = WindowGeometry.FitTo(1260, 770, 0, 0, 680, 400);

        Assert.Equal(1260, width);
        Assert.Equal(770, height);
    }
    [Fact]
    public void A_list_width_that_fits_is_left_alone()
    {
        // The default split: 552 of list in a 1236px content area, which is what was asked for.
        Assert.Equal(552, WindowGeometry.FitPane(552, 1236, 300, 240, 6));
    }

    [Fact]
    public void A_list_width_remembered_from_a_wider_window_is_brought_in()
    {
        // Left at 900 on a maximised window, then opened at 1000: honouring it would leave the preview 94px, so
        // the user would find a pane they cannot use and nothing to explain why.
        Assert.Equal(1000 - 6 - 240, WindowGeometry.FitPane(900, 1000, 300, 240, 6));
    }

    [Fact]
    public void The_lists_own_minimum_wins_when_even_that_will_not_fit()
    {
        Assert.Equal(300, WindowGeometry.FitPane(552, 400, 300, 240, 6));
    }

    [Fact]
    public void A_window_with_no_width_yet_leaves_the_remembered_split_alone()
    {
        // ActualWidth is zero until the window has laid out. Fitting against that would collapse the list to its
        // minimum on every opening, which is exactly the bug this guards.
        Assert.Equal(552, WindowGeometry.FitPane(552, 0, 300, 240, 6));
    }

}
