using PasteJump.Core.Formatting;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Every overlay frame must name the clip it is showing, by id.
/// <para>
/// This exists because of a reported bug rather than as a matter of taste. <see cref="PasteOverlayModel"/> carried
/// no id, so the application resolved the clip from <c>Position</c> against the store's own order - and
/// <c>Position</c> counts the <em>filtered</em> window. Pressing <c>K</c> for images only made "clip 7" the 7th
/// image, stack position 31 in the reported case, while the host read stack position 7. Where that slot held text
/// the image preview vanished, which is how it was noticed; where it held a different image, the overlay quietly
/// showed the wrong picture.
/// </para>
/// <para>
/// So these tests assert the identity <em>and</em> the trap: that position genuinely disagrees with store order
/// once a filter or a search is on. Asserting only the id would pass on a build where the two happened to agree,
/// which is exactly the build that hid this for weeks.
/// </para>
/// </summary>
public class OverlayClipIdentityTests
{
    /// <summary>Ids 1..6 oldest first, so the newest-first window is 6, 5, 4, 3, 2, 1.</summary>
    private static (PasteModeController Controller, FakeClipCatalog Catalog, RecordingPasteModeHost Host) Build()
    {
        var catalog = new FakeClipCatalog();

        catalog.Add("text one");
        catalog.AddOfKind("[image]", ClipKind.Image);
        catalog.Add("text two");
        catalog.AddOfKind("C:\\one.txt", ClipKind.Files);
        catalog.AddOfKind("[image]", ClipKind.Image);
        catalog.Add("text three");

        var host = new RecordingPasteModeHost();

        var controller = new PasteModeController(
            catalog,
            host,
            new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });

        return (controller, catalog, host);
    }

    [Fact]
    public void A_frame_names_the_clip_it_is_showing()
    {
        var (controller, _, host) = Build();

        controller.Begin();

        Assert.Equal(controller.Current!.Id, host.LastFrame!.ClipId);

        controller.Handle(PasteAction.Advance);

        Assert.Equal(controller.Current!.Id, host.LastFrame!.ClipId);
    }

    /// <summary>
    /// The reported case. With the images filter on, the id still names the clip on show while the position points
    /// somewhere else entirely in store order - including, at the second image, to a <em>different image</em>,
    /// which is the failure that showed the wrong picture instead of no picture.
    /// </summary>
    [Fact]
    public void A_kind_filter_leaves_position_pointing_at_a_different_clip()
    {
        var (controller, catalog, host) = Build();

        controller.Begin();
        controller.Handle(PasteAction.CycleKindFilter); // text
        controller.Handle(PasteAction.CycleKindFilter); // images

        Assert.Equal(PasteKindFilter.Images, controller.KindFilter);

        var unfiltered = catalog.Snapshot();

        // First image: position 1 of the filtered window, but store position 1 is the newest clip, which is text.
        // That is the "no preview at all" case - the host checked the kind, found text, and gave up.
        var frame = host.LastFrame!;
        Assert.Equal(controller.Current!.Id, frame.ClipId);
        Assert.Equal(1, frame.Position);
        Assert.Equal(ClipKind.Text, unfiltered[frame.Position - 1].Kind);
        Assert.NotEqual(frame.ClipId, unfiltered[frame.Position - 1].Id);

        controller.Handle(PasteAction.Advance);

        // Second image: store position 2 holds the OTHER image. Same kind, different clip - so a host trusting
        // position would have drawn a real, wrong picture with nothing on screen to suggest it.
        frame = host.LastFrame!;
        Assert.Equal(controller.Current!.Id, frame.ClipId);
        Assert.Equal(2, frame.Position);
        Assert.Equal(ClipKind.Image, unfiltered[frame.Position - 1].Kind);
        Assert.NotEqual(frame.ClipId, unfiltered[frame.Position - 1].Id);
    }

    [Fact]
    public void A_search_leaves_position_pointing_at_a_different_clip()
    {
        var (controller, catalog, host) = Build();

        controller.Begin();
        controller.Handle(PasteAction.ToggleSearch);
        controller.SetSearchQuery("text two");

        var frame = host.LastFrame!;
        var unfiltered = catalog.Snapshot();

        Assert.Equal(controller.Current!.Id, frame.ClipId);
        Assert.NotEqual(frame.ClipId, unfiltered[frame.Position - 1].Id);
    }

    [Fact]
    public void An_empty_window_names_no_clip()
    {
        var (controller, _, host) = Build();

        controller.Begin();
        controller.Handle(PasteAction.ToggleSearch);
        controller.SetSearchQuery("nothing matches this");

        Assert.Null(controller.Current);
        Assert.Null(host.LastFrame!.ClipId);
        Assert.Equal(0, host.LastFrame.Position);
    }
}
