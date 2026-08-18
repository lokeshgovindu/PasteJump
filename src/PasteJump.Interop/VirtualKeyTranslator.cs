using System.Text;
using PasteJump.Core.PasteMode;
using PasteJump.Interop.Win32;

namespace PasteJump.Interop;

/// <summary>
/// Maps Windows virtual-key codes onto the platform-neutral <see cref="GestureKey"/> vocabulary,
/// and resolves printable characters for the search box.
/// </summary>
public static class VirtualKeyTranslator
{
    /// <summary>
    /// The key bindings: the original Clipjump layout, minus its channel keys (Up / Down / PitSwap) which have
    /// no meaning without channels, plus the physical navigation keys it never used.
    /// <para>
    /// Every Clipjump letter still does what it did. The additions are aliases or previously unbound keys, so
    /// a hand that learned the original layout is never wrong - see the arrow-key and Home notes below.
    /// </para>
    /// </summary>
    /// <param name="triggerVirtualKey">
    /// Virtual key of the configurable trigger - the key that opens a session and, once open, steps to an
    /// older clip. Checked before everything else so it always wins, and <c>V</c> is therefore <em>not</em>
    /// in the table below: when V is the trigger the first check catches it, and when it is not, V must
    /// fall through to <see cref="GestureKey.None"/> so it can be typed into the search box like any other
    /// unbound letter.
    /// </param>
    /// <param name="keyMap">
    /// The letter bindings, which the user can change. Null means the defaults. Physical keys are not in it and
    /// never move - see <see cref="PasteKeyMap"/> for why that is a safety property rather than a shortcut.
    /// </param>
    public static GestureKey ToGestureKey(int virtualKey, int triggerVirtualKey, PasteKeyMap? keyMap = null)
    {
        if (virtualKey == triggerVirtualKey)
        {
            return GestureKey.Paste;
        }

        // Letters first, from the map. Checked before the physical table so a letter the user has unbound falls
        // through to GestureKey.None and can be typed into the search box, exactly as an unbound letter always
        // could.
        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            return (keyMap ?? PasteKeyMap.Default).ForLetter((char)virtualKey);
        }

