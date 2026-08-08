namespace PasteJump.Core.Settings;

/// <summary>
/// Which chord PasteJump synthesises to make the target application paste.
/// <para>
/// This is a setting because of a collision that cannot be won any other way. A
/// <c>WH_KEYBOARD_LL</c> hook belonging to another process sees every keystroke we inject, and if that
/// process has a suppressing hotkey on the same chord it consumes ours before the focused application
/// ever sees it. Clipjump does exactly that - <c>Clipjump.ahk:227</c> registers <c>$^V</c>, where the
/// <c>$</c> forces its own keyboard hook and the absence of <c>~</c> means the key is swallowed - so
/// with Clipjump running, a PasteJump paste silently does nothing while copy carries on working,
/// because capture goes through <c>WM_CLIPBOARDUPDATE</c> and no hook can suppress that.
/// </para>
/// <para>
/// There is no API-level fix. Injected input is deliberately visible to low-level hooks, and returning
/// 1 from our own hook to "keep" the keystroke would remove it from the chain <em>and</em> from delivery
/// to the target window, so nothing would paste at all. Sending a different chord is the only avenue.
/// </para>
/// </summary>
public enum PasteKeystroke
{
    /// <summary>Ctrl+V. The default, and what every Windows application understands.</summary>
    CtrlV,

    /// <summary>
    /// Shift+Insert. The legacy Windows paste chord, still honoured by Win32 edit controls, Office,
    /// browsers, Electron shells and terminals. Use it to coexist with another clipboard manager that
    /// has claimed Ctrl+V.
    /// </summary>
    ShiftInsert,
}
