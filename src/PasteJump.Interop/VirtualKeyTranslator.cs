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
    /// The key bindings, matching the original Clipjump layout minus the channel keys (Up / Down /
    /// PitSwap), which have no meaning without channels.
    /// </summary>
    /// <param name="triggerVirtualKey">
    /// Virtual key of the configurable trigger - the key that opens a session and, once open, steps to an
    /// older clip. Checked before everything else so it always wins, and <c>V</c> is therefore <em>not</em>
    /// in the table below: when V is the trigger the first check catches it, and when it is not, V must
    /// fall through to <see cref="GestureKey.None"/> so it can be typed into the search box like any other
    /// unbound letter.
    /// </param>
    public static GestureKey ToGestureKey(int virtualKey, int triggerVirtualKey)
    {
        if (virtualKey == triggerVirtualKey)
        {
            return GestureKey.Paste;
        }

        return Map(virtualKey);
    }

    /// <summary>Bindings with the default <c>V</c> trigger, for the probe harness and for tests.</summary>
    public static GestureKey ToGestureKey(int virtualKey)
        => ToGestureKey(virtualKey, NativeConstants.VK_V);

    private static GestureKey Map(int virtualKey) => virtualKey switch
    {
        NativeConstants.VK_CONTROL or NativeConstants.VK_LCONTROL or NativeConstants.VK_RCONTROL
            => GestureKey.Control,

        NativeConstants.VK_SHIFT or NativeConstants.VK_LSHIFT or NativeConstants.VK_RSHIFT
            => GestureKey.Shift,

        NativeConstants.VK_C => GestureKey.Back,
        NativeConstants.VK_X => GestureKey.CycleCommitMode,
        NativeConstants.VK_A => GestureKey.JumpToNewest,
        NativeConstants.VK_Q => GestureKey.PromoteToFront,
        NativeConstants.VK_F => GestureKey.ToggleSearch,
        NativeConstants.VK_Z => GestureKey.CycleFormatter,
        NativeConstants.VK_SPACE => GestureKey.TogglePin,
        NativeConstants.VK_T => GestureKey.EditTags,
        NativeConstants.VK_S => GestureKey.PushToClipboard,
        NativeConstants.VK_H => GestureKey.EditClip,
        NativeConstants.VK_E => GestureKey.ExportClip,
        NativeConstants.VK_RETURN => GestureKey.Commit,
        NativeConstants.VK_F1 => GestureKey.Help,
        NativeConstants.VK_OEM_MINUS or NativeConstants.VK_SUBTRACT => GestureKey.ToggleJumpDirection,
        NativeConstants.VK_ESCAPE => GestureKey.Escape,
        NativeConstants.VK_HOME => GestureKey.Escape,
        NativeConstants.VK_BACK => GestureKey.Backspace,

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
