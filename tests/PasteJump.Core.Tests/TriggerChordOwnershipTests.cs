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
}
