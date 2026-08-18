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
    /// <summary>
    /// A Ctrl release this application never saw must not leave the trigger key opening a session on its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported as "sometimes even press just v (without ctrl), I am seeing the PasteJump overlay" - the worst
    /// failure available to this application, since it takes an unmodified letter away from whatever is being
    /// typed into. The cause was that Ctrl was the one modifier still tracked purely from key transitions, so a
    /// key-up that never reached the hook left the flag stuck at true: the secure desktop taking over for
    /// Ctrl+Alt+Del or a UAC prompt, a lock or RDP session change, another hook suppressing it, or Windows
    /// dropping our hook for exceeding LowLevelHooksTimeout.
    /// </para>
    /// <para>
    /// The missed release is simulated the only honest way - by NOT sending the key-up, which is exactly what the
    /// hook experiences - and then telling the recogniser what the live keyboard says, as the host does on every
    /// keystroke.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_missed_Ctrl_release_does_not_let_the_trigger_open_a_session_alone()
    {
        var (recognizer, controller, _) = Build();

        // Ctrl down, then a full gesture, then Ctrl's key-up goes missing entirely.
        recognizer.CtrlHeld = true;
        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.True(controller.IsActive);

        // The next keystroke arrives with Ctrl genuinely up, which is all the host reports.
        recognizer.CtrlHeld = false;

        var swallowed = recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.False(swallowed);
        Assert.False(controller.IsActive);
        Assert.False(recognizer.IsControlDown);
        Assert.Equal(1, recognizer.MissedControlReleaseCount);
    }

    /// <summary>
    /// And a bare trigger key with no Ctrl at any point is never claimed - the same property, from the other end.
    /// </summary>
    [Fact]
    public void The_trigger_key_alone_is_never_swallowed()
    {
        var (recognizer, controller, _) = Build();

        Assert.False(recognizer.Handle(GestureKey.Paste, isDown: true));
        Assert.False(controller.IsActive);
    }

    /// <summary>
    /// A session whose Ctrl-up went missing is abandoned rather than pasted, and abandoning it hands the keyboard
    /// back.
    /// </summary>
    /// <remarks>
    /// Releasing Ctrl is what asks for a paste, so a release we merely inferred must not paste: this is precisely
    /// the case where the user's intent is unknown. Leaving the session open instead would be worse still - an
    /// overlay on screen, the hook swallowing every key, and no way to close it.
    /// </remarks>
    [Fact]
    public void A_session_whose_Ctrl_release_went_missing_is_abandoned_not_pasted()
    {
        var (recognizer, controller, host) = Build();

        recognizer.CtrlHeld = true;
        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.True(controller.IsActive);

        var pastesBefore = host.PastedClips.Count;

        // Any later keystroke, with the live keyboard saying Ctrl is up. Deliberately NOT Escape: that ends a
        // session by itself, so the first version of this test passed with the reconciliation removed - it proved
        // nothing. The trigger key is the honest choice, since with a session open it would otherwise step.
        recognizer.CtrlHeld = false;
        var swallowed = recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.False(controller.IsActive);
        Assert.False(swallowed);
        Assert.Equal(pastesBefore, host.PastedClips.Count);

        // And the keyboard is the application's again: nothing is swallowed once the session is gone.
        Assert.False(recognizer.ShouldSwallowUnhandled());
    }

}
