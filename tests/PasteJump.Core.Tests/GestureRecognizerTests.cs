using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Swallow/pass-through behaviour. Every key consumed here is a key the foreground app never
/// sees, so these tests guard against breaking typing system-wide.
/// </summary>
public class GestureRecognizerTests
{
    private static (PasteGestureRecognizer Recognizer, PasteModeController Controller, RecordingPasteModeHost Host)
        Build(int clipCount = 3)
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

        return (new PasteGestureRecognizer(controller), controller, host);
    }

    [Fact]
    public void ModifierItself_IsNeverSwallowed()
    {
        var (recognizer, _, _) = Build();

        Assert.False(recognizer.Handle(GestureKey.Control, isDown: true));
        Assert.False(recognizer.Handle(GestureKey.Control, isDown: false));
    }

    [Fact]
    public void ShiftIsObservedButNeverSwallowed()
    {
        var (recognizer, controller, _) = Build();

        Assert.False(recognizer.Handle(GestureKey.Shift, isDown: true));
        Assert.True(controller.ShiftHeld);

        Assert.False(recognizer.Handle(GestureKey.Shift, isDown: false));
        Assert.False(controller.ShiftHeld);
    }

    [Fact]
    public void CtrlV_OpensSessionAndSwallowsTheKey()
    {
        var (recognizer, controller, _) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        var swallowed = recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.True(swallowed);
        Assert.True(controller.IsActive);
    }

    /// <summary>
    /// Ctrl+Shift+V belongs to the application. Every terminal pastes with it and browsers use it to paste as
    /// plain text, so swallowing the V substitutes our paste for theirs - and because Shift also means "pop",
    /// it deleted the clip on the way. Reported from a Visual Studio terminal as Ctrl+Shift+V having stopped
    /// working.
    /// </summary>
    [Fact]
    public void CtrlShiftV_IsPassedThroughAndOpensNothing()
    {
        var (recognizer, controller, host) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Shift, isDown: true);

        var swallowed = recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.False(swallowed);
        Assert.False(controller.IsActive);

        // And nothing was pasted on our behalf either - the application handles the chord itself.
        Assert.Empty(host.PastedClips);
    }

    /// <summary>
    /// Paste popping survives, reached the way the key list describes it: Shift pressed once the gesture is
    /// already open. Giving up Ctrl+Shift+V as an entry point costs nothing here.
    /// </summary>
    [Fact]
    public void ShiftAfterEntry_StillPopsTheClip()
    {
        // Built here rather than through the helper, so the catalog is in reach to assert the deletion.
        var catalog = new FakeClipCatalog();
        catalog.Add("clip 1");
        catalog.Add("clip 2");
        catalog.Add("clip 3");

        var host = new RecordingPasteModeHost();
        var controller = new PasteModeController(
            catalog, host, new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });

        var recognizer = new PasteGestureRecognizer(controller);

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.True(controller.IsActive);

        recognizer.Handle(GestureKey.Shift, isDown: true);
        recognizer.Handle(GestureKey.Control, isDown: false);

        Assert.Single(host.PastedClips);
        Assert.Equal(2, catalog.Snapshot().Count);
        Assert.Equal(1, catalog.DeleteCallCount);
    }

    [Fact]
    public void PasteKeyWithoutModifier_IsPassedThrough()
    {
        var (recognizer, controller, _) = Build();

        // Typing a plain "v" must never open paste mode or be eaten.
        var swallowed = recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.False(swallowed);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public void ActionKeysAreIgnoredWhenNoSessionIsOpen()
    {
        var (recognizer, _, _) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);

        // Ctrl+X, Ctrl+C, Ctrl+A and friends belong to the app while we are idle.
        Assert.False(recognizer.Handle(GestureKey.CycleCommitMode, isDown: true));
        Assert.False(recognizer.Handle(GestureKey.Back, isDown: true));
        Assert.False(recognizer.Handle(GestureKey.JumpToNewest, isDown: true));
        Assert.False(recognizer.Handle(GestureKey.CycleFormatter, isDown: true));
    }

    [Fact]
    public void SecondPasteTap_AdvancesWithinTheSameSession()
    {
        var (recognizer, controller, _) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: false);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.Equal(1, controller.CursorIndex);
    }

    [Fact]
    public void KeyUpIsSwallowedOnlyWhenItsKeyDownWas()
    {
        var (recognizer, _, _) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);

        // Down swallowed, so up must be too - otherwise the app sees a release for a press it
        // never received.
        Assert.True(recognizer.Handle(GestureKey.Paste, isDown: true));
        Assert.True(recognizer.Handle(GestureKey.Paste, isDown: false));

        // A key we never swallowed on the way down passes through on the way up.
        Assert.False(recognizer.Handle(GestureKey.EditClip, isDown: false));
    }

    [Fact]
    public void ReleasingModifier_CommitsAndReportsOutcome()
    {
        var (recognizer, _, host) = Build();
        PasteCommitKind? reported = null;
        recognizer.Committed += k => reported = k;

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: false);
        recognizer.Handle(GestureKey.Control, isDown: false);

        Assert.Equal(PasteCommitKind.Pasted, reported);
        Assert.Single(host.PastedClips);
    }

    [Fact]
    public void EmptyStore_SwallowsTheKeyButReportsPassThrough()
    {
        var (recognizer, _, host) = Build(clipCount: 0);
        PasteCommitKind? reported = null;
        recognizer.Committed += k => reported = k;

        recognizer.Handle(GestureKey.Control, isDown: true);
        var swallowed = recognizer.Handle(GestureKey.Paste, isDown: true);

        // The host already synthesised the paste, so letting the original through as well would
        // paste twice.
        Assert.True(swallowed);
        Assert.Equal(PasteCommitKind.PassedThrough, reported);
        Assert.Equal(1, host.PassThroughCount);
    }

    [Fact]
    public void DigitKeys_JumpWhileSessionIsOpen()
    {
        var (recognizer, controller, _) = Build(clipCount: 9);

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        var swallowed = recognizer.Handle(GestureKey.Digit3, isDown: true);

        Assert.True(swallowed);
        Assert.Equal(3, controller.CursorIndex);
    }

    [Fact]
    public void DigitKeys_ArePassedThroughWhenIdle()
    {
        var (recognizer, _, _) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);

        Assert.False(recognizer.Handle(GestureKey.Digit3, isDown: true));
    }

    // ---- search mode

    [Fact]
    public void SearchMode_TypedCharactersBuildTheQuery()
    {
        var catalog = new FakeClipCatalog();
        catalog.Add("alpha");
        catalog.Add("beta");

        var host = new RecordingPasteModeHost();
        var controller = new PasteModeController(
            catalog, host, new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });
        var recognizer = new PasteGestureRecognizer(controller);

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.ToggleSearch, isDown: true);

        Assert.True(recognizer.HandleCharacter('a'));
        Assert.True(recognizer.HandleCharacter('l'));

        Assert.Equal("al", controller.SearchQuery);
        Assert.Single(controller.Window);
    }

    [Fact]
    public void CharactersAreNotConsumedOutsideSearchMode()
    {
        var (recognizer, _, _) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        // Browsing, not searching: ordinary characters belong to nobody and must not be eaten.
        Assert.False(recognizer.HandleCharacter('a'));
    }

    [Fact]
    public void SearchMode_BackspaceTrimsTheQuery()
    {
        var (recognizer, controller, _) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.ToggleSearch, isDown: true);

        recognizer.HandleCharacter('c');
        recognizer.HandleCharacter('l');
        Assert.True(recognizer.Handle(GestureKey.Backspace, isDown: true));

        Assert.Equal("c", controller.SearchQuery);
    }

    [Fact]
    public void SearchMode_ReleasingModifierDoesNotCommit()
    {
        var (recognizer, controller, host) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.ToggleSearch, isDown: true);
        recognizer.Handle(GestureKey.Control, isDown: false);

        Assert.True(controller.IsActive);
        Assert.Empty(host.PastedClips);
    }

    [Fact]
    public void SearchMode_EnterCommits()
    {
        var (recognizer, controller, host) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.ToggleSearch, isDown: true);
        recognizer.Handle(GestureKey.Control, isDown: false);

        var swallowed = recognizer.Handle(GestureKey.Commit, isDown: true);

        Assert.True(swallowed);
        Assert.Single(host.PastedClips);
        Assert.False(controller.IsActive);
    }

    [Fact]
    public void SearchMode_EscapeCancels()
    {
        var (recognizer, controller, host) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.ToggleSearch, isDown: true);
        recognizer.Handle(GestureKey.Control, isDown: false);

        Assert.True(recognizer.Handle(GestureKey.Escape, isDown: true));
        Assert.False(controller.IsActive);
        Assert.Empty(host.PastedClips);
        Assert.Equal(1, host.RestoreCount);
    }

    [Fact]
    public void Reset_AbortsAnyOpenSession()
    {
        var (recognizer, controller, host) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        recognizer.Reset();

        Assert.False(controller.IsActive);
        Assert.Equal(1, host.RestoreCount);
        Assert.False(recognizer.IsControlDown);
    }
}
