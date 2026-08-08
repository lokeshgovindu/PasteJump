using PasteJump.Core.Model;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Dropping duplicate encodings of the same image at capture.
/// <para>
/// Reported as one image showing 83 KB in Clipjump and 5 MB in PasteJump. Two separate things were behind
/// that. Clipjump's 83 KB is the size of the lossy JPEG <em>thumbnail</em> it generates, not its clip - the
/// clip file for that copy was 443 KB. But PasteJump really was storing three full-size copies of the same
/// pixels, because Windows publishes <c>CF_DIB</c>, <c>CF_DIBV5</c> and <c>System.Drawing.Bitmap</c> together
/// and they differ only in header size, so nothing downstream could collapse them.
/// </para>
/// </summary>
public sealed class RedundantImageFormatTests
{
    private const uint CfUnicodeText = 13;
    private const uint CfHtml = 49_431;

    /// <summary>
    /// Approximates a real capture: the same pixels three times, differing by exactly the header sizes
    /// observed on disk - <c>BITMAPV5HEADER</c> is 84 bytes larger than <c>BITMAPINFOHEADER</c>, and a BMP
    /// file adds a 14-byte <c>BITMAPFILEHEADER</c>.
    /// </summary>
    private static List<ClipPayload> ThreeCopiesOfOneImage(int pixelBytes = 400_000) =>
    [
        new(RedundantImageFormats.CfDib, null, new byte[40 + pixelBytes]),
        new(RedundantImageFormats.CfDibV5, null, new byte[124 + pixelBytes]),
        new(0xC100, "System.Drawing.Bitmap", new byte[54 + pixelBytes]),
    ];

    [Fact]
    public void The_three_copies_collapse_to_one()
    {
        var kept = RedundantImageFormats.Prune(ThreeCopiesOfOneImage());

        // CF_DIB survives, not CF_DIBV5. Windows synthesises either from the other so nothing is lost, but
        // WPF's BMP decoder is far better exercised against BITMAPINFOHEADER - keeping V5 instead was reported
        // as previews rendering with their right-hand portion wrong. Clipjump keeps the plain CF_DIB too.
        var only = Assert.Single(kept);
        Assert.Equal(RedundantImageFormats.CfDib, only.FormatId);
    }

    [Fact]
    public void The_kept_dib_is_the_one_history_will_render()
    {
        // RecordHistory picks its blob with FirstOrDefault(FormatId is 8 or 17), so whichever DIB survives
        // pruning is the one that becomes the preview. This test is the link between the two: change the
        // preference here and the preview changes with it.
        var kept = RedundantImageFormats.Prune(ThreeCopiesOfOneImage());

        var forHistory = kept.First(static p => p.FormatId is RedundantImageFormats.CfDib or RedundantImageFormats.CfDibV5);

        Assert.Equal(RedundantImageFormats.CfDib, forHistory.FormatId);
    }

    [Fact]
    public void Two_thirds_of_the_bytes_go_away()
    {
        var payloads = ThreeCopiesOfOneImage();

        var before = payloads.Sum(static p => (long)p.ByteLength);
        var after = RedundantImageFormats.Prune(payloads).Sum(static p => (long)p.ByteLength);

        Assert.True(after * 2 < before, $"expected roughly a third to survive, went from {before} to {after}");
    }

    [Fact]
    public void The_plain_dib_is_kept_when_there_is_no_v5()
    {
        // Which is what Clipjump keeps, and it must remain sufficient on its own.
        List<ClipPayload> payloads =
        [
            new(RedundantImageFormats.CfDib, null, new byte[40_000]),
            new(0xC100, "System.Drawing.Bitmap", new byte[40_014]),
        ];

        var only = Assert.Single(RedundantImageFormats.Prune(payloads));

        Assert.Equal(RedundantImageFormats.CfDib, only.FormatId);
    }

