using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The five invariants from the design. These are the tests that matter: if any of them regress
/// the app is broken in a way users will hit within minutes.
/// </summary>
public class PasteModeInvariantTests
{
    private static (PasteModeController Controller, FakeClipCatalog Catalog, RecordingPasteModeHost Host)
        Build(int clipCount = 3, PasteModeOptions? options = null)
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

    // Invariant 1 -----------------------------------------------------------

    [Fact]
    public void ReleasingModifier_AlwaysEndsBrowsingSession()
    {
        var (controller, _, host) = Build();

        controller.Begin();
        Assert.Equal(PasteSessionState.Browsing, controller.State);

        controller.ModifierReleased();

        Assert.Equal(PasteSessionState.Idle, controller.State);
        Assert.False(controller.IsActive);
        Assert.False(host.OverlayVisible);
    }

    [Fact]
    public void ReleasingModifier_EndsSession_EvenAfterManyIntermediateKeys()
    {
        var (controller, _, host) = Build(clipCount: 5);

        controller.Begin();
        controller.Handle(PasteAction.Advance);
        controller.Handle(PasteAction.CycleFormatter);
        controller.Handle(PasteAction.Back);
        controller.Handle(PasteAction.Help);
        controller.HandleDigit(3);
        controller.Handle(PasteAction.ToggleJumpDirection);
        controller.HandleDigit(2);

        controller.ModifierReleased();

        Assert.Equal(PasteSessionState.Idle, controller.State);
        Assert.False(host.OverlayVisible);
    }

    // Invariant 2 -----------------------------------------------------------

    /// <summary>
    /// DELETE ALL asks first and deletes nothing on its own. The prompt cannot be answered inside
    /// <c>ModifierReleased</c> - that runs in the keyboard hook, where anything modal blocks the whole machine's
    /// keyboard - so the controller must hand the deletion over and let it happen later, or not at all.
    /// </summary>
    [Fact]
    public void DeleteAll_AsksBeforeDeletingAnything()
    {
        var (controller, catalog, host) = Build(clipCount: 3);

        controller.Begin();

        for (var i = 0; i < 3; i++)
        {
            controller.Handle(PasteAction.CycleCommitMode);
        }

        var kind = controller.ModifierReleased();

        Assert.Equal(PasteCommitKind.DeleteAllRequested, kind);
        Assert.Equal(3, host.DeleteAllConfirmationCount);

        // The part that matters: nothing has gone yet.
        Assert.Equal(0, catalog.DeleteAllCallCount);
        Assert.Equal(3, catalog.Snapshot().Count);
    }

    [Fact]
    public void DeleteAll_DeletesOnceTheRequestIsConfirmed()
    {
        var (controller, catalog, host) = Build(clipCount: 3);

        controller.Begin();

        for (var i = 0; i < 3; i++)
        {
            controller.Handle(PasteAction.CycleCommitMode);
        }

        controller.ModifierReleased();
        host.DeleteAllConfirmAction!();

        Assert.Equal(1, catalog.DeleteAllCallCount);
        Assert.Empty(catalog.Snapshot());
    }

    /// <summary>
    /// The count offered to the user excludes pinned clips, because those survive the deletion. Promising to
    /// remove more than will actually go would make the prompt a lie.
    /// </summary>
    [Fact]
    public void DeleteAll_CountsOnlyUnpinnedClips()
    {
        var catalog = new FakeClipCatalog();
        catalog.Add("one");
        catalog.Add("two");
        catalog.AddPinned("kept");

        var host = new RecordingPasteModeHost();
        var controller = new PasteModeController(
            catalog,
            host,
            new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });

        controller.Begin();

        for (var i = 0; i < 3; i++)
        {
            controller.Handle(PasteAction.CycleCommitMode);
        }

        controller.ModifierReleased();

