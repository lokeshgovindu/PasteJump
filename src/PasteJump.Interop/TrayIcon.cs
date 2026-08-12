using System.Runtime.InteropServices;
using PasteJump.Core.Imaging;
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

    /// <summary>
    /// The icon <em>resource format</em> version <c>CreateIconFromResourceEx</c> demands. Nothing to do with
    /// this application's version, and the call fails outright with any other value.
    /// </summary>
    private const uint IconResourceVersion = 0x00030000;

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
    /// Replaces the icon from the bytes of an <c>.ico</c>, rendered at the size the shell wants.
    /// <para>
    /// Bytes rather than a path so the icons can be embedded in the executable. They were loose files beside
    /// it until 2026-08-12, for one reason: only <c>LoadImage(LR_LOADFROMFILE)</c> honoured a requested size,
    /// and the notification area asks for 16 px at 100% scaling but 24 at 150%. <c>CreateIconFromResourceEx</c>
    /// takes a size too, given a single frame - so <see cref="IconFile"/> picks the frame and this makes the
    /// icon, and nothing has to be on disk. A portable copy unzipped without its <c>Assets</c> folder used to
    /// lose its tray icon, and with no main window that left no way to reach the application at all.
    /// </para>
    /// <para>
    /// Returns false and leaves the current icon in place if the bytes are not a usable icon, rather than
    /// throwing: this is called during start-up and on every state change, and no tray icon is a far worse
    /// outcome than a stale one.
    /// </para>
    /// </summary>
    public bool SetIcon(ReadOnlySpan<byte> ico)
    {
        if (ico.IsEmpty)
        {
            return false;
        }

        // Ask for the shell's small-icon metric rather than a hard-coded 16: at 150% scaling it wants
        // 24, and passing 16 there gets it upscaled and blurry.
        var metricWidth = NativeMethods.GetSystemMetrics(NativeConstants.SM_CXSMICON);
        var metricHeight = NativeMethods.GetSystemMetrics(NativeConstants.SM_CYSMICON);

        var width = metricWidth > 0 ? metricWidth : 16;
        var height = metricHeight > 0 ? metricHeight : 16;

        // Chosen on width alone, since every icon here is square and a non-square frame would be a defect in
        // the artwork rather than something to accommodate.
        if (IconFile.SelectFrame(ico, width) is not { } frame)
        {
            return false;
        }

        // Copied out because the P/Invoke marshals a byte[]. It is one frame - under 1 KB at these sizes - so
        // the allocation is not worth avoiding with a fixed pointer and unsafe code.
        var bytes = ico.Slice(frame.Offset, frame.Length).ToArray();

        var loaded = NativeMethods.CreateIconFromResourceEx(
            bytes,
            (uint)bytes.Length,
            fIcon: true,
            IconResourceVersion,
            width,
            height,
            NativeConstants.LR_DEFAULTCOLOR);

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

    /// <summary>
    /// Reports which frame <see cref="SetIcon"/> would choose for a given size, and whether Windows accepts it,
    /// without touching the notification area. For the UI smoke harness.
    /// <para>
    /// A hook rather than a test in PasteJump.Interop.Tests because the icons are embedded in the WPF project,
    /// which that test project deliberately does not reference. The harness already loads them.
    /// </para>
    /// </summary>
    /// <returns>
    /// The chosen frame's width, and whether an icon was created. Width is 0 when nothing could be selected.
    /// </returns>
    public static (int FrameWidth, bool Created) DescribeIconForSmokeTest(ReadOnlySpan<byte> ico, int size)
    {
        if (IconFile.SelectFrame(ico, size) is not { } frame)
        {
            return (0, false);
        }

        var bytes = ico.Slice(frame.Offset, frame.Length).ToArray();

        var icon = NativeMethods.CreateIconFromResourceEx(
            bytes,
            (uint)bytes.Length,
            fIcon: true,
            IconResourceVersion,
            size,
            size,
            NativeConstants.LR_DEFAULTCOLOR);

        if (icon != IntPtr.Zero)
        {
            // Destroyed at once: this creates icons in a loop and hands none of them to the shell, so without
            // it the harness would leak one per size per state.
            NativeMethods.DestroyIcon(icon);
        }

        return (frame.Width, icon != IntPtr.Zero);
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
