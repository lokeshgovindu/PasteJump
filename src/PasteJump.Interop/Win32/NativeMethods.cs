using System.Runtime.InteropServices;
using System.Text;

namespace PasteJump.Interop.Win32;

/// <summary>
/// The P/Invoke surface. Deliberately small - roughly forty entry points, against which
/// everything else in the app is managed code.
/// <para>
/// Uses <c>DllImport</c> rather than the source-generated <c>LibraryImport</c>. That would
/// normally be the modern default for its AOT-friendliness, but WPF already rules out both
/// NativeAOT and trimming, so the only thing <c>LibraryImport</c> would buy here is stricter
/// marshalling rules to satisfy. Not worth the churn.
/// </para>
/// </summary>
internal static class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Shell32 = "shell32.dll";
    private const string Shcore = "shcore.dll";

    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // ------------------------------------------------------------- clipboard

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport(User32, SetLastError = true)]
    public static extern uint EnumClipboardFormats(uint format);

    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClipboardFormatName(uint format, StringBuilder lpszFormatName, int cchMaxCount);

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport(User32)]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport(User32)]
    public static extern int CountClipboardFormats();

    // ------------------------------------------------------------- global memory

    [DllImport(Kernel32, SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport(Kernel32, SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport(Kernel32, SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport(Kernel32, SetLastError = true)]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    // ------------------------------------------------------------- hooks and input

    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport(User32)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport(User32, SetLastError = true)]
    public static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport(User32)]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// Translates a virtual key into a scan code.
    /// <para>
    /// Needed because a synthesised keystroke carrying <c>wScan == 0</c> is invisible to anything
    /// that reads scan codes rather than virtual keys - Remote Desktop and Citrix clients, VM guest
    /// windows, DirectInput/raw-input consumers, and a number of Qt and Java applications. Those are
    /// exactly the "paste works in Notepad but not here" cases.
    /// </para>
    /// </summary>
    [DllImport(User32)]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport(User32)]
    public static extern short GetKeyState(int vKey);

    [DllImport(User32)]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetKeyboardState([Out] byte[] lpKeyState);

    /// <summary>
    /// Translates a virtual key to characters using the active keyboard layout.
    /// <para>
    /// Used instead of a hand-rolled VK-to-character table so that non-US layouts work. A table
    /// keyed on VK codes silently produces the wrong letters on AZERTY, QWERTZ and Dvorak, because
    /// VK codes describe key positions rather than the characters printed on them.
    /// </para>
    /// </summary>
    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern int ToUnicodeEx(
        uint wVirtKey,
        uint wScanCode,
        byte[] lpKeyState,
        [Out] StringBuilder pwszBuff,
        int cchBuff,
        uint wFlags,
        IntPtr dwhkl);

    // ------------------------------------------------------------- windows

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string? lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport(User32, CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport(User32)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport(User32, SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// Finds a window by title. <c>FindWindowEx</c> rather than <c>FindWindow</c> because the parent must be
    /// specified: a message-only window's parent is <c>HWND_MESSAGE</c>, and a search rooted at the desktop
    /// never sees one. <c>HWND_BROADCAST</c> cannot reach them either, which rules out broadcasting.
    /// </summary>
    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(
        IntPtr hWndParent,
        IntPtr hWndChildAfter,
        string? lpszClass,
        string? lpszWindow);

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Interns a message name into an id every process gets the same answer for, so two instances agree on
    /// what the message means without hard-coding a <c>WM_APP</c> offset.
    /// </summary>
    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string lpString);

    /// <summary>
    /// Lets the named process take the foreground next time it asks.
    /// <para>
    /// Currently unused, and kept because it is the missing piece the moment anything here needs to raise
    /// another process's window: Windows grants <c>SetForegroundWindow</c> only to a process that already has
    /// the foreground, so the target cannot raise itself - the process the user just launched has to hand the
    /// right over first. <c>SingleInstanceSignal</c> does not need it because a toast is topmost and never
    /// activates; a window would.
    /// </para>
    /// </summary>
    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Claims a system-wide chord. Fails when another process already owns it, which is ordinary and must
    /// be reported to the user rather than treated as an error - the whole point of a global hotkey is
    /// that it is exclusive, so somebody has to lose.
    /// </summary>
    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport(User32, SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport(User32, SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport(User32, SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport(User32, SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ------------------------------------------------------------- monitors and DPI

    [DllImport(User32)]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport(Shcore)]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport(User32)]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    // ------------------------------------------------------------- processes

    [DllImport(Kernel32, SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    // ------------------------------------------------------------- tray

    [DllImport(Shell32, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    /// <summary>
    /// Pulls icons out of a PE file. We point this at our own executable so the tray icon comes
    /// from the embedded resource - no loose .ico to ship, and it keeps working under a
    /// single-file publish where there is no adjacent file to load.
    /// </summary>
    [DllImport(Shell32, CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(
        string lpszFile,
        int nIconIndex,
        IntPtr[]? phiconLarge,
        IntPtr[]? phiconSmall,
        uint nIcons);

    /// <summary>
    /// Builds an icon from the bytes of ONE frame of an <c>.ico</c> - not the whole file - at an explicit size.
    /// <para>
    /// This is what lets the icons live inside the executable. <c>ExtractIconEx</c> can only return the two
    /// system sizes, 32 and 16, so it cannot produce the 24 px the shell asks for at 150% scaling, and
    /// <c>LoadImage</c> honours a requested size only from a file on disk. This takes both: a size, and bytes
    /// from anywhere.
    /// </para>
    /// <para>
    /// <c>dwVer</c> must be 0x00030000 - the version of the icon *resource format*, not of anything in this
    /// application, and the call fails outright with any other value.
    /// </para>
    /// <para>
    /// It accepts a PNG-compressed frame, which is worth recording because the documentation does not say so
    /// and older accounts say it does not: every frame in our own icons is PNG, and this was verified returning
    /// a 24x24 32bpp icon from one before the code was written this way. If that ever stops being true the
    /// symptom is a missing tray icon, and the fix is to emit DIB frames from tools/generate-icon.ps1.
    /// </para>
    /// </summary>
    [DllImport(User32, SetLastError = true)]
    public static extern IntPtr CreateIconFromResourceEx(
        byte[] presbits,
        uint dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer,
        int cxDesired,
        int cyDesired,
        uint flags);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// System metrics. Used for <c>SM_CXSMICON</c>, so a tray icon is requested at the size the
    /// shell actually wants at the current DPI rather than assuming 16x16 and being downscaled.
    /// </summary>
    [DllImport(User32)]
    public static extern int GetSystemMetrics(int nIndex);
}
