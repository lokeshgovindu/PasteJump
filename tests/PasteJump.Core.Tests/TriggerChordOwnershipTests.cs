using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Which chords open the gesture, and which are left alone.
/// <para>
/// The rule is exact rather than "Ctrl is somewhere in the mix": <c>Ctrl</c>+the trigger and nothing else.
/// Every additional modifier belongs to somebody: <c>Ctrl+Shift+V</c> is how terminals paste and how browsers
/// paste as plain text, <c>Ctrl+Alt+V</c> is <c>AltGr</c>+V on a great many keyboard layouts, and Win chords
/// belong to the shell. Claiming one swallows the keystroke, so the application never receives the chord it
/// owns - and the user gets a clip pasted instead of what they asked for.
/// </para>
/// <para>
/// Alt and Win were reported as opening the gesture, and they were: entry checked Ctrl and Shift and nothing
/// else, while <see cref="PasteGestureRecognizer.ShouldSwallowUnhandled"/> had always let Alt and Win chords
/// through. The two halves disagreed.
/// </para>
/// </summary>
public sealed class TriggerChordOwnershipTests
{
    private static (PasteGestureRecognizer Recognizer, PasteModeController Controller) Build()
    {
        var catalog = new FakeClipCatalog();
        catalog.Add("a clip to paste");

        var controller = new PasteModeController(
            catalog,
            new RecordingPasteModeHost(),
            new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });

