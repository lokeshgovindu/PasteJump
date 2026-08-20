using PasteJump.Core.Diagnostics;
using Xunit;

namespace PasteJump.Core.Tests;

public class KeyRepeatFilterTests
{
    private const int Ctrl = 0x11;
    private const int V = 0x56;
    private const int Shift = 0x10;

    [Fact]
    public void A_first_press_is_written_and_reports_no_repeats()
    {
        var filter = new KeyRepeatFilter();

        Assert.True(filter.ShouldWrite(Ctrl, isKeyDown: true, out var repeats));
        Assert.Equal(0, repeats);
    }

    [Fact]
    public void Auto_repeat_is_swallowed()
    {
        var filter = new KeyRepeatFilter();

        filter.ShouldWrite(Ctrl, isKeyDown: true, out _);

        for (var i = 0; i < 40; i++)
        {
            Assert.False(filter.ShouldWrite(Ctrl, isKeyDown: true, out var repeats));
            Assert.Equal(0, repeats);
        }
    }

    [Fact]
    public void The_release_carries_the_count_that_was_swallowed()
    {
        var filter = new KeyRepeatFilter();

        filter.ShouldWrite(Ctrl, isKeyDown: true, out _);

        for (var i = 0; i < 37; i++)
        {
            filter.ShouldWrite(Ctrl, isKeyDown: true, out _);
        }

        Assert.True(filter.ShouldWrite(Ctrl, isKeyDown: false, out var repeats));
        Assert.Equal(37, repeats);
    }

    /// <summary>
    /// The reason this tracks per key rather than keeping one "last key" - holding Ctrl while tapping the trigger
    /// is the entire gesture, and collapsing globally would swallow every trigger press after the first.
    /// </summary>
    [Fact]
    public void Keys_are_tracked_independently()
    {
        var filter = new KeyRepeatFilter();

        Assert.True(filter.ShouldWrite(Ctrl, isKeyDown: true, out _));
        filter.ShouldWrite(Ctrl, isKeyDown: true, out _);   // Ctrl repeating throughout

        Assert.True(filter.ShouldWrite(V, isKeyDown: true, out _));
        Assert.True(filter.ShouldWrite(V, isKeyDown: false, out _));

        filter.ShouldWrite(Ctrl, isKeyDown: true, out _);

        Assert.True(filter.ShouldWrite(V, isKeyDown: true, out _));
        Assert.True(filter.ShouldWrite(V, isKeyDown: false, out _));

        Assert.True(filter.ShouldWrite(Ctrl, isKeyDown: false, out var ctrlRepeats));
        // Two repeats, not three: the first Ctrl down is the press itself.
        Assert.Equal(2, ctrlRepeats);
    }

    [Fact]
    public void A_second_press_after_a_release_is_written_again()
    {
        var filter = new KeyRepeatFilter();

        filter.ShouldWrite(V, isKeyDown: true, out _);
        filter.ShouldWrite(V, isKeyDown: true, out _);
        filter.ShouldWrite(V, isKeyDown: false, out _);

        Assert.True(filter.ShouldWrite(V, isKeyDown: true, out var repeats));
        Assert.Equal(0, repeats);
    }

    /// <summary>
    /// A release whose press was never seen still gets a line. It happens for real: a key held across a hook
    /// reinstall, or one whose press another hook suppressed. Swallowing it would hide the release entirely.
    /// </summary>
    [Fact]
    public void A_release_with_no_press_is_still_written()
    {
        var filter = new KeyRepeatFilter();

        Assert.True(filter.ShouldWrite(Shift, isKeyDown: false, out var repeats));
        Assert.Equal(0, repeats);
    }

    /// <summary>
    /// After a reinstall nothing is believed to be down, so the next press is a press. Without this, a key held
    /// across the gap has its next press read as auto-repeat and swallowed - losing the one line that matters.
    /// </summary>
    [Fact]
    public void Reset_makes_the_next_press_a_first_press()
    {
        var filter = new KeyRepeatFilter();

        filter.ShouldWrite(Ctrl, isKeyDown: true, out _);
        filter.ShouldWrite(Ctrl, isKeyDown: true, out _);

        filter.Reset();

        Assert.True(filter.ShouldWrite(Ctrl, isKeyDown: true, out var repeats));
        Assert.Equal(0, repeats);
    }

    /// <summary>
    /// The hook reports a virtual key in 0-255, but nothing in this class may throw on the hook path whatever
    /// arrives - an out-of-range value is written rather than indexed with.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    [InlineData(int.MaxValue)]
    public void An_out_of_range_key_is_written_and_never_indexed(int virtualKey)
    {
        var filter = new KeyRepeatFilter();

        Assert.True(filter.ShouldWrite(virtualKey, isKeyDown: true, out _));
        Assert.True(filter.ShouldWrite(virtualKey, isKeyDown: true, out _));
        Assert.True(filter.ShouldWrite(virtualKey, isKeyDown: false, out _));
    }

    /// <summary>Every key in range behaves, not just the handful the gesture uses.</summary>
    [Fact]
    public void The_whole_keyboard_collapses_the_same_way()
    {
        var filter = new KeyRepeatFilter();

        for (var vk = 0; vk <= 255; vk++)
        {
            Assert.True(filter.ShouldWrite(vk, isKeyDown: true, out _));
            Assert.False(filter.ShouldWrite(vk, isKeyDown: true, out _));
            Assert.True(filter.ShouldWrite(vk, isKeyDown: false, out var repeats));
            Assert.Equal(1, repeats);
        }
    }
}
