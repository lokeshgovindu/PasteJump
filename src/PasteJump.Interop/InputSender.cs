using PasteJump.Core.Abstractions;
using PasteJump.Core.Settings;
using PasteJump.Interop.Win32;

namespace PasteJump.Interop;

/// <summary>
/// Synthesises the paste keystroke.
/// <para>
/// Note the explicit release of any physically-held modifiers before sending. When the user
/// releases Ctrl to commit, our synthesised Ctrl+V can race the real key-up, and a Shift still
/// down from a paste-pop turns Ctrl+V into Ctrl+Shift+V - which many apps bind to "paste as plain
/// text" or something else entirely. Normalising modifier state first removes that whole class of
/// "pasted the wrong thing" bug.
/// </para>
/// <para>
/// Every event also carries a real scan code and our own <c>dwExtraInfo</c> signature. Both matter
/// for compatibility rather than correctness-on-paper: see <see cref="ScanCodeFor"/> and
/// <see cref="NativeConstants.PasteJumpInputSignature"/>.
/// </para>
/// </summary>
public sealed class InputSender : IPasteSender
{
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    /// <summary>Number of times <c>SendInput</c> reported sending fewer events than requested.</summary>
    public int SendFailureCount { get; private set; }

    /// <summary>
    /// Sends the paste chord to the foreground window. False when <c>SendInput</c> was blocked - which
    /// happens when the foreground window belongs to a more privileged process, since UIPI silently
    /// discards synthetic input aimed at it.
    /// </summary>
    public bool SendPaste(PasteKeystroke keystroke)
    {
        var inputs = new List<INPUT>(12);

        // Every modifier is lifted first, including the one the chord is about to use, so the sequence
        // starts from a known state instead of depending on whether the user's own release has landed
        // yet. Without this, a Shift still held from a paste-pop turns Ctrl+V into Ctrl+Shift+V, which
        // many applications bind to something else entirely.
        AppendModifierRelease(inputs, NativeConstants.VK_LSHIFT);
        AppendModifierRelease(inputs, NativeConstants.VK_RSHIFT);
        AppendModifierRelease(inputs, NativeConstants.VK_SHIFT);
        AppendModifierRelease(inputs, NativeConstants.VK_MENU);
        AppendModifierRelease(inputs, VK_LWIN);
        AppendModifierRelease(inputs, VK_RWIN);
        AppendModifierRelease(inputs, NativeConstants.VK_LCONTROL);
        AppendModifierRelease(inputs, NativeConstants.VK_RCONTROL);
        AppendModifierRelease(inputs, NativeConstants.VK_CONTROL);

        if (keystroke == PasteKeystroke.ShiftInsert)
        {
            inputs.Add(KeyDown(NativeConstants.VK_SHIFT));
            inputs.Add(KeyDown(NativeConstants.VK_INSERT, extended: true));
            inputs.Add(KeyUp(NativeConstants.VK_INSERT, extended: true));
            inputs.Add(KeyUp(NativeConstants.VK_SHIFT));
        }
        else
        {
            inputs.Add(KeyDown(NativeConstants.VK_CONTROL));
            inputs.Add(KeyDown(NativeConstants.VK_V));
            inputs.Add(KeyUp(NativeConstants.VK_V));
            inputs.Add(KeyUp(NativeConstants.VK_CONTROL));
        }

        return Send(inputs);
    }

    /// <summary>Presses a single key with no modifiers.</summary>
    public bool SendKey(int virtualKey) => Send([KeyDown(virtualKey), KeyUp(virtualKey)]);

    private static void AppendModifierRelease(List<INPUT> inputs, int virtualKey)
    {
        if ((NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0)
        {
            inputs.Add(KeyUp(virtualKey));
        }
    }

    private bool Send(IReadOnlyList<INPUT> inputs)
    {
        if (inputs.Count == 0)
        {
            return true;
        }

        var array = inputs.ToArray();

        var sent = NativeMethods.SendInput(
            (uint)array.Length,
            array,
            System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());

        if (sent == array.Length)
        {
            return true;
        }

        // A partial or zero send is not a transient hiccup worth retrying: the usual cause is UIPI
        // refusing synthetic input for an elevated foreground window, which will refuse it again.
        SendFailureCount++;
        return false;
    }

    /// <summary>
    /// Scan code for a virtual key under the active layout, or 0 if the layout has none.
    /// <para>
    /// Populating <c>wScan</c> alongside <c>wVk</c> is what makes the keystroke visible to consumers
    /// that read scan codes: RDP and Citrix clients, VM guest windows, DirectInput and raw-input
    /// users, and various Qt/Java apps all ignore an event whose scan code is zero. The virtual key
    /// is deliberately kept as well rather than switching to <c>KEYEVENTF_SCANCODE</c>, so ordinary
    /// Win32 apps still see the exact VK they expect.
    /// </para>
    /// </summary>
    private static ushort ScanCodeFor(int virtualKey)
        => (ushort)NativeMethods.MapVirtualKey((uint)virtualKey, NativeConstants.MAPVK_VK_TO_VSC);

    /// <param name="extended">
    /// Sets <c>KEYEVENTF_EXTENDEDKEY</c>. Required for Insert, and for the other keys on the grey island
    /// between the main block and the numpad: they share a scan code with their numpad twin, and the
    /// extended flag is the only thing that distinguishes Insert from numpad 0. Omit it and a scan-code
    /// reader sees a numpad keypress with Num Lock off.
    /// </param>
    private static INPUT KeyDown(int virtualKey, bool extended = false) => new()
    {
        type = NativeConstants.INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wVk = (ushort)virtualKey,
            wScan = ScanCodeFor(virtualKey),
            dwFlags = extended ? NativeConstants.KEYEVENTF_EXTENDEDKEY : 0,
            dwExtraInfo = NativeConstants.PasteJumpInputSignature,
        },
    };

    private static INPUT KeyUp(int virtualKey, bool extended = false) => new()
    {
        type = NativeConstants.INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wVk = (ushort)virtualKey,
            wScan = ScanCodeFor(virtualKey),
            dwFlags = NativeConstants.KEYEVENTF_KEYUP
                | (extended ? NativeConstants.KEYEVENTF_EXTENDEDKEY : 0),
            dwExtraInfo = NativeConstants.PasteJumpInputSignature,
        },
    };
}