        return (new PasteGestureRecognizer(controller), controller);
    }

    [Fact]
    public void Ctrl_and_the_trigger_opens_the_gesture()
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);

        Assert.True(recognizer.Handle(GestureKey.Paste, isDown: true), "the trigger should be swallowed");
        Assert.True(controller.IsActive);
    }

    /// <summary>
    /// The reported bug. Alt held at the trigger must leave the chord entirely alone - not opened, and not
    /// swallowed, so whatever owns it still receives it.
    /// </summary>
    [Fact]
    public void Ctrl_Alt_and_the_trigger_is_left_alone()
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.AltHeld = true;

        Assert.False(recognizer.Handle(GestureKey.Paste, isDown: true), "the trigger must not be swallowed");
        Assert.False(controller.IsActive);
    }

    [Fact]
    public void Ctrl_Win_and_the_trigger_is_left_alone()
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.WinHeld = true;

        Assert.False(recognizer.Handle(GestureKey.Paste, isDown: true), "the trigger must not be swallowed");
        Assert.False(controller.IsActive);
    }

    /// <summary>Already covered elsewhere, restated here so the whole rule reads in one place.</summary>
    [Fact]
    public void Ctrl_Shift_and_the_trigger_is_left_alone()
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Shift, isDown: true);

        Assert.False(recognizer.Handle(GestureKey.Paste, isDown: true), "the trigger must not be swallowed");
        Assert.False(controller.IsActive);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void Any_extra_modifier_declines(bool shift, bool alt, bool win)
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);

        if (shift)
        {
            recognizer.Handle(GestureKey.Shift, isDown: true);
        }

        recognizer.AltHeld = alt;
        recognizer.WinHeld = win;

        Assert.False(recognizer.Handle(GestureKey.Paste, isDown: true));
        Assert.False(controller.IsActive);
    }

    /// <summary>
    /// And releasing the extra modifier restores the plain chord. This is what makes querying the live keyboard
    /// state rather than tracking transitions worth the trouble: a flag left stuck by a missed key-up would
    /// leave the gesture permanently refusing to open.
    /// </summary>
    [Fact]
    public void Releasing_the_extra_modifier_restores_the_gesture()
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.AltHeld = true;

        Assert.False(recognizer.Handle(GestureKey.Paste, isDown: true));

        recognizer.AltHeld = false;

        Assert.True(recognizer.Handle(GestureKey.Paste, isDown: true));
        Assert.True(controller.IsActive);
    }

    /// <summary>
    /// The trigger alone, with no Ctrl, is ordinary typing and must never be touched.
    /// </summary>
    [Fact]
    public void The_trigger_without_Ctrl_is_ordinary_typing()
    {
        var (recognizer, controller) = Build();

        Assert.False(recognizer.Handle(GestureKey.Paste, isDown: true));
        Assert.False(controller.IsActive);
    }

    /// <summary>
    /// Alt pressed <em>after</em> the gesture is open does not close or break it - it only stops us swallowing
    /// the shell's chords, which is what keeps Alt+Tab working mid-gesture.
    /// </summary>
    [Fact]
    public void Alt_pressed_after_entry_leaves_the_session_open()
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        recognizer.AltHeld = true;

        Assert.True(controller.IsActive);
        Assert.False(recognizer.ShouldSwallowUnhandled());
    }

    /// <summary>
    /// The reported follow-up, and the case gating entry alone missed entirely: with a session already open,
    /// the trigger fell through to the step action, so <c>Ctrl+Win+V</c> walked the stack and releasing Ctrl
    /// pasted. The first chord was refused and every one after it honoured.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void An_extra_modifier_is_refused_inside_an_open_session(bool alt, bool win)
    {
        var (recognizer, controller) = Build2();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        var startedOn = controller.CursorIndex;

        recognizer.AltHeld = alt;
        recognizer.WinHeld = win;

        Assert.False(recognizer.Handle(GestureKey.Paste, isDown: true), "the trigger must not be swallowed");
        Assert.Equal(startedOn, controller.CursorIndex);
    }

    /// <summary>And no other paste-mode key acts either, since the gate covers all of them rather than one.</summary>
    [Fact]
    public void No_paste_mode_key_acts_while_Win_is_held()
    {
        var (recognizer, controller) = Build2();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.WinHeld = true;

        foreach (var key in new[] { GestureKey.Back, GestureKey.JumpToNewest, GestureKey.CycleCommitMode })
        {
            Assert.False(recognizer.Handle(key, isDown: true), $"{key} must not be swallowed");
        }

        Assert.Equal(PasteCommitMode.Paste, controller.CommitMode);
    }

    /// <summary>
    /// Releasing the extra modifier hands the keys back. Worth pinning down because the state is queried
    /// rather than tracked - the whole point being that it cannot get stuck.
    /// </summary>
    [Fact]
    public void Releasing_the_modifier_hands_the_keys_back_mid_session()
    {
        var (recognizer, controller) = Build2();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        recognizer.WinHeld = true;
        Assert.False(recognizer.Handle(GestureKey.Paste, isDown: true));

        recognizer.WinHeld = false;

        Assert.True(recognizer.Handle(GestureKey.Paste, isDown: true));
        Assert.Equal(1, controller.CursorIndex);
    }

    /// <summary>
    /// The risk the gate introduces, and the reason the Ctrl release is deliberately outside it: letting go of
    /// Ctrl while Alt happens to be held must still commit. If it did not, the session would stay open with no
    /// way to close it - a live keyboard hook swallowing keys for ever.
    /// </summary>
    [Fact]
    public void Releasing_Ctrl_still_commits_even_with_Alt_held()
    {
        var (recognizer, controller) = Build2();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.True(controller.IsActive);

        recognizer.AltHeld = true;
        recognizer.Handle(GestureKey.Control, isDown: false);

        Assert.False(controller.IsActive);
        Assert.False(recognizer.ShouldSwallowUnhandled());
    }

    /// <summary>Two clips, so stepping has somewhere to go and a refused step is visible as a cursor that did not move.</summary>
    private static (PasteGestureRecognizer Recognizer, PasteModeController Controller) Build2()
    {
        var catalog = new FakeClipCatalog();
        catalog.Add("older clip");
        catalog.Add("newer clip");

        var controller = new PasteModeController(
            catalog,
            new RecordingPasteModeHost(),
            new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });

        return (new PasteGestureRecognizer(controller), controller);
    }
}
