using System.Runtime.InteropServices;
using System.Windows;

namespace PasteJump.App.Services;

/// <summary>
/// Window-style and DPI plumbing for the overlay. Kept here rather than in PasteJump.Interop
/// because it is WPF-specific.
/// </summary>
internal static class WindowInterop
{
    private const int GWL_EXSTYLE = -20;

    /// <summary>
    /// Where Windows has actually put a window, for the trace: its device rectangle, its extended style, and
    /// whether Windows considers it visible.
    /// </summary>
    /// <remarks>
    /// Deliberately read from the HWND rather than from WPF. <c>Window.Left</c> is what the window asked for in
    /// device-independent units and <c>Window.IsVisible</c> is WPF's own bookkeeping; neither is evidence about
    /// the thing on the screen, and a disagreement between the two is exactly the kind of fault that presents as
    /// "the overlay is not there" while every property in the debugger looks right.
    /// </remarks>
    public static string DescribeWindowForTrace(System.Windows.Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;

            if (handle == IntPtr.Zero)
            {
                return "(no handle)";
            }

            var rect = GetWindowRect(handle, out var r)
                ? $"({r.Left},{r.Top})-({r.Right},{r.Bottom})"
                : "(unreadable)";

            var exStyle = (long)GetWindowLongPtr(handle, GWL_EXSTYLE);

            return $"0x{handle:X} {rect} ex=0x{exStyle:X8} visible={IsWindowVisible(handle)}";
        }
        catch (Exception ex)
        {
            return "(failed: " + ex.GetType().Name + ")";
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACERECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out TRACERECT rect);

    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const uint LWA_ALPHA = 0x00000002;

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>
    /// Gives a window the keyboard, even when another application owns the foreground.
    /// <para>
    /// <see cref="Window.Activate"/> alone is <em>usually</em> enough and is tried first, so the ordinary case
    /// stays ordinary. It is not always enough: Windows grants <c>SetForegroundWindow</c> only to a process that
    /// meets one of a short list of conditions - being the foreground process, having received the last input
    /// event, and so on - and a tray application opening a window in response to a hook meets none of them. When
    /// that happens WPF reports success and the window appears behind, unfocused, waiting for a mouse click.
    /// </para>
    /// <para>
    /// The fallback is the standard remedy: attach this thread's input queue to the foreground window's thread
    /// for the duration of one <c>SetForegroundWindow</c> call, which makes the two threads share the foreground
    /// entitlement, then detach immediately. Measured on this machine, seven ways of showing a window with
    /// another application in front: <c>ShowActivated="False"</c> and no <c>Activate</c> - what this app shipped -
    /// never took focus in four runs; <c>Activate()</c> took it in three runs of four; the attach-and-set pair
    /// took it in all four. Hence one then the other, rather than either alone.
    /// </para>
    /// <para>
    /// Detaching in a <c>finally</c> is not tidiness. Leaving two input queues attached makes the threads share
    /// keyboard state indefinitely, so a throw here would degrade every later keystroke in both processes.
    /// </para>
    /// </summary>
    public static void BringToFrontAndFocus(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.Activate();

        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            return;
        }

        var foreground = GetForegroundWindow();

        if (foreground == handle)
        {
            return;
        }

        // Nothing to borrow entitlement from, so the plain call is the whole of what can be done.
        if (foreground == IntPtr.Zero)
        {
            SetForegroundWindow(handle);
            return;
        }

        var theirThread = GetWindowThreadProcessId(foreground, out _);
        var ourThread = GetCurrentThreadId();

        if (theirThread == 0 || theirThread == ourThread)
        {
            SetForegroundWindow(handle);
            return;
        }

        var attached = AttachThreadInput(ourThread, theirThread, true);

