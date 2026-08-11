using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The physical navigation keys - Home, End and Delete - and the rule that Help ends the session.
/// <para>
/// These were added after a report that F1 opened the key card over a live overlay, so the keys the card was
/// explaining were still being eaten by the gesture. The navigation keys came with it, on the grounds that
/// Home had been a second Escape and End and Delete did nothing at all.
/// </para>
/// </summary>
public class PasteModeNavigationKeyTests
{
    private static (PasteModeController Controller, FakeClipCatalog Catalog, RecordingPasteModeHost Host)
        Build(int clipCount = 5)
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
            new PasteModeOptions { PreserveClipPosition = false });

        return (controller, catalog, host);
    }

    /// <summary>
    /// The arrow keys step an open session and must never open one. Mapping them onto
    /// <see cref="GestureKey.Paste"/> - which is the entry key - claimed Ctrl+Right and Ctrl+Down machine-wide:
    /// the overlay appeared over an editor, the keystroke was swallowed so the caret never moved, and releasing
    /// Ctrl pasted. Reported within a day of shipping it.
    /// </summary>
    [Theory]
    [InlineData(GestureKey.StepOlder)]
    [InlineData(GestureKey.Back)]
    [InlineData(GestureKey.JumpToNewest)]
    [InlineData(GestureKey.JumpToOldest)]
    [InlineData(GestureKey.DeleteCurrent)]
    public void No_navigation_key_can_open_a_session(GestureKey key)
    {
        var (controller, _, host) = Build();
        var recognizer = new PasteGestureRecognizer(controller) { AltHeld = false, WinHeld = false };

        recognizer.Handle(GestureKey.Control, isDown: true);

        var swallowed = recognizer.Handle(key, isDown: true);

        // Both halves matter. Not opening a session is the behaviour; not swallowing is what lets the editor
        // underneath actually receive Ctrl+Right and move the caret.
        Assert.False(swallowed);
        Assert.False(controller.IsActive);
        Assert.False(host.OverlayVisible);
    }

    /// <summary>The trigger itself must still open one, or the fix above has broken the product.</summary>
    [Fact]
    public void The_trigger_key_still_opens_a_session()
    {
        var (controller, _, host) = Build();
        var recognizer = new PasteGestureRecognizer(controller);

        recognizer.Handle(GestureKey.Control, isDown: true);

        Assert.True(recognizer.Handle(GestureKey.Paste, isDown: true));
        Assert.True(controller.IsActive);
        Assert.True(host.OverlayVisible);
    }

    /// <summary>And once it is open, the arrow steps like the trigger does.</summary>
    [Fact]
    public void StepOlder_advances_an_open_session()
    {
        var (controller, _, host) = Build(clipCount: 5);
        var recognizer = new PasteGestureRecognizer(controller);

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        var first = host.LastFrame!.Position;

        Assert.True(recognizer.Handle(GestureKey.StepOlder, isDown: true));
        Assert.Equal(first + 1, host.LastFrame!.Position);
    }

    [Fact]
    public void JumpToOldest_lands_on_the_last_clip_in_the_window()
    {
        var (controller, _, host) = Build(clipCount: 5);

        controller.Begin();
        controller.Handle(PasteAction.JumpToOldest);

        // Position is 1-based in the overlay model, so the oldest of five reads as 5 of 5.
        Assert.Equal(5, host.LastFrame!.Position);
        Assert.Equal(5, host.LastFrame.Total);
    }

    [Fact]
    public void JumpToNewest_and_JumpToOldest_are_opposite_ends()
    {
        var (controller, _, host) = Build(clipCount: 4);

        controller.Begin();

        controller.Handle(PasteAction.JumpToOldest);
        Assert.Equal(4, host.LastFrame!.Position);

        controller.Handle(PasteAction.JumpToNewest);
        Assert.Equal(1, host.LastFrame.Position);
    }

    /// <summary>
    /// The empty case is the one that would throw rather than misbehave: a search matching nothing leaves the
    /// window empty, and Count - 1 is then -1.
    /// </summary>
    [Fact]
    public void JumpToOldest_on_an_empty_window_does_not_throw()
    {
        var (controller, _, _) = Build(clipCount: 0);

        controller.Begin();
        controller.Handle(PasteAction.JumpToOldest);

        Assert.Null(controller.Current);
    }

    [Fact]
    public void Delete_removes_the_clip_and_keeps_the_session_open()
    {
        var (controller, catalog, host) = Build(clipCount: 3);

        controller.Begin();
        var doomed = controller.Current!.Id;

        var result = controller.Handle(PasteAction.DeleteCurrentClip);

        Assert.Equal(PasteCommitKind.None, result);
        Assert.True(controller.IsActive);
        Assert.True(host.OverlayVisible);
        Assert.Equal(1, catalog.DeleteCallCount);
        Assert.DoesNotContain(catalog.Snapshot(), c => c.Id == doomed);

        // Two left, and the cursor stayed at the top - so it now shows what used to be the second clip.
        Assert.Equal(2, host.LastFrame!.Total);
        Assert.Equal(1, host.LastFrame.Position);
    }

    [Fact]
    public void Delete_pastes_nothing()
    {
        var (controller, _, host) = Build(clipCount: 3);

        controller.Begin();
        controller.Handle(PasteAction.DeleteCurrentClip);

        Assert.Empty(host.PastedClips);
        Assert.Equal(0, host.PassThroughCount);
    }

    /// <summary>
    /// Repeated presses walk forward through the stack rather than stalling, because the cursor is left where
    /// it is and the clip beneath it moves up. The last one empties the window without throwing.
    /// </summary>
    [Fact]
    public void Delete_repeated_empties_the_stack_and_then_does_nothing()
    {
        var (controller, catalog, _) = Build(clipCount: 3);

        controller.Begin();

        for (var i = 0; i < 5; i++)
        {
            controller.Handle(PasteAction.DeleteCurrentClip);
        }

        Assert.Empty(catalog.Snapshot());

        // Three deletions for three clips: the two extra presses found nothing to delete and said so by
        // returning early rather than by deleting whatever the cursor had landed on.
        Assert.Equal(3, catalog.DeleteCallCount);
    }

    /// <summary>
    /// Delete acts now; X only arms. Keeping them independent matters because a Delete key that also rearmed
    /// the commit mode would take a second clip when Ctrl came up.
    /// </summary>
    [Fact]
    public void Delete_does_not_change_what_releasing_Ctrl_will_do()
    {
        var (controller, catalog, host) = Build(clipCount: 3);

        controller.Begin();
        controller.Handle(PasteAction.DeleteCurrentClip);

        Assert.Equal(PasteCommitMode.Paste, controller.CommitMode);

        var remaining = catalog.Snapshot().Count;
        controller.ModifierReleased();

        Assert.Single(host.PastedClips);
        Assert.Equal(remaining, catalog.Snapshot().Count);
    }

    [Fact]
    public void Help_ends_the_session_before_showing_the_card()
    {
        var (controller, _, host) = Build();

        controller.Begin();

        var result = controller.Handle(PasteAction.Help);

        Assert.Equal(PasteCommitKind.Cancelled, result);
        Assert.False(controller.IsActive);
        Assert.False(host.OverlayVisible);
        Assert.Equal(1, host.HelpCount);

        // Ordering, not just the calls: the clipboard has to be back and the overlay down before a window that
        // takes focus appears, or the user's next keystroke lands somewhere neither of us intended.
        Assert.Equal(
            ["snapshot", "show", "restore", "hide", "help"],
            host.Calls);
    }

    /// <summary>Invariant 2 still holds for the route out through Help: the clipboard goes back.</summary>
    [Fact]
    public void Help_restores_the_clipboard()
    {
        var (controller, _, host) = Build();

        controller.Begin();
        controller.Handle(PasteAction.Help);

        Assert.Equal(1, host.RestoreCount);
    }

    [Fact]
    public void Keys_after_Help_are_ignored_because_the_session_has_gone()
    {
        var (controller, catalog, host) = Build();

        controller.Begin();
        controller.Handle(PasteAction.Help);

        controller.Handle(PasteAction.DeleteCurrentClip);
        controller.Handle(PasteAction.Advance);

        Assert.Equal(0, catalog.DeleteCallCount);
        Assert.Equal(1, host.HelpCount);
    }
}
