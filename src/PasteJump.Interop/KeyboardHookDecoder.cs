namespace PasteJump.Interop;

/// <summary>
/// Turns one raw <c>WH_KEYBOARD_LL</c> callback into a <see cref="KeyboardHookEvent"/>, or decides it is none of
/// our business. Pure: no P/Invoke, no state, no message loop.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <see cref="LowLevelKeyboardHook"/> so it can be tested, which it could not be before: installing
/// a real hook needs a message loop and a live keyboard, so the whole callback - including every decision in it -
/// sat in the one part of the application nothing could reach. The same move <c>VirtualKeyTranslator</c> already
/// made, and for the same reason.
/// </para>
/// <para>
/// The decisions here are small and have each been got wrong in a shipped build:
/// </para>
/// <list type="bullet">
/// <item>
/// <b><c>LLKHF_INJECTED</c> alone must never be used to ignore input.</b> That flag is set by <em>any</em>
/// process calling <c>SendInput</c>, so filtering on it killed the gesture outright under Remote Desktop, in VM
/// guest windows, and for anyone using a macro keyboard, an on-screen keyboard or an accessibility tool. Only our
/// own signature in <c>dwExtraInfo</c> identifies our own injection, which is the loop guard that is actually
/// needed - without it, sending Ctrl+V re-enters paste mode for ever.
/// </item>
/// <item>
/// <b>Both the Sys and the ordinary messages count.</b> <c>WM_SYSKEYDOWN</c> arrives whenever Alt is held, so
/// treating only <c>WM_KEYDOWN</c> as a key press makes every Alt combination invisible - and Alt chords are
/// exactly what the recognizer must see in order to decline to swallow them.
/// </item>
/// <item>
/// <b><c>nCode</c> other than <c>HC_ACTION</c> carries no event at all</b> and the parameters must not be
/// interpreted; the only correct response is to pass it along untouched.
/// </item>
/// </list>
/// </remarks>
public static class KeyboardHookDecoder
{
    /// <summary>Windows' own numbers, stated here rather than shared with the P/Invoke layer.</summary>
    /// <remarks>
    /// Deliberately independent of <c>NativeConstants</c>: these are values from WinUser.h, and restating them
    /// means a typo in the constants table makes a test fail rather than quietly agree with itself. The same
    /// reasoning <c>VirtualKeyTranslatorTests</c> applies to virtual key codes.
    /// </remarks>
    private const int HcAction = 0;

    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private const uint InjectedFlag = 0x00000010;

    /// <summary>
    /// The event this callback describes, or null when it describes none - which means "pass it on unexamined".
    /// </summary>
    /// <param name="code">The hook code. Anything but <c>HC_ACTION</c> yields null.</param>
    /// <param name="message">The window message: a key down, a key up, or something we do not handle.</param>
    /// <param name="virtualKey">The virtual key code from <c>KBDLLHOOKSTRUCT.vkCode</c>.</param>
    /// <param name="flags">The <c>KBDLLHOOKSTRUCT.flags</c> field, which carries <c>LLKHF_INJECTED</c>.</param>
    /// <param name="extraInfo">
    /// The <c>KBDLLHOOKSTRUCT.dwExtraInfo</c> field. Compared against <paramref name="ownSignature"/> to tell our
    /// own injected keystrokes from everybody else's.
    /// </param>
    /// <param name="ownSignature">
    /// The value this application stamps into its own injected input. Passed in rather than read from the
    /// constants table so a test can state it independently.
    /// </param>
    public static KeyboardHookEvent? Decode(
        int code,
        int message,
        int virtualKey,
        uint flags,
        IntPtr extraInfo,
        IntPtr ownSignature)
    {
        if (code != HcAction)
        {
            return null;
        }

        var isKeyDown = message is WmKeyDown or WmSysKeyDown;
        var isKeyUp = message is WmKeyUp or WmSysKeyUp;

        if (!isKeyDown && !isKeyUp)
        {
            return null;
        }

        var injected = (flags & InjectedFlag) != 0;

        // Our own injection is injected AND carries our signature. The conjunction matters: a signature on a
        // key that is not marked injected cannot have come from us, and every other process's SendInput sets
        // the flag without the signature.
        var ownInjection = injected && extraInfo == ownSignature;

        return new KeyboardHookEvent(virtualKey, isKeyDown, injected, ownInjection);
    }
}
