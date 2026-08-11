using PasteJump.Core.PasteMode;
using PasteJump.Interop;
using Xunit;

namespace PasteJump.Interop.Tests;

/// <summary>
/// The paste-mode key table. Pure lookup, no Win32, and it had no tests until an arrow key was aliased onto
/// the entry trigger.
/// <para>
/// Virtual keys are written as literals rather than taken from <c>NativeConstants</c>, which is internal to
/// Interop. That turns out to be the better test: these are Windows' numbers, published in WinUser.h, so
/// stating them independently means a typo in the constants table fails here too instead of agreeing with
/// itself. Each one carries its name in a comment.
/// </para>
/// </summary>
public class VirtualKeyTranslatorTests
{
    private const int VkV = 0x56;
    private const int VkB = 0x42;
    private const int VkF1 = 0x70;

    /// <summary>
    /// The one that matters. <see cref="GestureKey.Paste"/> is the key that <em>opens</em> a session, and it
    /// opens one by swallowing the keystroke - so anything else mapped to it silently claims that chord
    /// machine-wide and hides it from the focused application.
    /// <para>
    /// Ctrl+Right and Ctrl+Down were claimed exactly this way: the arrows were mapped to Paste, so pressing
    /// Ctrl+Right in an editor opened the overlay, ate the keystroke, and pasted a clip on release instead of
    /// moving the caret by a word. Swept over every virtual key rather than asserted per key, so a new binding
    /// cannot slip past by being one nobody thought to test.
    /// </para>
    /// </summary>
    [Fact]
    public void The_trigger_is_the_only_key_that_can_open_a_session()
    {
        var openers = Enumerable.Range(0, 256)
            .Where(vk => VirtualKeyTranslator.ToGestureKey(vk, VkV) == GestureKey.Paste)
            .ToList();

        Assert.Equal([VkV], openers);
    }

    /// <summary>And the same holds when the trigger has been reconfigured.</summary>
    [Theory]
    [InlineData(VkB)]
    [InlineData(0x4A)] // J
    public void A_reconfigured_trigger_is_still_the_only_opener(int triggerVk)
    {
        var openers = Enumerable.Range(0, 256)
            .Where(vk => VirtualKeyTranslator.ToGestureKey(vk, triggerVk) == GestureKey.Paste)
            .ToList();

        Assert.Equal([triggerVk], openers);
    }

    [Theory]
    [InlineData(0x28, GestureKey.StepOlder)]   // VK_DOWN
    [InlineData(0x27, GestureKey.StepOlder)]   // VK_RIGHT
    [InlineData(0x26, GestureKey.Back)]        // VK_UP
    [InlineData(0x25, GestureKey.Back)]        // VK_LEFT
    [InlineData(0x24, GestureKey.JumpToNewest)] // VK_HOME
    [InlineData(0x23, GestureKey.JumpToOldest)] // VK_END
    [InlineData(0x2E, GestureKey.DeleteCurrent)] // VK_DELETE
    [InlineData(0x1B, GestureKey.Escape)]      // VK_ESCAPE
    [InlineData(0x0D, GestureKey.Commit)]      // VK_RETURN
    [InlineData(0x20, GestureKey.TogglePin)]   // VK_SPACE
    [InlineData(0x70, GestureKey.Help)]        // VK_F1
    public void The_physical_keys_map_where_the_help_says_they_do(int virtualKey, GestureKey expected)
        => Assert.Equal(expected, VirtualKeyTranslator.ToGestureKey(virtualKey, VkV));

    /// <summary>
    /// Home used to be a second Escape. It is now "jump to the newest clip", and Escape must still be Escape -
    /// cancelling is the way out of a session and losing it would be far worse than the change that freed Home.
    /// </summary>
    [Fact]
    public void Home_no_longer_cancels_and_Escape_still_does()
    {
        Assert.Equal(GestureKey.JumpToNewest, VirtualKeyTranslator.ToGestureKey(0x24, VkV)); // VK_HOME
        Assert.Equal(GestureKey.Escape, VirtualKeyTranslator.ToGestureKey(0x1B, VkV));       // VK_ESCAPE
    }