    [Fact]
    public void A_v5_only_clip_keeps_its_v5()
    {
        // Preferring CF_DIB must not mean requiring it. Some sources publish only the V5 form, and dropping it
        // for want of a plain DIB would leave the clip with no image at all.
        List<ClipPayload> payloads =
        [
            new(RedundantImageFormats.CfDibV5, null, new byte[40_124]),
            new(0xC100, "System.Drawing.Bitmap", new byte[40_054]),
        ];

        var only = Assert.Single(RedundantImageFormats.Prune(payloads));

        Assert.Equal(RedundantImageFormats.CfDibV5, only.FormatId);
    }

    [Fact]
    public void A_bitmap_only_clip_is_left_completely_alone()
    {
        // Nothing is dropped unless a DIB survives to represent the image. Without that guard this would
        // strip the clip of its only picture, and the user would paste nothing.
        List<ClipPayload> payloads = [new(0xC100, "System.Drawing.Bitmap", new byte[40_014])];

        var kept = RedundantImageFormats.Prune(payloads);

        Assert.Single(kept);
        Assert.Equal("System.Drawing.Bitmap", kept[0].FormatName);
    }

    [Fact]
    public void Text_and_html_beside_an_image_are_untouched()
    {
        // Copying a table from a browser publishes all of these at once. Only the duplicate pictures go.
        List<ClipPayload> payloads =
        [
            new(CfUnicodeText, null, new byte[100]),
            new(CfHtml, "HTML Format", new byte[400]),
            new(RedundantImageFormats.CfDib, null, new byte[40_040]),
            new(RedundantImageFormats.CfDibV5, null, new byte[40_124]),
        ];

        var kept = RedundantImageFormats.Prune(payloads);

        Assert.Equal(3, kept.Count);
        Assert.Contains(kept, p => p.FormatId == CfUnicodeText);
        Assert.Contains(kept, p => p.FormatId == CfHtml);
        Assert.Contains(kept, p => p.FormatId == RedundantImageFormats.CfDib);
        Assert.DoesNotContain(kept, p => p.FormatId == RedundantImageFormats.CfDibV5);
    }

    [Fact]
    public void A_text_only_clip_is_returned_unchanged_without_reallocating()
    {
        // The overwhelmingly common case. It must not pay for this feature.
        List<ClipPayload> payloads = [new(CfUnicodeText, null, new byte[100])];

        Assert.Same(payloads, RedundantImageFormats.Prune(payloads));
    }

    [Fact]
    public void A_clip_with_nothing_to_drop_is_returned_unchanged()
    {
        List<ClipPayload> payloads =
        [
            new(CfUnicodeText, null, new byte[100]),
            new(RedundantImageFormats.CfDibV5, null, new byte[40_124]),
        ];

        Assert.Same(payloads, RedundantImageFormats.Prune(payloads));
    }

    [Fact]
    public void The_redundant_name_match_is_case_insensitive()
    {
        // Registered format names come back from Windows verbatim, and nothing guarantees their casing.
        List<ClipPayload> payloads =
        [
            new(RedundantImageFormats.CfDib, null, new byte[40_040]),
            new(0xC100, "system.drawing.BITMAP", new byte[40_054]),
        ];

        Assert.Single(RedundantImageFormats.Prune(payloads));
    }

    [Fact]
    public void An_empty_list_is_handled()
        => Assert.Empty(RedundantImageFormats.Prune([]));

    [Fact]
    public void The_snapshot_total_reflects_only_what_was_kept()
    {
        // TotalBytes is what the history window reports, so this is the number the original complaint was
        // about. It must fall as a result of pruning, not merely the bytes on disk.
        var pruned = RedundantImageFormats.Prune(ThreeCopiesOfOneImage());

        var snapshot = new ClipboardSnapshot(pruned, null, ClipKind.Image, "devenv.exe");

        // The surviving CF_DIB, so a 40-byte BITMAPINFOHEADER plus the pixels - not the 124-byte V5 form.
        Assert.Equal(40 + 400_000, snapshot.TotalBytes);
    }
}
