using PasteJump.Core.Formatting;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The kind filter: narrow the stack to text, images or files while browsing.
/// <para>
/// Clipjump has no equivalent - only a capture on/off toggle for images - so every rule here was chosen rather
/// than observed, which is exactly the shape of change PLAN.md section 5 warns about. Hence the coverage.
/// </para>
/// </summary>
public class PasteKindFilterTests
{
    /// <summary>Three text clips, two images, one file copy - ids 1..6, newest first in the window.</summary>
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
    public void The_cycle_is_all_text_images_files_and_wraps()
    {
        Assert.Equal(PasteKindFilter.Text, PasteKindFilter.All.Next());
        Assert.Equal(PasteKindFilter.Images, PasteKindFilter.Text.Next());
        Assert.Equal(PasteKindFilter.Files, PasteKindFilter.Images.Next());

        // Wraps, unlike the X commit cycle. Nothing here is destructive, so returning to "show everything" must
        // not cost three more taps.
        Assert.Equal(PasteKindFilter.All, PasteKindFilter.Files.Next());
    }

    [Fact]
    public void Cycling_narrows_the_window_to_that_kind()
    {
        var (controller, _, _) = Build();

        controller.Begin();
        Assert.Equal(6, controller.Window.Count);

        controller.Handle(PasteAction.CycleKindFilter);
        Assert.Equal(PasteKindFilter.Text, controller.KindFilter);
        Assert.Equal(3, controller.Window.Count);
        Assert.All(controller.Window, c => Assert.Equal(ClipKind.Text, c.Kind));

        controller.Handle(PasteAction.CycleKindFilter);
        Assert.Equal(2, controller.Window.Count);
        Assert.All(controller.Window, c => Assert.Equal(ClipKind.Image, c.Kind));

        controller.Handle(PasteAction.CycleKindFilter);
        Assert.Single(controller.Window);
        Assert.All(controller.Window, c => Assert.Equal(ClipKind.Files, c.Kind));

        controller.Handle(PasteAction.CycleKindFilter);
        Assert.Equal(PasteKindFilter.All, controller.KindFilter);
        Assert.Equal(6, controller.Window.Count);
    }

    /// <summary>The overlay must be told, or the filter is a stack that has silently lost most of its clips.</summary>
    [Fact]
    public void The_overlay_is_told_which_filter_is_in_force()
    {
        var (controller, _, host) = Build();

        controller.Begin();
        Assert.Equal(PasteKindFilter.All, host.LastFrame!.KindFilter);
        Assert.Null(host.LastFrame.KindFilter.Describe());

        controller.Handle(PasteAction.CycleKindFilter);
        controller.Handle(PasteAction.CycleKindFilter);

        Assert.Equal(PasteKindFilter.Images, host.LastFrame!.KindFilter);
        Assert.Equal("images only", host.LastFrame.KindFilter.Describe());
    }

    /// <summary>Cycling keeps the clip you were on when it survives the new filter.</summary>
    [Fact]
    public void The_current_clip_is_kept_when_it_survives()
    {
        var (controller, _, _) = Build();

        controller.Begin();

        // Step to the newest image, which is id 5 - second in the window behind "text three".
        controller.Handle(PasteAction.Advance);
        var image = controller.Current!;
        Assert.Equal(ClipKind.Image, image.Kind);

        controller.Handle(PasteAction.CycleKindFilter); // Text - drops it
        controller.Handle(PasteAction.CycleKindFilter); // Images - it is back

        Assert.Equal(image.Id, controller.Current!.Id);
    }

    [Fact]
    public void The_cursor_goes_to_the_top_when_the_current_clip_is_filtered_out()
    {
        var (controller, _, host) = Build();

        controller.Begin();
        Assert.Equal(ClipKind.Text, controller.Current!.Kind);

        controller.Handle(PasteAction.CycleKindFilter); // Text - kept
        controller.Handle(PasteAction.CycleKindFilter); // Images - the text clip is gone

        Assert.Equal(1, host.LastFrame!.Position);
        Assert.Equal(ClipKind.Image, controller.Current!.Kind);
    }

    /// <summary>
    /// A filter that matches nothing is a legal state, not one to skip. Skipping would make the cycle
    /// unpredictable - four taps must always return to All - and the empty window is handled everywhere already,
    /// because a search matching nothing does the same thing.
    /// </summary>
    [Fact]
    public void A_filter_matching_nothing_shows_an_empty_overlay_rather_than_being_skipped()
    {
        var catalog = new FakeClipCatalog();
        catalog.Add("only text here");

        var host = new RecordingPasteModeHost();
        var controller = new PasteModeController(catalog, host, new FormatterRegistry());

        controller.Begin();
        controller.Handle(PasteAction.CycleKindFilter); // Text
        controller.Handle(PasteAction.CycleKindFilter); // Images - none

        Assert.Equal(PasteKindFilter.Images, controller.KindFilter);
        Assert.Empty(controller.Window);
        Assert.Null(controller.Current);
        Assert.True(host.LastFrame!.IsEmpty);
        Assert.True(controller.IsActive);
    }

    /// <summary>And releasing Ctrl on an empty filtered window pastes nothing rather than throwing.</summary>
    [Fact]
    public void Releasing_Ctrl_with_an_empty_filter_passes_the_keystroke_through()
    {
        var catalog = new FakeClipCatalog();
        catalog.Add("only text here");

        var host = new RecordingPasteModeHost();
        var controller = new PasteModeController(catalog, host, new FormatterRegistry());

        controller.Begin();
        controller.Handle(PasteAction.CycleKindFilter);
        controller.Handle(PasteAction.CycleKindFilter);

        Assert.Equal(PasteCommitKind.PassedThrough, controller.ModifierReleased());
        Assert.Empty(host.PastedClips);
    }

    /// <summary>
    /// Reset per session, deliberately. A filter that survived would open the gesture on a stack with most of it
    /// missing and only a chip to explain why, which reads as clips having been lost.
    /// </summary>
    [Fact]
    public void The_filter_resets_when_the_gesture_reopens()
    {
        var (controller, _, _) = Build();

        controller.Begin();
        controller.Handle(PasteAction.CycleKindFilter);
        Assert.Equal(PasteKindFilter.Text, controller.KindFilter);

        controller.Abort();
        controller.Begin();

        Assert.Equal(PasteKindFilter.All, controller.KindFilter);
        Assert.Equal(6, controller.Window.Count);
    }

    /// <summary>The filter and the search query compose: narrow to images, then search within them.</summary>
    [Fact]
    public void The_filter_composes_with_search()
    {
        var (controller, _, _) = Build();

        controller.Begin();
        controller.Handle(PasteAction.CycleKindFilter); // Text
        controller.Handle(PasteAction.ToggleSearch);
        controller.SetSearchQuery("two");

        Assert.Single(controller.Window);
        Assert.Equal("text two", controller.Window[0].Preview);

        // And the kind still applies - "one.txt" is a file copy, so it cannot match while Text is in force.
        controller.SetSearchQuery("one");
        Assert.Single(controller.Window);
        Assert.Equal("text one", controller.Window[0].Preview);
    }

    /// <summary>
    /// Anything unrecognised stays visible under All. Erring towards showing a clip is the safe direction: a
    /// filter that hid something would read as the clip having been lost.
    /// </summary>
    [Fact]
    public void All_admits_every_kind_including_Other()
    {
        foreach (var kind in Enum.GetValues<ClipKind>())
        {
            Assert.True(PasteKindFilter.All.Admits(kind));
        }

        Assert.False(PasteKindFilter.Images.Admits(ClipKind.Other));
    }
}
