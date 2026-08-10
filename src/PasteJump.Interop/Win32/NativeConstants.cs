namespace PasteJump.Interop.Win32;

/// <summary>Win32 constants, named as they appear in the SDK headers.</summary>
internal static class NativeConstants
{
    // ---- window messages
    public const int WM_DESTROY = 0x0002;
    public const int WM_CLOSE = 0x0010;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_APP = 0x8000;

    /// <summary>Private message used for tray icon callbacks.</summary>
    public const int WM_TRAYICON = WM_APP + 1;

    // ---- window creation
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    // ---- clipboard formats
    public const uint CF_TEXT = 1;
    public const uint CF_BITMAP = 2;
    public const uint CF_METAFILEPICT = 3;
    public const uint CF_SYLK = 4;
    public const uint CF_DIF = 5;
    public const uint CF_TIFF = 6;
    public const uint CF_OEMTEXT = 7;
    public const uint CF_DIB = 8;
    public const uint CF_PALETTE = 9;
    public const uint CF_PENDATA = 10;
    public const uint CF_RIFF = 11;
    public const uint CF_WAVE = 12;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_ENHMETAFILE = 14;
    public const uint CF_HDROP = 15;
    public const uint CF_LOCALE = 16;
    public const uint CF_DIBV5 = 17;
    public const uint CF_OWNERDISPLAY = 0x0080;
    public const uint CF_DSPTEXT = 0x0081;
    public const uint CF_DSPBITMAP = 0x0082;
    public const uint CF_DSPMETAFILEPICT = 0x0083;
    public const uint CF_DSPENHMETAFILE = 0x008E;

    /// <summary>
    /// Formats whose clipboard handle is <em>not</em> an HGLOBAL, so <c>GlobalLock</c> on them is
    /// meaningless and reading them as bytes would be undefined behaviour.
    /// <para>
    /// GDI-object formats need <c>CopyImage</c> / <c>CopyEnhMetaFile</c> to round-trip properly.
    /// We skip them instead: Windows synthesises <c>CF_BITMAP</c> from <c>CF_DIB</c> anyway, and
    /// <c>CF_DIB</c>/<c>CF_DIBV5</c> are plain HGLOBALs we do capture - so nothing a user cares
    /// about is actually lost.
    /// </para>
    /// </summary>
    public static readonly uint[] NonGlobalFormats =
    [
        CF_BITMAP,
        CF_PALETTE,
        CF_METAFILEPICT,
        CF_ENHMETAFILE,
        CF_OWNERDISPLAY,
        CF_DSPBITMAP,
        CF_DSPMETAFILEPICT,
        CF_DSPENHMETAFILE,
    ];

    /// <summary>
    /// Formats Windows regenerates for us on write, given a richer sibling. Writing them back
    /// explicitly is not merely redundant - a stale <c>CF_TEXT</c> captured under a different
    /// system codepage can disagree with the <c>CF_UNICODETEXT</c> beside it, and whichever
    /// format the target app happens to prefer decides which one the user gets.
    /// </summary>
    public static readonly uint[] SynthesisedFromUnicodeText = [CF_TEXT, CF_OEMTEXT, CF_LOCALE];

    // Note: the rule for dropping duplicate image encodings lives in Core, as RedundantImageFormats, so it
    // can be tested without a clipboard. It is applied by Win32ClipboardAccess at capture.

    // ---- global hotkeys
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    /// <summary>
    /// Suppresses auto-repeat while the chord is held. Without it, holding the history hotkey down
    /// delivers a stream of WM_HOTKEY messages and the window is asked to open dozens of times.
    /// </summary>
    public const uint MOD_NOREPEAT = 0x4000;

    // ---- hooks
    public const int WH_KEYBOARD_LL = 13;
    public const int HC_ACTION = 0;
    public const uint LLKHF_INJECTED = 0x00000010;

    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    // ---- virtual keys
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_SPACE = 0x20;
    public const int VK_INSERT = 0x2D;
    public const int VK_RETURN = 0x0D;
    public const int VK_HOME = 0x24;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;
    public const int VK_BACK = 0x08;
    public const int VK_F1 = 0x70;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;

    // The remaining modifiers. Needed by VirtualKeyTranslator.IsModifier, which decides what is never
    // swallowed - the left/right variants matter because a low-level hook reports those rather than the
    // generic VK_MENU that GetKeyState answers for.
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_CAPITAL = 0x14;
    public const int VK_NUMLOCK = 0x90;
    public const int VK_SCROLL = 0x91;
    public const int VK_OEM_MINUS = 0xBD;
    public const int VK_SUBTRACT = 0x6D;

    public const int VK_A = 0x41;
    public const int VK_C = 0x43;
    public const int VK_E = 0x45;
    public const int VK_F = 0x46;
    public const int VK_H = 0x48;
    public const int VK_Q = 0x51;
    public const int VK_S = 0x53;
    public const int VK_T = 0x54;
    public const int VK_V = 0x56;
    public const int VK_X = 0x58;
    public const int VK_Z = 0x5A;
    public const int VK_0 = 0x30;
    public const int VK_9 = 0x39;

    // ---- SendInput
    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_SCANCODE = 0x0008;

    /// <summary>Translation type for <c>MapVirtualKey</c>: virtual key to scan code.</summary>
    public const uint MAPVK_VK_TO_VSC = 0;

    /// <summary>
    /// Stamped into <c>KEYBDINPUT.dwExtraInfo</c> on every keystroke we synthesise, so the hook can
    /// recognise our own input specifically.
    /// <para>
    /// <c>LLKHF_INJECTED</c> is not good enough for that job: it is set by <em>any</em> process
    /// calling <c>SendInput</c>, which includes Remote Desktop, on-screen keyboards, macro-capable
    /// keyboards, AutoHotkey and accessibility tools. Ignoring all injected input therefore makes
    /// the gesture dead for those users, while ignoring only our own signature keeps the loop-guard
    /// and lets genuine input through whatever produced it.
    /// </para>
    /// </summary>
    public static readonly IntPtr PasteJumpInputSignature = new(0x436A6F67);

    // ---- GlobalAlloc
    public const uint GMEM_MOVEABLE = 0x0002;

    // ---- tray icon
    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_LBUTTONDBLCLK = 0x0203;

    // ---- LoadImage
    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTSIZE = 0x00000040;

    // ---- GetSystemMetrics
    public const int SM_CXSMICON = 49;
    public const int SM_CYSMICON = 50;

    // ---- DPI
    public const int MDT_EFFECTIVE_DPI = 0;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    // ---- process access
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
}