    /// <summary>Both letters open the clip in an editor: O is the one the help names, H is the original.</summary>
    [Theory]
    [InlineData(0x4F)] // VK_O
    [InlineData(0x48)] // VK_H
    public void O_and_H_both_open_the_editor(int virtualKey)
        => Assert.Equal(GestureKey.EditClip, VirtualKeyTranslator.ToGestureKey(virtualKey, VkV));

    /// <summary>
    /// The invariant CLAUDE.md says has to be maintained by hand, checked instead: every letter bound to an
    /// action must be listed in <c>TriggerKey.Reserved</c>, or that letter can be chosen as the trigger and will
    /// shadow the action for ever. This is what catches an alias added on one side only.
    /// </summary>
    [Fact]
    public void Every_bound_letter_is_reserved_against_being_chosen_as_the_trigger()
    {
        var boundButOffered = BoundLetters().Where(TriggerKey.IsAvailable).ToList();

        Assert.Empty(boundButOffered);
    }

    /// <summary>
    /// And the other direction: a letter withheld from the trigger list must really be bound to something, or
    /// the list is quietly shrinking the user's choices for nothing.
    /// </summary>
    [Fact]
    public void Every_reserved_letter_is_really_bound()
    {
        var reservedButUnbound = Enumerable.Range('A', 26)
            .Select(static c => (char)c)
            .Where(static letter => !TriggerKey.IsAvailable(letter))
            .Except(BoundLetters())
            .ToList();

        Assert.Empty(reservedButUnbound);
    }

    /// <summary>
    /// Letters bound to an action. Translated with F1 as the trigger - deliberately not a letter - so the
    /// trigger check inside <c>ToGestureKey</c> cannot mask a real binding or invent one.
    /// </summary>
    private static IEnumerable<char> BoundLetters() => Enumerable.Range('A', 26)
        .Select(static c => (char)c)
        .Where(static letter => VirtualKeyTranslator.ToGestureKey(letter, VkF1) != GestureKey.None);

    /// <summary>
    /// V is absent from the table on purpose. When it is not the trigger it must fall through to
    /// <see cref="GestureKey.None"/> so it can be typed into the search box like any other unbound letter.
    /// </summary>
    [Fact]
    public void V_is_unbound_when_it_is_not_the_trigger()
        => Assert.Equal(GestureKey.None, VirtualKeyTranslator.ToGestureKey(VkV, VkB));

    [Theory]
    [InlineData(0x31, GestureKey.Digit1)]
    [InlineData(0x39, GestureKey.Digit9)]
    [InlineData(0x61, GestureKey.Digit1)] // numpad 1
    [InlineData(0x69, GestureKey.Digit9)] // numpad 9
    public void Digits_map_from_both_rows(int virtualKey, GestureKey expected)
        => Assert.Equal(expected, VirtualKeyTranslator.ToGestureKey(virtualKey, VkV));

    /// <summary>
    /// Modifiers are never swallowed, whatever else is happening: the foreground application tracks them, and
    /// eating a release leaves it believing a modifier is still down. The left/right variants are in the list
    /// because a low-level hook reports those rather than the generic code.
    /// </summary>
    [Theory]
    [InlineData(0x11)] // VK_CONTROL
    [InlineData(0xA2)] // VK_LCONTROL
    [InlineData(0xA3)] // VK_RCONTROL
    [InlineData(0x10)] // VK_SHIFT
    [InlineData(0xA0)] // VK_LSHIFT
    [InlineData(0xA1)] // VK_RSHIFT
    [InlineData(0x12)] // VK_MENU
    [InlineData(0xA4)] // VK_LMENU
    [InlineData(0xA5)] // VK_RMENU
    [InlineData(0x5B)] // VK_LWIN
    [InlineData(0x5C)] // VK_RWIN
    [InlineData(0x14)] // VK_CAPITAL
    public void Modifiers_are_recognised_as_modifiers(int virtualKey)
        => Assert.True(VirtualKeyTranslator.IsModifier(virtualKey));
}
