using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Marking clips during the gesture so several paste as one. The state machine half of joining - the text is put
/// together by the host, which needs the store, so what is asserted here is which clips are chosen, in what order,
/// and when the marks go away.
/// </summary>
public class JoinMarkTests
{
    private static (PasteModeController Controller, RecordingPasteModeHost Host, FakeClipCatalog Catalog) Build(
        int clipCount = 4)
    {
        var catalog = new FakeClipCatalog();

        for (var i = 1; i <= clipCount; i++)
        {
            catalog.Add($"clip {i}");
        }

        var host = new RecordingPasteModeHost();

        var controller = new PasteModeController(
            catalog, host, new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });

        controller.Begin();

        return (controller, host, catalog);
    }

    [Fact]
    public void Nothing_is_marked_when_a_session_opens()
    {
        var (controller, _, _) = Build();

        Assert.Equal(0, controller.MarkedCount);
        Assert.False(controller.CurrentIsMarked);
    }

    [Fact]
    public void Marking_the_current_clip_counts_it()
    {
        var (controller, _, _) = Build();

        controller.Handle(PasteAction.ToggleJoinMark);

        Assert.Equal(1, controller.MarkedCount);
        Assert.True(controller.CurrentIsMarked);
    }

    [Fact]
    public void Marking_twice_unmarks()
    {
        var (controller, _, _) = Build();

        controller.Handle(PasteAction.ToggleJoinMark);
        controller.Handle(PasteAction.ToggleJoinMark);

        Assert.Equal(0, controller.MarkedCount);
        Assert.False(controller.CurrentIsMarked);
    }

    /// <summary>
    /// Marking must not advance. A key that also stepped would make "mark this one and that one" require counting,
    /// and the two useful sequences - mark-step-mark, mark-search-mark - are driven by the user.
    /// </summary>
    [Fact]
    public void Marking_does_not_move_the_cursor()
    {
        var (controller, _, _) = Build();

        controller.Handle(PasteAction.Advance);
        var before = controller.CursorIndex;

        controller.Handle(PasteAction.ToggleJoinMark);

        Assert.Equal(before, controller.CursorIndex);
    }

    [Fact]
    public void Releasing_ctrl_pastes_the_marked_clips_joined()
    {
        var (controller, host, _) = Build();

        controller.Handle(PasteAction.ToggleJoinMark);
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.ToggleJoinMark);

        var kind = controller.ModifierReleased();

        Assert.Equal(PasteCommitKind.Pasted, kind);
        Assert.Empty(host.PastedClips);

        var joined = Assert.Single(host.JoinedClips);
        Assert.Equal(2, joined.Count);
    }

    /// <summary>
    /// Mark order, not stack order: the user marks clips in the sequence they want them, and during the gesture
    /// that sequence is knowable - unlike the history window, where a DataGrid cannot report click order and
    /// display order is used instead.
    /// </summary>
    [Fact]
    public void The_marked_clips_are_pasted_in_the_order_they_were_marked()
    {
        var (controller, host, _) = Build();

        controller.Handle(PasteAction.JumpToOldest);
        controller.Handle(PasteAction.ToggleJoinMark);
        controller.Handle(PasteAction.JumpToNewest);
        controller.Handle(PasteAction.ToggleJoinMark);

        controller.ModifierReleased();

        var joined = Assert.Single(host.JoinedClips);

        Assert.Equal("clip 1", joined[0].Preview);
        Assert.Equal("clip 4", joined[1].Preview);
    }

    /// <summary>
    /// Unmarking and marking again moves the clip to the END of the order, which is how a sequence gets corrected
    /// without starting over.
    /// </summary>
    [Fact]
    public void Re_marking_moves_a_clip_to_the_end_of_the_order()
    {
        var (controller, host, _) = Build();

        controller.Handle(PasteAction.JumpToNewest);
        controller.Handle(PasteAction.ToggleJoinMark);
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.ToggleJoinMark);

        // Back to the first one, off and on again.
        controller.Handle(PasteAction.JumpToNewest);
        controller.Handle(PasteAction.ToggleJoinMark);
        controller.Handle(PasteAction.ToggleJoinMark);

        controller.ModifierReleased();

        var joined = Assert.Single(host.JoinedClips);

        Assert.Equal(["clip 3", "clip 4"], joined.Select(static c => c.Preview));
    }

    /// <summary>
    /// The marks decide what is pasted, wherever the cursor ends up. That is the point of having marked: the clips
    /// chosen are the clips pasted.
    /// </summary>
    [Fact]
    public void The_cursor_position_does_not_change_what_a_marked_session_pastes()
    {
        var (controller, host, _) = Build();

        controller.Handle(PasteAction.ToggleJoinMark);
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.Advance);

        controller.ModifierReleased();

        var joined = Assert.Single(host.JoinedClips);

        Assert.Equal("clip 4", Assert.Single(joined).Preview);
    }

    [Fact]
    public void With_nothing_marked_the_ordinary_single_clip_paste_still_happens()
    {
        var (controller, host, _) = Build();

        controller.ModifierReleased();

        Assert.Single(host.PastedClips);
        Assert.Empty(host.JoinedClips);
    }

    /// <summary>
    /// Per session, like the kind filter and deliberately not governed by PreserveClipPosition: a mark that
    /// survived would make the next ordinary Ctrl+V paste something assembled minutes ago.
    /// </summary>
    [Fact]
    public void Marks_do_not_survive_the_end_of_a_session()
    {
        var (controller, host, _) = Build();

        controller.Handle(PasteAction.ToggleJoinMark);
        controller.ModifierReleased();

        controller.Begin();

        Assert.Equal(0, controller.MarkedCount);

        controller.ModifierReleased();

        Assert.Single(host.JoinedClips);
        Assert.Single(host.PastedClips);
    }

    [Fact]
    public void Marks_do_not_survive_an_escape_either()
    {
        var (controller, _, _) = Build();

        controller.Handle(PasteAction.ToggleJoinMark);
        controller.Abort();
        controller.Begin();

        Assert.Equal(0, controller.MarkedCount);
    }

    /// <summary>
    /// A mark is an id, so narrowing the window must not lose it - marking, searching for the next clip, and
    /// marking again is the obvious way to use this.
    /// </summary>
    [Fact]
    public void A_mark_survives_a_search_that_hides_the_clip()
    {
        var (controller, host, _) = Build();

        controller.Handle(PasteAction.ToggleJoinMark);

        controller.Handle(PasteAction.ToggleSearch);
        controller.SetSearchQuery("clip 1");

        Assert.Equal(1, controller.MarkedCount);

        // Search closed before releasing Ctrl, because releasing it while searching deliberately does nothing -
        // the session stays open until Enter or Esc. That is existing behaviour, not part of this feature.
        controller.Handle(PasteAction.ToggleSearch);
        controller.ModifierReleased();

        Assert.Equal("clip 4", Assert.Single(Assert.Single(host.JoinedClips)).Preview);
    }

    /// <summary>
    /// Deleting a marked clip unmarks it. MarkedClips would skip it anyway, but a chip reading JOIN 3 while only
    /// two clips remain is a lie about what releasing Ctrl will do.
    /// </summary>
    [Fact]
    public void Deleting_a_marked_clip_unmarks_it()
    {
        var (controller, _, _) = Build();

        controller.Handle(PasteAction.ToggleJoinMark);
        controller.Handle(PasteAction.DeleteCurrentClip);

        Assert.Equal(0, controller.MarkedCount);
    }

    /// <summary>
    /// Every marked clip deleted mid-session leaves nothing to paste. It must pass the keystroke through rather
    /// than swallowing it - the same rule as an empty store, and the worst failure this app can have.
    /// </summary>
    [Fact]
    public void A_session_whose_marks_were_all_deleted_passes_the_keystroke_through()
    {
        var (controller, host, catalog) = Build();

        controller.Handle(PasteAction.ToggleJoinMark);

        foreach (var clip in catalog.Snapshot().ToList())
        {
            catalog.Delete(clip.Id);
        }

        var kind = controller.ModifierReleased();

        Assert.Equal(PasteCommitKind.PassedThrough, kind);
        Assert.Empty(host.JoinedClips);
        Assert.Equal(1, host.PassThroughCount);
    }

    /// <summary>
    /// Pop deletes what was pasted, which with marks means all of them. Consistent rather than cautious, and
    /// deliberate twice over on the user's part: they marked each clip, and they held Shift while releasing Ctrl.
    /// </summary>
    [Fact]
    public void Popping_a_marked_session_deletes_every_marked_clip()
    {
        var (controller, _, catalog) = Build();

        controller.Handle(PasteAction.ToggleJoinMark);
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.ToggleJoinMark);

        controller.ShiftHeld = true;
        controller.ModifierReleased();

        Assert.Equal(2, catalog.Snapshot().Count);
    }

    [Fact]
    public void The_overlay_is_told_the_count_and_whether_this_clip_is_one_of_them()
    {
        var (controller, host, _) = Build();

        controller.Handle(PasteAction.ToggleJoinMark);
        controller.Handle(PasteAction.Advance);

        var model = host.OverlayFrames[^1];

        Assert.Equal(1, model.MarkedCount);
        Assert.False(model.CurrentIsMarked);

        controller.Handle(PasteAction.ToggleJoinMark);

        Assert.True(host.OverlayFrames[^1].CurrentIsMarked);
        Assert.Equal(2, host.OverlayFrames[^1].MarkedCount);
    }
}
