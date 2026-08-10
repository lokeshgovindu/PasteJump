using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// What happens to a keystroke no paste-mode action claimed, while the overlay is open.
/// <para>
/// It has to be swallowed. The user is holding Ctrl, and almost every <c>Ctrl</c>+key in every application is
/// a command - reported as VS Code zooming from <c>Ctrl+0</c> and <c>Ctrl+=</c> while browsing clips, and
/// <c>Ctrl+W</c> would have closed the tab. But swallowing must stop at the modifiers and at the chords the
/// shell owns, or a session that failed to close would look like a dead keyboard.
/// </para>
/// </summary>
public sealed class UnhandledKeySwallowTests
{
    private static (PasteGestureRecognizer Recognizer, PasteModeController Controller) Build(int clips = 3)
    {
        var catalog = new FakeClipCatalog();

        for (var i = 1; i <= clips; i++)
        {
            catalog.Add($"clip {i}");
        }

        var controller = new PasteModeController(
            catalog,
            new RecordingPasteModeHost(),
            new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });

        return (new PasteGestureRecognizer(controller), controller);
    }

    /// <summary>
    /// The whole point: with no session open, an unclaimed key is none of our business. Swallowing here would
    /// break typing machine-wide, which is the worst thing this application could do.
    /// </summary>
    [Fact]
    public void With_no_session_open_nothing_is_swallowed()
    {
        var (recognizer, _) = Build();

        Assert.False(recognizer.ShouldSwallowUnhandled(altHeld: false, winHeld: false));
    }

    [Fact]
    public void With_a_session_open_an_unclaimed_key_is_swallowed()
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.True(controller.IsActive);
        Assert.True(recognizer.ShouldSwallowUnhandled(altHeld: false, winHeld: false));
    }

    /// <summary>
    /// The escape hatch. Alt+Tab has to keep working while a session is open - both because the user may want
    /// to switch away mid-gesture, and because it is the way out if a session ever fails to close.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_shell_chord_is_never_swallowed(bool alt, bool win)
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.True(controller.IsActive);
        Assert.False(recognizer.ShouldSwallowUnhandled(alt, win));
    }

    /// <summary>
    /// And it stops the moment the session does. Releasing Ctrl commits, so the swallow window is bounded by
    /// the user's own finger - which is what makes this safe to do at all.
    /// </summary>
    [Fact]
    public void Swallowing_stops_when_the_session_ends()
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.Control, isDown: false);

        Assert.False(controller.IsActive);
        Assert.False(recognizer.ShouldSwallowUnhandled(altHeld: false, winHeld: false));
    }

    /// <summary>
    /// Search mode is the one state that outlives the Ctrl hold, and stray keys must not leak there either -
    /// the user is typing a query, not driving the application underneath.
    /// </summary>
    [Fact]
    public void While_searching_with_Ctrl_released_keys_are_still_swallowed()
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.ToggleSearch, isDown: true);
        recognizer.Handle(GestureKey.Control, isDown: false);

        Assert.Equal(PasteSessionState.Searching, controller.State);
        Assert.True(controller.IsActive);
        Assert.True(recognizer.ShouldSwallowUnhandled(altHeld: false, winHeld: false));
    }

    /// <summary>
    /// Aborting must release the keyboard too. Esc is the user saying "stop", and a recognizer that kept
    /// swallowing afterwards would be the dead-keyboard failure.
    /// </summary>
    [Fact]
    public void Aborting_releases_the_keyboard()
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.Escape, isDown: true);

        Assert.False(controller.IsActive);
        Assert.False(recognizer.ShouldSwallowUnhandled(altHeld: false, winHeld: false));
    }

    [Fact]
    public void Reset_releases_the_keyboard()
    {
        var (recognizer, controller) = Build();

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        recognizer.Reset();

        Assert.False(controller.IsActive);
        Assert.False(recognizer.ShouldSwallowUnhandled(altHeld: false, winHeld: false));
    }

    /// <summary>
    /// An empty store passes the trigger through and opens nothing, so it must not start swallowing either -
    /// otherwise Ctrl+V on a fresh install would break every other Ctrl chord until a clip existed.
    /// </summary>
    [Fact]
    public void An_empty_store_does_not_start_swallowing()
    {
        var (recognizer, controller) = Build(clips: 0);

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.False(controller.IsActive);
        Assert.False(recognizer.ShouldSwallowUnhandled(altHeld: false, winHeld: false));
    }
}