        Assert.Equal(2, host.DeleteAllConfirmationCount);
    }

    [Theory]
    [InlineData(1, PasteCommitKind.Cancelled)]
    [InlineData(2, PasteCommitKind.Deleted)]
    [InlineData(3, PasteCommitKind.DeleteAllRequested)]
    public void DestructiveCommitModes_RestoreThePreviousClipboard(int cyclePresses, PasteCommitKind expected)
    {
        var (controller, _, host) = Build();

        controller.Begin();

        for (var i = 0; i < cyclePresses; i++)
        {
            controller.Handle(PasteAction.CycleCommitMode);
        }

        var restoresBefore = host.RestoreCount;
        var kind = controller.ModifierReleased();

        Assert.Equal(expected, kind);
        Assert.Equal(restoresBefore + 1, host.RestoreCount);
        Assert.Empty(host.PastedClips);
    }

    [Fact]
    public void Escape_RestoresClipboardAndEndsSession()
    {
        var (controller, _, host) = Build();

        controller.Begin();
        var kind = controller.Handle(PasteAction.Escape);

        Assert.Equal(PasteCommitKind.Cancelled, kind);
        Assert.Equal(1, host.RestoreCount);
        Assert.Equal(PasteSessionState.Idle, controller.State);
    }

    // Invariant 3 -----------------------------------------------------------

    [Fact]
    public void Paste_LeavesPastedClipOnClipboard_AndDoesNotRestore()
    {
        var (controller, _, host) = Build();

        controller.Begin();
        var kind = controller.ModifierReleased();

        Assert.Equal(PasteCommitKind.Pasted, kind);
        Assert.Single(host.PastedClips);

        // A paste deliberately does NOT restore: the pasted clip stays put so a following
        // native Ctrl+V repeats it, matching the original's behaviour.
        Assert.Equal(0, host.RestoreCount);
    }

    // Invariant 4 -----------------------------------------------------------

    [Fact]
    public void InSearchMode_ReleasingModifier_DoesNotCommit()
    {
        var (controller, _, host) = Build();

        controller.Begin();
        controller.Handle(PasteAction.ToggleSearch);
        Assert.Equal(PasteSessionState.Searching, controller.State);

        var kind = controller.ModifierReleased();

        Assert.Equal(PasteCommitKind.None, kind);
        Assert.Equal(PasteSessionState.Searching, controller.State);
        Assert.True(controller.IsActive);
        Assert.Empty(host.PastedClips);
    }

    [Fact]
    public void InSearchMode_ExplicitCommit_Pastes()
    {
        var (controller, _, host) = Build();

        controller.Begin();
        controller.Handle(PasteAction.ToggleSearch);
        var kind = controller.CommitExplicitly();

        Assert.Equal(PasteCommitKind.Pasted, kind);
        Assert.Single(host.PastedClips);
        Assert.Equal(PasteSessionState.Idle, controller.State);
    }

    // Invariant 5 -----------------------------------------------------------

    [Fact]
    public void EmptyStore_PassesCtrlVThrough_RatherThanSwallowingIt()
    {
        var (controller, _, host) = Build(clipCount: 0);

        var kind = controller.Begin();

        Assert.Equal(PasteCommitKind.PassedThrough, kind);
        Assert.Equal(1, host.PassThroughCount);
        Assert.Equal(PasteSessionState.Idle, controller.State);
        Assert.False(host.OverlayVisible);
    }

    [Fact]
    public void StoreEmptiedMidSession_StillPassesThroughOnCommit()
    {
        var (controller, catalog, host) = Build(clipCount: 2);

        controller.Begin();

        // Delete-all mode, commit, confirm, then a fresh session with nothing left.
        controller.Handle(PasteAction.CycleCommitMode);
        controller.Handle(PasteAction.CycleCommitMode);
        controller.Handle(PasteAction.CycleCommitMode);
        controller.ModifierReleased();
        host.DeleteAllConfirmAction!();

        Assert.Empty(catalog.Snapshot());

        var kind = controller.Begin();
        Assert.Equal(PasteCommitKind.PassedThrough, kind);
    }
}
