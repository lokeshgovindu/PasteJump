using PasteJump.Core.Imaging;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Frame selection is what decides whether the tray icon is sharp, so it is asserted rather than eyeballed.
/// <para>
/// The icons are built here byte by byte instead of being read from <c>Assets</c>: Core.Tests does not
/// reference the WPF project, and a test that needs a real file could not cover a malformed one at all.
/// </para>
/// </summary>
public class IconFileTests
{
    /// <summary>
    /// Builds an <c>.ico</c> with the given frame sizes. The payloads are filler - nothing here decodes an
    /// image, and a real PNG would only make the expected offsets harder to reason about.
    /// </summary>
    private static byte[] BuildIcon(params (int Size, int BitCount)[] frames)
    {
        var header = 6 + (frames.Length * 16);
        var payload = 64;
        var bytes = new byte[header + (frames.Length * payload)];

        bytes[2] = 1;                                   // idType: icon
        bytes[4] = (byte)frames.Length;

        for (var i = 0; i < frames.Length; i++)
        {
            var entry = 6 + (i * 16);
            var offset = header + (i * payload);

            // 256 is stored as 0, which is the one part of this format that catches people out.
            bytes[entry] = (byte)(frames[i].Size == 256 ? 0 : frames[i].Size);
            bytes[entry + 1] = bytes[entry];
            bytes[entry + 6] = (byte)frames[i].BitCount;
            BitConverter.GetBytes(payload).CopyTo(bytes, entry + 8);
            BitConverter.GetBytes(offset).CopyTo(bytes, entry + 12);
        }

        return bytes;
    }

    [Fact]
    public void Every_frame_in_the_table_is_read()
    {
        var frames = IconFile.ReadFrames(BuildIcon((16, 32), (24, 32), (32, 32)));

        Assert.Equal([16, 24, 32], frames.Select(f => f.Width));
        Assert.All(frames, f => Assert.Equal(64, f.Length));
    }

    [Fact]
    public void A_stored_width_of_zero_means_256()
    {
        var frames = IconFile.ReadFrames(BuildIcon((256, 32)));

        Assert.Equal(256, Assert.Single(frames).Width);
    }

    [Fact]
    public void An_exact_size_is_preferred()
    {
        var frames = IconFile.ReadFrames(BuildIcon((16, 32), (24, 32), (32, 32)));

        Assert.Equal(24, IconFile.SelectFrame(frames, 24)!.Value.Width);
    }

    /// <summary>
    /// The rule that matters: shrinking keeps detail, enlarging invents it. A 20 px request with 16 and 24
    /// available must take the 24.
    /// </summary>
    [Fact]
    public void A_larger_frame_beats_a_smaller_one_when_there_is_no_exact_match()
    {
        var frames = IconFile.ReadFrames(BuildIcon((16, 32), (24, 32), (64, 32)));

        Assert.Equal(24, IconFile.SelectFrame(frames, 20)!.Value.Width);
    }

    [Fact]
    public void The_nearest_larger_frame_wins_rather_than_the_largest()
    {
        var frames = IconFile.ReadFrames(BuildIcon((16, 32), (32, 32), (256, 32)));

        Assert.Equal(32, IconFile.SelectFrame(frames, 24)!.Value.Width);
    }

    [Fact]
    public void The_largest_smaller_frame_is_used_when_nothing_is_big_enough()
    {
        var frames = IconFile.ReadFrames(BuildIcon((16, 32), (24, 32)));

        Assert.Equal(24, IconFile.SelectFrame(frames, 48)!.Value.Width);
    }

    [Fact]
    public void Depth_breaks_a_tie_between_two_frames_of_the_same_size()
    {
        var frames = IconFile.ReadFrames(BuildIcon((24, 8), (24, 32)));

        Assert.Equal(32, IconFile.SelectFrame(frames, 24)!.Value.BitCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_size_that_is_not_a_size_selects_nothing(int size)
    {
        var frames = IconFile.ReadFrames(BuildIcon((16, 32)));

        Assert.Null(IconFile.SelectFrame(frames, size));
    }

    [Fact]
    public void Nothing_is_read_from_an_empty_span()
    {
        Assert.Empty(IconFile.ReadFrames([]));
    }

    /// <summary>
    /// A cursor is byte-for-byte an icon but for its type field, and its frames carry a hotspot where an
    /// icon's carry a colour count - so reading one as an icon would half-work, which is worse than refusing.
    /// </summary>
    [Fact]
    public void A_cursor_is_not_an_icon()
    {
        var bytes = BuildIcon((32, 32));
        bytes[2] = 2;

        Assert.Empty(IconFile.ReadFrames(bytes));
    }

    [Fact]
    public void A_file_claiming_frames_it_does_not_have_is_refused()
    {
        var bytes = BuildIcon((16, 32));
        bytes[4] = 9;

        Assert.Empty(IconFile.ReadFrames(bytes));
    }

    /// <summary>
    /// The one that would be a buffer overrun rather than a wrong picture: a frame whose offset and length
    /// run past the end of the file is dropped, not clamped.
    /// </summary>
    [Fact]
    public void A_frame_pointing_outside_the_file_is_dropped()
    {
        var bytes = BuildIcon((16, 32), (32, 32));
        BitConverter.GetBytes(bytes.Length + 500).CopyTo(bytes, 6 + 16 + 12);

        var frames = IconFile.ReadFrames(bytes);

        Assert.Equal(16, Assert.Single(frames).Width);
    }

    [Fact]
    public void A_frame_of_no_length_is_dropped()
    {
        var bytes = BuildIcon((16, 32));
        BitConverter.GetBytes(0).CopyTo(bytes, 6 + 8);

        Assert.Empty(IconFile.ReadFrames(bytes));
    }

    /// <summary>
    /// The real files, described: nine frames from 16 to 256, so the sizes the shell asks for are all exact
    /// matches. Written as a test of the *selection* rather than of the artwork, which is why it builds an
    /// icon with those sizes rather than opening one.
    /// </summary>
    [Theory]
    [InlineData(16, 16)]
    [InlineData(20, 20)]
    [InlineData(24, 24)]
    [InlineData(32, 32)]
    [InlineData(48, 48)]
    public void Every_size_the_shell_asks_for_is_an_exact_frame_in_our_own_icons(int requested, int expected)
    {
        var ours = BuildIcon((16, 32), (20, 32), (24, 32), (32, 32), (40, 32), (48, 32), (64, 32), (128, 32), (256, 32));

        Assert.Equal(expected, IconFile.SelectFrame(IconFile.ReadFrames(ours), requested)!.Value.Width);
    }
}
