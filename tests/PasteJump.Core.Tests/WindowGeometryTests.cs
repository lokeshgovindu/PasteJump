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
}
