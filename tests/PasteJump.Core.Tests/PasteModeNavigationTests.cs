using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The gesture itself: stepping, wrapping, the X cycle, digit jumps, pinning and search.
/// </summary>
public class PasteModeNavigationTests
{
    private static (PasteModeController Controller, FakeClipCatalog Catalog, RecordingPasteModeHost Host)
        Build(int clipCount, PasteModeOptions? options = null)
    {
        var catalog = new FakeClipCatalog();

        for (var i = 1; i <= clipCount; i++)
        {
            catalog.Add($"clip {i}");
        }

        var host = new RecordingPasteModeHost();
        var controller = new PasteModeController(
            catalog,
            host,
            new FormatterRegistry(),
            options ?? new PasteModeOptions { PreserveClipPosition = false });

        return (controller, catalog, host);
    }

    [Fact]
    public void Begin_StartsOnNewestClip()
    {
        var (controller, _, _) = Build(3);

        controller.Begin();

        // FakeClipCatalog adds ascending, so "clip 3" is newest and sorts first.
        Assert.Equal("clip 3", controller.Current!.Preview);
        Assert.Equal(0, controller.CursorIndex);
    }

    [Fact]
    public void RepeatedEntryChord_AdvancesInsteadOfRestarting()
    {
        var (controller, _, host) = Build(3);

        controller.Begin();
        controller.Begin();
        controller.Begin();

        Assert.Equal("clip 1", controller.Current!.Preview);

        // Only the first entry snapshots the clipboard; re-entry must not clobber the saved copy.
        Assert.Equal(1, host.SnapshotCount);
    }

    [Fact]
    public void Advance_WalksTowardsOlderClips_AndWraps()
    {
        var (controller, _, _) = Build(3);

        controller.Begin();
        Assert.Equal("clip 3", controller.Current!.Preview);

        controller.Handle(PasteAction.Advance);
        Assert.Equal("clip 2", controller.Current!.Preview);

        controller.Handle(PasteAction.Advance);
        Assert.Equal("clip 1", controller.Current!.Preview);

        controller.Handle(PasteAction.Advance);
        Assert.Equal("clip 3", controller.Current!.Preview);
    }

    [Fact]
    public void Back_WalksTowardsNewerClips_AndWraps()
    {
        var (controller, _, _) = Build(3);

        controller.Begin();
        controller.Handle(PasteAction.Back);

        Assert.Equal("clip 1", controller.Current!.Preview);
    }

    [Fact]
    public void JumpToNewest_ReturnsToFirstPosition()
    {
        var (controller, _, _) = Build(5);

        controller.Begin();
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.JumpToNewest);

