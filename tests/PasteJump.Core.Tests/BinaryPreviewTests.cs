using PasteJump.Core.Model;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// What a clip that is neither text, an image nor files is called in the list.
/// </summary>
/// <remarks>
/// Written from the report these exist for: a ShareX screenshot arrived as <c>Other</c>, 708 bytes, shown as
/// <c>[binary]</c>. The clip held <c>System.Drawing.Bitmap</c> at 484 bytes and nothing else of substance - a .NET
/// object where a bitmap should have been - and the row said none of that, so the only way to answer "what went
/// wrong?" was to open the database. The first case below is that clip, byte for byte.
/// </remarks>
public class BinaryPreviewTests
{
    private static ClipPayload Payload(uint id, string? name, int bytes) => new(id, name, new byte[bytes]);

    [Fact]
    public void The_reported_clip_names_the_format_that_was_actually_there()
    {
        // Exactly what the store held for it: the .NET bitmap object, then OLE's bookkeeping.
        ClipPayload[] payloads =
        [
            Payload(50198, "System.Drawing.Bitmap", 484),
            Payload(49171, "Ole Private Data", 216),
            Payload(49161, "DataObject", 8),
        ];

        Assert.Equal("[binary: System.Drawing.Bitmap]", BinaryPreview.Describe(payloads));
    }

    [Fact]
    public void The_largest_payload_wins_whatever_order_they_arrive_in()
    {
        // Enumeration order is the clipboard owner's business, not ours, so the answer must not depend on it.
        ClipPayload[] ascending =
        [
            Payload(49161, "DataObject", 8),
            Payload(49171, "Ole Private Data", 216),
            Payload(50198, "System.Drawing.Bitmap", 484),
        ];

        Assert.Equal("[binary: System.Drawing.Bitmap]", BinaryPreview.Describe(ascending));
    }

    [Fact]
    public void A_standard_format_has_no_registered_name_so_it_is_numbered()
    {
        // Still better than "[binary]", which is the whole point: a number can be looked up.
        Assert.Equal("[binary: format #6]", BinaryPreview.Describe([Payload(6, null, 900)]));
    }

    [Fact]
    public void A_blank_name_is_treated_as_no_name()
    {
        Assert.Equal("[binary: format #7]", BinaryPreview.Describe([Payload(7, "   ", 40)]));
    }

    [Fact]
    public void Nothing_to_name_falls_back_to_the_old_text()
    {
        Assert.Equal("[binary]", BinaryPreview.Describe(null));
        Assert.Equal("[binary]", BinaryPreview.Describe([]));
    }

    [Fact]
    public void A_long_name_is_cut_so_it_cannot_take_over_the_row()
    {
        var name = new string('x', BinaryPreview.MaxNameChars + 20);

        var described = BinaryPreview.Describe([Payload(50000, name, 10)]);

        Assert.Equal($"[binary: {new string('x', BinaryPreview.MaxNameChars)}]", described);
    }
}