        try
        {
            SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(ourThread, theirThread, false);
            }
        }
    }

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

    /// <summary>
    /// Asks DWM to round a window's corners, and to draw its border in a given colour.
    /// <para>
    /// This is what makes it possible to drop <c>AllowsTransparency</c>. WPF renders a layered window's text
    /// with greyscale antialiasing rather than ClearType, so any window using transparency to get rounded
    /// corners pays for them in text quality - which on 11-12px notification text is the difference between
    /// crisp and smudged. Letting DWM round an opaque window restores ClearType and hands the corners and the
    /// drop shadow to the compositor, which draws both better than we can.
    /// </para>
    /// <para>
    /// Windows 11 only. On Windows 10 the call fails and the window is a plain rectangle, which is exactly what
    /// Windows 10's own notifications look like, so the failure is ignored rather than worked around.
    /// </para>
    /// </summary>
    public static void ApplyRoundedCorners(IntPtr handle, System.Windows.Media.Color borderColor)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2.
        var preference = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));

        // DWMWA_BORDER_COLOR = 34, as 0x00BBGGRR. Without it the border follows the accent colour, which has
        // nothing to do with this app's palette.
        var colorRef = borderColor.R | (borderColor.G << 8) | (borderColor.B << 16);
        _ = DwmSetWindowAttribute(handle, 34, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Windows that have opted into a DWM-drawn border, so the colour can be re-pushed when the palette
    /// changes. The overlay and the toast are single instances reused for the app's lifetime, so this holds
    /// two entries in practice; the Closed handler is there for correctness rather than for pressure.
    /// </summary>
    private static readonly List<Window> RoundedWindows = [];

    /// <summary>
    /// Gives <paramref name="window"/> DWM-drawn rounded corners, a shadow and a themed border, taking the
    /// border colour from the palette's <c>BorderBrush</c> so it follows the theme rather than the system
    /// accent. Falls back to a mid grey if that resource is missing, which only happens in a host that
    /// composed the resource set by hand and forgot one.
    /// </summary>
    public static void ApplyRoundedCorners(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!RoundedWindows.Contains(window))
        {
            RoundedWindows.Add(window);
            window.Closed += (_, _) => RoundedWindows.Remove(window);
        }

        var border = window.TryFindResource("BorderBrush") is System.Windows.Media.SolidColorBrush brush
            ? brush.Color
            : System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80);

        ApplyRoundedCorners(new System.Windows.Interop.WindowInteropHelper(window).Handle, border);
    }

    /// <summary>
    /// Re-pushes the border colour for every window that called <see cref="ApplyRoundedCorners(Window)"/>.
    /// <para>
    /// Necessary because this colour is handed to DWM once through an API call, not bound - so unlike every
    /// <c>DynamicResource</c> in the XAML it does not follow a palette swap on its own, and the overlay and
    /// toast both outlive any number of theme changes. Same class of problem as the title bar.
    /// </para>
    /// </summary>
    public static void RefreshThemedBorders()
    {
        // Copied, because ApplyRoundedCorners can mutate the list through its Closed subscription.
        foreach (var window in RoundedWindows.ToArray())
        {
            ApplyRoundedCorners(window);
        }
    }

    /// <summary>
    /// Sets whole-window alpha through Win32, for fading a window that is deliberately not using
    /// <c>AllowsTransparency</c>.
    /// <para>
    /// <see cref="UIElement.Opacity"/> on a <see cref="Window"/> does nothing without
    /// <c>AllowsTransparency</c> - there is no alpha channel for WPF to composite into, so the animation runs,
    /// the property changes, and the window stays solid until it is hidden. That is exactly what happened to
    /// the toast when transparency was removed for the sake of ClearType: the fade silently became a pop.
    /// </para>
    /// <para>
    /// <c>WS_EX_LAYERED</c> with <c>LWA_ALPHA</c> is a different mechanism and does not cost ClearType. WPF
    /// still renders opaquely into the window; the compositor applies one alpha value to the finished result.
    /// </para>
    /// </summary>
    public static void SetWindowAlpha(IntPtr handle, double alpha)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var current = (long)GetWindowLongPtr(handle, GWL_EXSTYLE);

        if ((current & WS_EX_LAYERED) == 0)
        {
            SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(current | WS_EX_LAYERED));
        }

        var value = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
        _ = SetLayeredWindowAttributes(handle, 0, value, LWA_ALPHA);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    /// <summary>
    /// Rounds a device-independent coordinate so it lands on a whole device pixel.
    /// <para>
    /// Needed because window positions here are computed as <c>physicalPixels / scale</c>, which at any
    /// non-integer scale factor produces a fractional result - at 150% a great many anchor positions land on
    /// a half device pixel. WPF then renders the entire window, text included, offset by half a pixel, and
    /// the result is a uniformly soft window that no amount of text-rendering configuration will sharpen.
    /// <c>UseLayoutRounding</c> does not help: it rounds layout <em>within</em> the window, not the window's
    /// own origin.
    /// </para>
    /// </summary>
    public static double SnapToDevicePixel(double deviceIndependentValue, double scale)
        => scale <= 0 ? deviceIndependentValue : Math.Round(deviceIndependentValue * scale) / scale;

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
