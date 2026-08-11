using System.Runtime.InteropServices;
using PasteJump.Interop.Win32;

namespace PasteJump.Interop;

/// <summary>
/// Notification-area icon, driven directly through <c>Shell_NotifyIcon</c>.
/// <para>
/// Hand-rolled rather than taken from a NuGet package, for two reasons: we already own a
/// message-only window for the clipboard listener so the plumbing cost is near zero, and a
/// portable app that ships as a folder benefits from every dependency it does not carry.
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;

    private readonly MessageOnlyWindow _window;
    private readonly bool _ownsWindow;
    private IntPtr _iconHandle;
    private bool _added;
    private bool _disposed;
    private string _tooltip;

    /// <summary>
    /// Left click or double click. Screen coordinates are the cursor position, as for
    /// <see cref="ContextMenuRequested"/>.
    /// <para>
    /// Carries the position because what a left click does is configurable, and one of the choices is the tray
    /// menu - which has to be placed at the cursor. Without it the handler would have to ask for the cursor
    /// position itself, by which time the pointer may have moved.
    /// </para>
    /// </summary>
    public event Action<int, int>? Activated;

    /// <summary>Right click. Screen coordinates are the cursor position.</summary>
    public event Action<int, int>? ContextMenuRequested;

    public TrayIcon(string tooltip, MessageOnlyWindow? window = null)
    {
        _tooltip = tooltip;
        _ownsWindow = window is null;
        _window = window ?? new MessageOnlyWindow("_tray");
        _window.MessageReceived += OnMessage;

        _iconHandle = LoadOwnIcon();
    }

    public void Show()
    {
        if (_added)
        {
            return;
        }

        var data = BuildData();
        _added = NativeMethods.Shell_NotifyIcon(NativeConstants.NIM_ADD, ref data);
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;

        if (!_added)
        {
            return;
        }

        var data = BuildData();
        NativeMethods.Shell_NotifyIcon(NativeConstants.NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Replaces the icon, loading it from an <c>.ico</c> file at the size the shell wants.
    /// <para>
    /// Exists because the notification area is the one surface whose background colour changes under
    /// the icon: the taskbar follows the Windows "choose your mode" setting, so a single icon is
    /// wrong half the time. Returns false and leaves the current icon in place if the file is missing
    /// or unreadable - a portable folder can be copied incompletely, and losing the tray icon
    /// entirely would leave the app running with no way to reach its menu.
    /// </para>
    /// </summary>
    public bool SetIconFromFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        // Ask for the shell's small-icon metric rather than a hard-coded 16: at 150% scaling it wants
        // 24, and passing 16 there gets it upscaled and blurry.
        var width = NativeMethods.GetSystemMetrics(NativeConstants.SM_CXSMICON);
        var height = NativeMethods.GetSystemMetrics(NativeConstants.SM_CYSMICON);

        var loaded = NativeMethods.LoadImage(
            IntPtr.Zero,
            path,
            NativeConstants.IMAGE_ICON,
            width > 0 ? width : 16,
            height > 0 ? height : 16,
            NativeConstants.LR_LOADFROMFILE);

        if (loaded == IntPtr.Zero)
        {
            return false;
        }

        var previous = _iconHandle;
        _iconHandle = loaded;

        if (_added)
        {
            var data = BuildData();
            NativeMethods.Shell_NotifyIcon(NativeConstants.NIM_MODIFY, ref data);
        }

        // Destroyed only after the shell has been pointed at the replacement, so there is no window
        // in which it holds a handle we have already freed.
        if (previous != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(previous);
        }

        return true;
    }

    public void Hide()
    {
        if (!_added)
        {
            return;
        }

        var data = BuildData();
        NativeMethods.Shell_NotifyIcon(NativeConstants.NIM_DELETE, ref data);
        _added = false;
    }

    private NOTIFYICONDATA BuildData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = NativeConstants.NIF_MESSAGE | NativeConstants.NIF_ICON | NativeConstants.NIF_TIP,
        uCallbackMessage = NativeConstants.WM_TRAYICON,
        hIcon = _iconHandle,

        // Tooltip is a fixed 128-char buffer; overrunning it corrupts the struct.
        szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeConstants.WM_TRAYICON)
        {
            return null;
        }

        switch ((int)lParam)
        {
            // Only the button-up, not the double-click. Windows sends LBUTTONUP and then LBUTTONDBLCLK for a
            // double click, so handling both fired the action twice - harmless when it opened a window that was
            // already open, and a visible flicker now that one of the choices is the tray menu.
            case NativeConstants.WM_LBUTTONUP:
                if (NativeMethods.GetCursorPos(out var clicked))
                {
                    Activated?.Invoke(clicked.X, clicked.Y);
                }

                break;

            case NativeConstants.WM_RBUTTONUP:
                if (NativeMethods.GetCursorPos(out var point))
                {
                    ContextMenuRequested?.Invoke(point.X, point.Y);
                }

                break;
        }

        return IntPtr.Zero;
    }

    private static IntPtr LoadOwnIcon()
    {
        var exePath = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exePath))
        {
            return IntPtr.Zero;
        }

        var small = new IntPtr[1];

        // Small icon specifically: the notification area asks for a 16x16-class icon, and handing
        // it the large one produces a visibly blurry downscale.
        return NativeMethods.ExtractIconEx(exePath, 0, null, small, 1) > 0
            ? small[0]
            : IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
        _window.MessageReceived -= OnMessage;

        if (_iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        if (_ownsWindow)
        {
            _window.Dispose();
        }
    }
}
