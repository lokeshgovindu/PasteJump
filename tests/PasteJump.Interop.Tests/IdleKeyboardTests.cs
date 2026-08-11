using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;
using PasteJump.Interop.Tests.Fakes;
using Xunit;

namespace PasteJump.Interop.Tests;

/// <summary>
/// The single most dangerous property in the application: which keystrokes it takes away from everyone else.
/// <para>
/// The hook sees every key on the machine, and a swallowed key is one the focused application never receives.
/// So the rule is narrow - with no session open, PasteJump must consume exactly one chord and no other. These
/// tests drive the real key table and the real recogniser together, which is the seam where
/// <c>Ctrl+Right</c> was lost: the table said "step to an older clip", the recogniser read the same value as
/// "open a session", and neither component was wrong on its own.
/// </para>
/// </summary>
public class IdleKeyboardTests
{
    private const int VkV = 0x56;

    private static (PasteGestureRecognizer Recognizer, StubHost Host) Build()
    {
        var host = new StubHost();

        var controller = new PasteModeController(
            new StubCatalog(3),
            host,
            new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });

        return (new PasteGestureRecognizer(controller), host);
    }

    /// <summary>
    /// Every virtual key on the keyboard, with Ctrl held and no session open. Exactly one may be swallowed.
    /// <para>
    /// A fresh recogniser per key, because a key that wrongly opened a session would make every key after it
    /// swallowed too and the failure would name the wrong culprit.
    /// </para>
    /// </summary>
    [Fact]
    public void With_no_session_open_only_the_trigger_chord_is_swallowed()
    {
        var swallowed = new List<int>();

        for (var vk = 0; vk < 256; vk++)
        {
            var (recognizer, _) = Build();
            recognizer.Handle(GestureKey.Control, isDown: true);

            if (recognizer.Handle(VirtualKeyTranslator.ToGestureKey(vk, VkV), isDown: true))
            {
                swallowed.Add(vk);
            }
        }

        Assert.Equal([VkV], swallowed);
    }

    /// <summary>
    /// And it holds for the user's own bindings, not only the defaults. This is the promise that had to survive
    /// making the letters configurable: whatever anyone binds, an idle PasteJump consumes one chord.
    /// <para>
    /// The maps here are deliberately awkward - letters moved onto each other's old keys, actions switched off,
    /// and a reconfigured trigger - because the interesting failure is a binding table that claims a letter it
    /// should have released.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("pin=J;format=;tags=Z", 0x56)]                  // moved, switched off, trigger V
    [InlineData("back=;newest=;search=;pin=;front=", 0x56)]     // half of them off
    [InlineData("history=V", 0x42)]                             // V is free because the trigger moved to B
    public void The_promise_holds_for_any_bindings(string stored, int triggerVk)
    {
        var map = PasteKeyMap.Parse(stored);
        var swallowed = new List<int>();

        for (var vk = 0; vk < 256; vk++)
        {
            var (recognizer, _) = Build();
            recognizer.Handle(GestureKey.Control, isDown: true);

            if (recognizer.Handle(VirtualKeyTranslator.ToGestureKey(vk, triggerVk, map), isDown: true))
            {
                swallowed.Add(vk);
            }
        }

        Assert.Equal([triggerVk], swallowed);
    }

    /// <summary>
    /// A letter switched off must fall through to nothing, so it reaches the search box like any unbound letter.
    /// Its fixed alias is unaffected - that is what makes switching one off safe rather than lossy.
    /// </summary>
    [Fact]
    public void A_letter_switched_off_stops_firing_its_action()
    {
        var map = PasteKeyMap.Parse("pin=");

        Assert.Equal(GestureKey.None, VirtualKeyTranslator.ToGestureKey(0x50, VkV, map));      // VK_P
        Assert.Equal(GestureKey.TogglePin, VirtualKeyTranslator.ToGestureKey(0x20, VkV, map)); // VK_SPACE
    }

    /// <summary>A moved letter fires at its new home and no longer at its old one.</summary>
    [Fact]
    public void A_moved_letter_fires_where_it_was_moved_to()
    {
        var map = PasteKeyMap.Parse("tags=J");

        Assert.Equal(GestureKey.EditTags, VirtualKeyTranslator.ToGestureKey(0x4A, VkV, map)); // VK_J
        Assert.Equal(GestureKey.None, VirtualKeyTranslator.ToGestureKey(0x54, VkV, map));     // VK_T
    }

    /// <summary>And no key opens a session either, which is the other half of the same promise.</summary>
    [Fact]
    public void With_no_session_open_only_the_trigger_chord_starts_one()
    {
        var openers = new List<int>();

        for (var vk = 0; vk < 256; vk++)
        {
            var (recognizer, host) = Build();
            recognizer.Handle(GestureKey.Control, isDown: true);
            recognizer.Handle(VirtualKeyTranslator.ToGestureKey(vk, VkV), isDown: true);

            if (recognizer.IsSessionActive || host.OverlayVisible)
            {
                openers.Add(vk);
            }
        }

        Assert.Equal([VkV], openers);
    }

    /// <summary>
    /// Without Ctrl held, nothing at all is ours - not even the trigger. This is what stops an ordinary V from
    /// being eaten while typing.
    /// </summary>
    [Fact]
    public void Without_Ctrl_nothing_is_swallowed()
    {
        var swallowed = new List<int>();

        for (var vk = 0; vk < 256; vk++)
        {
            var (recognizer, _) = Build();

            if (recognizer.Handle(VirtualKeyTranslator.ToGestureKey(vk, VkV), isDown: true))
            {
                swallowed.Add(vk);
            }
        }

        Assert.Empty(swallowed);
    }

    /// <summary>
    /// With Alt, Win or Shift also held, the trigger itself is refused - AltGr is Ctrl+Alt on many layouts,
    /// Win chords belong to the shell, and Ctrl+Shift+V is how terminals paste. Swept over the whole keyboard
    /// rather than just the trigger, because the gate is meant to cover every key and not only the entry one.
    /// </summary>
    [Theory]
    [InlineData("alt")]
    [InlineData("win")]
    [InlineData("shift")]
    public void With_another_modifier_held_nothing_is_swallowed(string modifier)
    {
        var swallowed = new List<int>();

        for (var vk = 0; vk < 256; vk++)
        {
            var (recognizer, _) = Build();

            switch (modifier)
            {
                case "alt": recognizer.AltHeld = true; break;
                case "win": recognizer.WinHeld = true; break;
                default: recognizer.ShiftHeld = true; break;
            }

            recognizer.Handle(GestureKey.Control, isDown: true);

            if (recognizer.Handle(VirtualKeyTranslator.ToGestureKey(vk, VkV), isDown: true))
            {
                swallowed.Add(vk);
            }
        }

        Assert.Empty(swallowed);
    }

    /// <summary>
    /// Once a session IS open the gesture owns the keyboard, and that is deliberate: the user is holding Ctrl,
    /// so almost every unclaimed chord is a command somewhere - Ctrl+S saves, Ctrl+W closes a tab. The exception
    /// is Alt and Win chords, which are the way out if a session ever fails to close.
    /// </summary>
    [Fact]
    public void With_a_session_open_the_gesture_claims_the_keyboard()
    {
        var (recognizer, _) = Build();
        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);

        Assert.True(recognizer.IsSessionActive);
        Assert.True(recognizer.ShouldSwallowUnhandled());

        recognizer.AltHeld = true;
        Assert.False(recognizer.ShouldSwallowUnhandled());

        recognizer.AltHeld = false;
        recognizer.WinHeld = true;
        Assert.False(recognizer.ShouldSwallowUnhandled());
    }
}
