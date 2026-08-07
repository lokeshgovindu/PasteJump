using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Clipjog.Interop.Probe;

/// <summary>
/// Stand-in for the real overlay, used to verify the non-activating window styles and DPI-aware
/// positioning in isolation.
/// </summary>
public partial class ProbeOverlay : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public ProbeOverlay()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var current = (long)GetWindowLongPtr(handle, GWL_EXSTYLE);

        SetWindowLongPtr(
            handle,
            GWL_EXSTYLE,
            new IntPtr(current | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW));
    }

    /// <summary>Positions at a physical screen point, converting through the monitor's DPI.</summary>
    public void ShowAt(int physicalX, int physicalY, uint dpi)
    {
        var scale = dpi / 96.0;

        Left = physicalX / scale;
        Top = (physicalY / scale) + 20;

        DetailText.Text =
            $"anchor  : {physicalX},{physicalY} (physical){Environment.NewLine}" +
            $"dpi     : {dpi} (scale {scale:0.00}){Environment.NewLine}" +
            $"placed  : {Left:0.#},{Top:0.#} (WPF units)";
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
