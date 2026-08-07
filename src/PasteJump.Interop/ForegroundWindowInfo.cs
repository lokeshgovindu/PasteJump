using System.Text;
using PasteJump.Core.Abstractions;
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
    /// Where to put the overlay: at the text caret when the focused control exposes one, else at
    /// the mouse. Returned in physical screen pixels.
    /// <para>
    /// Caret-first placement matters because the overlay is a transient preview attached to a
    /// typing action. Anchoring it to the mouse when the user is typing puts it on the far side of
    /// the screen from where they are looking.
    /// </para>
    /// </summary>
    public static (int X, int Y) GetPreferredOverlayAnchor()
    {
        var hwnd = NativeMethods.GetForegroundWindow();

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

                    if (NativeMethods.ClientToScreen(info.hwndCaret, ref point))
                    {
                        return (point.X, point.Y);
                    }
                }
            }
        }

        return GetCursorPosition();
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