        return Map(virtualKey);
    }

    /// <summary>Bindings with the default <c>V</c> trigger, for the probe harness and for tests.</summary>
    public static GestureKey ToGestureKey(int virtualKey)
        => ToGestureKey(virtualKey, NativeConstants.VK_V);

    /// <summary>
    /// Whether this virtual key is a modifier rather than a key in its own right.
    /// <para>
    /// Modifiers are never swallowed, whatever else is going on. The foreground application tracks them, so
    /// consuming a Ctrl or Alt transition leaves it believing a modifier is still held after the user let go -
    /// which turns ordinary typing into a stream of commands. Caps Lock is in the list for the same reason:
    /// eating it desynchronises a state the whole desktop shares.
    /// </para>
    /// </summary>
    public static bool IsModifier(int virtualKey) => virtualKey switch
    {
        NativeConstants.VK_CONTROL or NativeConstants.VK_LCONTROL or NativeConstants.VK_RCONTROL => true,
        NativeConstants.VK_SHIFT or NativeConstants.VK_LSHIFT or NativeConstants.VK_RSHIFT => true,
        NativeConstants.VK_MENU or NativeConstants.VK_LMENU or NativeConstants.VK_RMENU => true,
        NativeConstants.VK_LWIN or NativeConstants.VK_RWIN => true,
        NativeConstants.VK_CAPITAL or NativeConstants.VK_NUMLOCK or NativeConstants.VK_SCROLL => true,
        _ => false,
    };

    /// <summary>Whether Alt is down right now, from the live keyboard state.</summary>
    public static bool IsAltDown() => IsDown(NativeConstants.VK_MENU);

    /// <summary>
    /// Whether Ctrl is down right now.
    /// <para>
    /// The gesture's entry condition, and the reason it is read rather than tracked: a Ctrl key-up that never
    /// reaches the hook - the secure desktop, a UAC prompt, a lock, an RDP session change, or our hook being
    /// dropped for exceeding <c>LowLevelHooksTimeout</c> - left the tracked flag stuck at true, and a stuck Ctrl
    /// makes the trigger key open a session on its own. Reported as the overlay appearing on a bare "v".
    /// </para>
    /// </summary>
    public static bool IsCtrlDown() => IsDown(NativeConstants.VK_CONTROL);

    /// <summary>
    /// Whether Shift is down right now.
    /// <para>
    /// Read live rather than tracked from transitions, and that is a fix rather than a preference: paste
    /// popping is armed by holding Shift, so a Shift key-up we never saw - which is what happens when focus
    /// changes mid-chord - used to leave pop armed and quietly delete a clip on every subsequent paste.
    /// </para>
    /// </summary>
    public static bool IsShiftDown() => IsDown(NativeConstants.VK_SHIFT);

    /// <summary>Whether either Windows key is down right now.</summary>
    public static bool IsWinDown()
        => IsDown(NativeConstants.VK_LWIN) || IsDown(NativeConstants.VK_RWIN);

    /// <summary>
    /// Asks the OS for one key's state. <c>GetAsyncKeyState</c> rather than <c>GetKeyState</c>: this runs inside
    /// the low-level hook, which is not processing a message queue of its own, and <c>GetKeyState</c> reports
    /// the state as of the last message that thread handled.
    /// </summary>
    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static GestureKey Map(int virtualKey) => virtualKey switch
    {
        NativeConstants.VK_CONTROL or NativeConstants.VK_LCONTROL or NativeConstants.VK_RCONTROL
            => GestureKey.Control,

        NativeConstants.VK_SHIFT or NativeConstants.VK_LSHIFT or NativeConstants.VK_RSHIFT
            => GestureKey.Shift,

        // No letters here any more - they come from PasteKeyMap, because the user can move them. What is left is
        // the fixed half of the layout: keys whose meaning is not in question and which no set of bindings can
        // take away, so a session can always be stepped through and always closed.
        NativeConstants.VK_SPACE => GestureKey.TogglePin,
        NativeConstants.VK_RETURN => GestureKey.Commit,
        NativeConstants.VK_F1 => GestureKey.Help,
        NativeConstants.VK_OEM_MINUS or NativeConstants.VK_SUBTRACT => GestureKey.ToggleJumpDirection,
        NativeConstants.VK_ESCAPE => GestureKey.Escape,
        NativeConstants.VK_BACK => GestureKey.Backspace,

        // The physical navigation keys, all additive. Down/Right and Up/Left step the same way as the trigger
        // and C, so a hand that already knows those loses nothing; they exist because stepping through a list
        // with an arrow key needs no learning at all. Clipjump binds Up and Down to its channel keys
        // (Clipjump.ahk:222) - channels are out of scope here, so nothing of ours was using them.
        //
        // GestureKey.StepOlder, NOT GestureKey.Paste. Paste is the key that OPENS a session, so mapping the
        // arrows onto it claimed Ctrl+Right and Ctrl+Down machine-wide: the overlay appeared, the keystroke was
        // swallowed so the editor never saw it, and releasing Ctrl pasted instead of moving the caret by a word.
        // Reported immediately, and it is the reason StepOlder exists. Up/Left were unaffected because Back was
        // never an entry point - which is exactly the asymmetry that makes this easy to reintroduce.
        NativeConstants.VK_DOWN or NativeConstants.VK_RIGHT => GestureKey.StepOlder,
        NativeConstants.VK_UP or NativeConstants.VK_LEFT => GestureKey.Back,

        // Home was a second Escape until now, which was our own invention rather than the original's and
        // appeared in no help text, card or footer - so no documented behaviour changes here. Cancel is
        // untouched on Esc.
        NativeConstants.VK_HOME => GestureKey.JumpToNewest,
        NativeConstants.VK_END => GestureKey.JumpToOldest,
        NativeConstants.VK_DELETE => GestureKey.DeleteCurrent,

        >= 0x31 and <= 0x39 => GestureKey.Digit1 + (virtualKey - 0x31),

        // Numpad 1-9.
        >= 0x61 and <= 0x69 => GestureKey.Digit1 + (virtualKey - 0x61),

        _ => GestureKey.None,
    };

    /// <summary>
    /// Resolves the character a key would produce under the current layout and modifier state,
    /// or null when it produces none.
    /// </summary>
    public static char? ToCharacter(int virtualKey)
    {
        var keyState = new byte[256];

        if (!NativeMethods.GetKeyboardState(keyState))
        {
            return null;
        }

        // Ctrl must be masked out. With Ctrl set, ToUnicodeEx returns control characters - Ctrl+A
        // becomes U+0001 rather than 'a' - which would put unprintable junk in the search box.
        keyState[NativeConstants.VK_CONTROL] = 0;
        keyState[NativeConstants.VK_LCONTROL] = 0;
        keyState[NativeConstants.VK_RCONTROL] = 0;

        var layout = NativeMethods.GetKeyboardLayout(0);
        var buffer = new StringBuilder(8);

        // wFlags bit 2 keeps the kernel's dead-key state untouched, so probing a key here cannot
        // corrupt the accent the user is midway through composing in the real app.
        var result = NativeMethods.ToUnicodeEx(
            (uint)virtualKey,
            0,
            keyState,
            buffer,
            buffer.Capacity,
            0x4,
            layout);

        if (result <= 0 || buffer.Length == 0)
        {
            return null;
        }

        var character = buffer[0];
        return char.IsControl(character) ? null : character;
    }
}
