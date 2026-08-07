using System.Runtime.InteropServices;
using System.Text;

namespace Clipjog.Interop.Win32;

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

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

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

    [DllImport(User32, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImage(
        IntPtr hinst,
        string lpszName,
        uint uType,
        int cxDesired,
        int cyDesired,
        uint fuLoad);

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
