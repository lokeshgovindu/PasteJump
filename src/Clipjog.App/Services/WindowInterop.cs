using System.Runtime.InteropServices;
using System.Windows;

namespace Clipjog.App.Services;

/// <summary>
/// Window-style and DPI plumbing for the overlay. Kept here rather than in Clipjog.Interop
/// because it is WPF-specific.
/// </summary>
internal static class WindowInterop
{
    private const int GWL_EXSTYLE = -20;

    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Applies the extended styles that make a window incapable of taking focus.
    /// <para>
    /// All three matter. NOACTIVATE stops clicks and foreground changes from activating it,
    /// TRANSPARENT removes it from hit testing so clicks fall through to the app underneath, and
    /// TOOLWINDOW keeps it out of Alt+Tab. Focus theft here would send the user's paste into our
    /// overlay instead of their document, which is the single most damaging bug this app could ship.
    /// </para>
    /// </summary>
    public static void MakeNonActivating(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var current = (long)GetWindowLongPtr(handle, GWL_EXSTYLE);
        var updated = current | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;

        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(updated));
    }

    /// <summary>DPI scale factor (1.0 == 96 DPI) for the monitor containing a physical point.</summary>
    public static double GetScaleForPoint(int x, int y)
    {
        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);

        if (monitor == IntPtr.Zero)
        {
            return 1.0;
        }

        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0
            ? dpiX / 96.0
            : 1.0;
    }

    /// <summary>
    /// Work area of the monitor containing a physical point, converted to WPF units. Work area
    /// rather than full bounds so the overlay never hides behind the taskbar.
    /// </summary>
    public static Rect GetWorkAreaForPoint(int x, int y, double scale)
    {
        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);

        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFOEX
            {
                cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty,
            };

            if (GetMonitorInfo(monitor, ref info))
            {
                return new Rect(
                    info.rcWork.Left / scale,
                    info.rcWork.Top / scale,
                    (info.rcWork.Right - info.rcWork.Left) / scale,
                    (info.rcWork.Bottom - info.rcWork.Top) / scale);
            }
        }

        return new Rect(
            0,
            0,
            SystemParameters.WorkArea.Width,
            SystemParameters.WorkArea.Height);
    }
}
