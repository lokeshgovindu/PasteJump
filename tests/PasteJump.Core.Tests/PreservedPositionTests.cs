using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Where paste mode opens, and specifically the interaction between "remember my position" and a new
/// copy arriving.
/// <para>
/// These are two separate rules, and collapsing them into one caused a bug that made the app feel
/// broken: the remembered position was stored when a session ended and then never cleared, so once the
/// user had browsed away from the newest clip, every later Ctrl+V reopened on that same clip regardless
/// of what had been copied since. Copy five file paths, and the gesture still offered the stale one.
/// </para>
/// <para>
/// The original splits them explicitly. <c>clipChange()</c> sets <c>TEMPSAVE := CURSAVE</c> on every
/// successful copy (Clipjump.ahk:508, :517) with no reference to the setting, whereas
/// <c>ini_PreserveClipPos</c> is consulted only as a paste session ends (Clipjump.ahk:1010-1012).
/// </para>
/// </summary>
public sealed class PreservedPositionTests
{
    private static (PasteModeController Controller, FakeClipCatalog Catalog) Build(
        int clipCount,
        bool preservePosition)
    {
        var catalog = new FakeClipCatalog();

        for (var i = 1; i <= clipCount; i++)
        {
            catalog.Add($"clip {i}");
        }

        var controller = new PasteModeController(
            catalog,
            new RecordingPasteModeHost(),
            new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = preservePosition });

        return (controller, catalog);
    }

    /// <summary>Opens paste mode, steps back <paramref name="steps"/> clips, then commits.</summary>
    private static void BrowseToAndPaste(PasteModeController controller, int steps)
    {
        controller.Begin();

        for (var i = 0; i < steps; i++)
        {
            controller.Handle(PasteAction.Advance);
        }

        controller.ModifierReleased();
    }

    [Fact]
    public void A_new_copy_resets_the_position_to_the_newest_clip()
    {
        // The reported failure, reduced: browse to an older clip, paste, copy something new, and the
        // next Ctrl+V must offer the new clip - not the one browsed to earlier.
        var (controller, catalog) = Build(5, preservePosition: true);

        BrowseToAndPaste(controller, steps: 4);

        catalog.Add("freshly copied");
        controller.NotifyClipCaptured();

        controller.Begin();

        Assert.Equal(0, controller.CursorIndex);
        Assert.Equal("freshly copied", controller.Current!.Preview);
    }

    [Fact]
    public void Five_copies_in_a_row_still_leave_the_newest_selected()
    {
        // Closest reproduction of "I copy file paths five times and it pastes the wrong one".
        var (controller, catalog) = Build(1, preservePosition: true);

        BrowseToAndPaste(controller, steps: 0);

        for (var i = 1; i <= 5; i++)
        {
            catalog.Add($@"C:\path\file{i}.txt");
            controller.NotifyClipCaptured();
        }

        controller.Begin();

        Assert.Equal(0, controller.CursorIndex);
        Assert.Equal(@"C:\path\file5.txt", controller.Current!.Preview);
    }

    [Fact]
    public void Without_a_new_copy_the_position_is_preserved()
    {
        // The setting still has to work: with nothing copied in between, the next session resumes on
        // the clip the last one ended on.
        var (controller, _) = Build(5, preservePosition: true);

        BrowseToAndPaste(controller, steps: 2);

        controller.Begin();

        Assert.Equal(2, controller.CursorIndex);
        Assert.Equal("clip 3", controller.Current!.Preview);
    }

    [Fact]
    public void With_the_setting_off_every_session_starts_at_the_newest()
    {
        var (controller, _) = Build(5, preservePosition: false);

        BrowseToAndPaste(controller, steps: 3);

        controller.Begin();

        Assert.Equal(0, controller.CursorIndex);
        Assert.Equal("clip 5", controller.Current!.Preview);
    }

    [Fact]
    public void A_capture_during_an_open_session_does_not_move_the_cursor()
    {
        // Something copied while the overlay is up must not yank the selection out from under the user
        // mid-gesture. The reset applies to the next session.
        var (controller, catalog) = Build(4, preservePosition: true);

        controller.Begin();
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.Advance);

        Assert.Equal(2, controller.CursorIndex);

        catalog.Add("arrived mid-gesture");
        controller.NotifyClipCaptured();

        Assert.Equal(2, controller.CursorIndex);
    }

    [Fact]
    public void A_preserved_clip_that_has_been_deleted_falls_back_to_the_newest()
    {
        var (controller, catalog) = Build(3, preservePosition: true);

        controller.Begin();
        controller.Handle(PasteAction.Advance);

        var browsedTo = controller.Current!.Id;
        controller.ModifierReleased();

        catalog.Delete(browsedTo);

        controller.Begin();

        Assert.Equal(0, controller.CursorIndex);
        Assert.Equal("clip 3", controller.Current!.Preview);
    }
}
