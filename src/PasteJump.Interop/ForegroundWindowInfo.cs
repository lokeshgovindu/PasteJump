using System.Text;
using PasteJump.Core.Abstractions;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;
using PasteJump.Interop.Win32;

namespace PasteJump.Interop;

/// <summary>
/// Identifies the foreground process, used both to tag captured clips with their source and to
/// honour the ignore list (so a password manager's clipboard never reaches the store).
/// </summary>
public sealed class ForegroundWindowInfo : IForegroundWindowInfo
{
    public string? GetForegroundProcessName()
    {
        var hwnd = NativeMethods.GetForegroundWindow();

        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        if (NativeMethods.GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId == 0)
        {
            return null;
        }

        // PROCESS_QUERY_LIMITED_INFORMATION rather than the broader QUERY_INFORMATION: it is the
        // narrowest right that answers the question, and unlike the latter it succeeds against
        // higher-integrity processes without elevation.
        var handle = NativeMethods.OpenProcess(
            NativeConstants.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            processId);

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;

            if (!NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return null;
            }

            var fullPath = buffer.ToString(0, (int)size);
            return Path.GetFileName(fullPath);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Where to put the overlay: at the text caret when the focused control exposes one, else centred on the
    /// window being pasted into, and only failing both at the mouse. Returned in physical screen pixels.
    /// <para>
    /// Caret-first placement matters because the overlay is a transient preview attached to a typing action.
    /// The window comes second because <b>most modern applications expose no Win32 caret at all</b> - Edge and
    /// every other Chromium browser, Electron, WPF, WinUI and Visual Studio all report <c>hwndCaret == 0</c> -
    /// so the fallback is the common case, not the rare one. It used to be the mouse, which put the overlay
    /// wherever the pointer happened to be: on the other monitor, over the taskbar, or where a click landed
    /// minutes ago. <see cref="OverlayAnchorChooser"/> owns the order and the reasoning.
    /// </para>
    /// </summary>
    /// <param name="preference">What the user asked for. See <see cref="PopupPosition"/>.</param>
    /// <param name="fixedPoint">The pinned position, when both coordinates are set. Null degrades to Automatic.</param>
    public static OverlayAnchor GetPreferredOverlayAnchor(
        PopupPosition preference = PopupPosition.Automatic,
        (int X, int Y)? fixedPoint = null)
    {
        var hwnd = NativeMethods.GetForegroundWindow();

        (int X, int Y)? caret = null;
        (int Left, int Top, int Right, int Bottom)? window = null;
        var topmost = false;

        if (hwnd != IntPtr.Zero)
        {
            var threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);

            if (threadId != 0)
            {
                var info = new GUITHREADINFO();
                info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>();

                if (NativeMethods.GetGUIThreadInfo(threadId, ref info)
                    && info.hwndCaret != IntPtr.Zero
                    && (info.rcCaret.Width > 0 || info.rcCaret.Height > 0))
                {
                    var point = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };

                    // Checked rather than assumed: hwndCaret can name a window that has already been destroyed,
                    // which is what Edge reports for a thread whose text field has gone. A stale handle fails
                    // here, and a caret we cannot place is no caret at all.
                    if (NativeMethods.ClientToScreen(info.hwndCaret, ref point))
                    {
                        caret = (point.X, point.Y);
                    }
                }
            }

            // IsIconic rather than inspecting the rectangle: Windows parks a minimised window at -32000, which
            // has a perfectly plausible width and height, so the numbers alone cannot answer this.
            if (!NativeMethods.IsIconic(hwnd) && NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                window = (rect.Left, rect.Top, rect.Right, rect.Bottom);

                // Whether we can rely on being drawn above it. The Start menu is WS_EX_TOPMOST and Windows puts
                // it in a band above ordinary topmost windows, so centring the overlay on it renders it
                // invisible - see OverlayPlacementSolver.
                topmost = (NativeMethods.GetWindowLong(hwnd, NativeConstants.GWL_EXSTYLE)
                    & NativeConstants.WS_EX_TOPMOST) != 0;
            }
        }

        return OverlayAnchorChooser.Choose(caret, window, GetCursorPosition(), topmost, preference, fixedPoint);
    }

    /// <summary>
    /// Mouse position in physical screen pixels, or the origin if it cannot be read.
    /// <para>
    /// Used to place the copy notification. Deliberately the cursor and not the caret: a copy is a
    /// mouse-or-keyboard action whose result the user looks for where they are working, and Clipjump
    /// anchored its equivalent tooltip to the mouse too.
    /// </para>
    /// </summary>
    public static (int X, int Y) GetCursorPosition()
        => NativeMethods.GetCursorPos(out var cursor) ? (cursor.X, cursor.Y) : (0, 0);

    /// <summary>
    /// The foreground process name for a diagnostic line, never throwing and never null. Static because the
    /// overlay host has no <see cref="IForegroundWindowInfo"/> of its own and does not need one for this.
    /// </summary>
    public static string GetForegroundProcessNameForTrace()
    {
        try
        {
            return new ForegroundWindowInfo().GetForegroundProcessName() ?? "(no foreground window)";
        }
        catch (Exception ex)
        {
            return "(failed: " + ex.GetType().Name + ")";
        }
    }

    /// <summary>Effective DPI of the monitor containing a physical point. 96 means unscaled.</summary>
    public static uint GetDpiForPoint(int x, int y)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new POINT { X = x, Y = y },
            NativeConstants.MONITOR_DEFAULTTONEAREST);

        if (monitor == IntPtr.Zero)
        {
            return 96;
        }

        return NativeMethods.GetDpiForMonitor(monitor, NativeConstants.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
            ? dpiX
            : 96;
    }
}