        Assert.Equal(0, controller.CursorIndex);
    }

    [Fact]
    public void CommitModeCycle_NeverReturnsToPaste()
    {
        var (controller, _, _) = Build(2);

        controller.Begin();
        Assert.Equal(PasteCommitMode.Paste, controller.CommitMode);

        controller.Handle(PasteAction.CycleCommitMode);
        Assert.Equal(PasteCommitMode.Cancel, controller.CommitMode);

        controller.Handle(PasteAction.CycleCommitMode);
        Assert.Equal(PasteCommitMode.Delete, controller.CommitMode);

        controller.Handle(PasteAction.CycleCommitMode);
        Assert.Equal(PasteCommitMode.DeleteAll, controller.CommitMode);

        // Wraps to Cancel, not Paste - an over-eager keypress must never turn a delete into a paste.
        controller.Handle(PasteAction.CycleCommitMode);
        Assert.Equal(PasteCommitMode.Cancel, controller.CommitMode);
    }

    [Fact]
    public void DigitJump_MovesThatManyClips()
    {
        var (controller, _, _) = Build(9);

        controller.Begin();
        controller.HandleDigit(3);

        Assert.Equal(3, controller.CursorIndex);
    }

    [Fact]
    public void ToggleJumpDirection_ReversesDigitJumps()
    {
        var (controller, _, _) = Build(9);

        controller.Begin();
        controller.HandleDigit(4);
        Assert.Equal(4, controller.CursorIndex);

        controller.Handle(PasteAction.ToggleJumpDirection);
        controller.HandleDigit(2);
        Assert.Equal(2, controller.CursorIndex);
    }

    [Fact]
    public void ShiftHeld_DeletesClipAfterPasting()
    {
        var (controller, catalog, host) = Build(3);

        controller.Begin();
        controller.ShiftHeld = true;

        var target = controller.Current!.Id;
        var kind = controller.ModifierReleased();

        Assert.Equal(PasteCommitKind.Pasted, kind);
        Assert.Single(host.PastedClips);
        Assert.Equal(1, catalog.DeleteCallCount);
        Assert.DoesNotContain(catalog.Snapshot(), c => c.Id == target);
    }

    [Fact]
    public void PasteWithoutShift_KeepsTheClip()
    {
        var (controller, catalog, _) = Build(3);

        controller.Begin();
        controller.ModifierReleased();

        Assert.Equal(0, catalog.DeleteCallCount);
        Assert.Equal(3, catalog.Snapshot().Count);
    }

    [Fact]
    public void DeleteAll_KeepsPinnedClips()
    {
        var catalog = new FakeClipCatalog();
        catalog.Add("ordinary");
        var pinnedId = catalog.AddPinned("important");
        catalog.Add("also ordinary");

        var host = new RecordingPasteModeHost();
        var controller = new PasteModeController(
            catalog, host, new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });

        controller.Begin();
        controller.Handle(PasteAction.CycleCommitMode);
        controller.Handle(PasteAction.CycleCommitMode);
        controller.Handle(PasteAction.CycleCommitMode);
        controller.ModifierReleased();

        var remaining = catalog.Snapshot();
        Assert.Single(remaining);
        Assert.Equal(pinnedId, remaining[0].Id);
    }

    [Fact]
    public void TogglePin_KeepsCursorOnTheSameClip_DespiteReordering()
    {
        var (controller, _, _) = Build(4);

        controller.Begin();
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.Advance);

        var target = controller.Current!;
        Assert.Equal("clip 2", target.Preview);

        controller.Handle(PasteAction.TogglePin);

        // Pinning floats the clip to the top of the ordering; the cursor must follow the clip,
        // not stay at index 2 and silently select something else.
        Assert.Equal(target.Id, controller.Current!.Id);
        Assert.True(controller.Current!.Pinned);
        Assert.Equal(0, controller.CursorIndex);
    }

    [Fact]
    public void PromoteToFront_MovesClipAndKeepsCursorOnIt()
    {
        var (controller, _, _) = Build(4);

        controller.Begin();
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.Advance);

        var target = controller.Current!;
        controller.Handle(PasteAction.PromoteToFront);

        Assert.Equal(target.Id, controller.Current!.Id);
        Assert.Equal(0, controller.CursorIndex);
        Assert.Equal(target.Id, controller.Window[0].Id);
    }

    [Fact]
    public void Multipaste_PastesAndKeepsSessionOpen()
    {
        var (controller, _, host) = Build(3);

        controller.Begin();

        var kind = controller.Handle(PasteAction.Multipaste);

        Assert.Equal(PasteCommitKind.Pasted, kind);
        Assert.Single(host.PastedClips);
        Assert.True(controller.IsActive);

        controller.Handle(PasteAction.Multipaste);
        Assert.Equal(2, host.PastedClips.Count);
        Assert.True(controller.IsActive);
    }

    [Fact]
    public void PushToClipboard_DoesNotPaste_AndEndsSession()
    {
        var (controller, _, host) = Build(3);

        controller.Begin();
        var kind = controller.Handle(PasteAction.PushToClipboard);

        Assert.Equal(PasteCommitKind.PushedToClipboard, kind);
        Assert.Single(host.PushedClips);
        Assert.Empty(host.PastedClips);
        Assert.Equal(PasteSessionState.Idle, controller.State);
    }

    [Fact]
    public void CycleFormatter_AdvancesThroughRegistryAndWraps()
    {
        var (controller, _, _) = Build(2);
        var registry = new FormatterRegistry();

        controller.Begin();
        Assert.Equal("Original", controller.Formatter.DisplayName);

        for (var i = 0; i < registry.All.Count; i++)
        {
            controller.Handle(PasteAction.CycleFormatter);
        }

        Assert.Equal("Original", controller.Formatter.DisplayName);
    }

    [Fact]
    public void EditTags_EndsSessionAndDelegatesToHost()
    {
        var (controller, _, host) = Build(2);

        controller.Begin();
        var target = controller.Current!;
        controller.Handle(PasteAction.EditTags);

        Assert.Equal(target.Id, host.TagEditorRequestedFor!.Id);
        Assert.Equal(PasteSessionState.Idle, controller.State);

        // The clipboard must be put back before a real window takes focus.
        Assert.Equal(1, host.RestoreCount);
    }

    [Fact]
    public void PreserveClipPosition_ReopensOnPreviousClip()
    {
        var catalog = new FakeClipCatalog();

        for (var i = 1; i <= 4; i++)
        {
            catalog.Add($"clip {i}");
        }

        var host = new RecordingPasteModeHost();
        var controller = new PasteModeController(
            catalog, host, new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = true });

        controller.Begin();
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.Advance);
        var landed = controller.Current!.Id;
        controller.Handle(PasteAction.Escape);

        controller.Begin();

        Assert.Equal(landed, controller.Current!.Id);
    }

    [Fact]
    public void WithoutPreserveClipPosition_ReopensOnNewest()
    {
        var (controller, _, _) = Build(4);

        controller.Begin();
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.Escape);

        controller.Begin();

        Assert.Equal(0, controller.CursorIndex);
    }
}
