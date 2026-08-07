using System.Runtime.InteropServices;
using PasteJump.Interop.Win32;

namespace PasteJump.Interop;

/// <summary>
/// A hidden <c>HWND_MESSAGE</c> window. Needed because several Win32 facilities we depend on -
/// the clipboard listener and the tray icon - deliver their notifications as window messages and
/// therefore require an HWND, but we have no visible window to hang them off.
/// </summary>
public sealed class MessageOnlyWindow : IDisposable
{
    private readonly NativeMethods.WndProc _wndProc;
    private readonly string _className;
    private bool _disposed;

    /// <summary>Raised for every message. Return a value to handle it, or null to defer to Windows.</summary>
    public event Func<uint, IntPtr, IntPtr, IntPtr?>? MessageReceived;

    public MessageOnlyWindow(string classNameSuffix = "")
    {
        // Unique class name per instance: RegisterClassEx fails on a duplicate, which would
        // otherwise break a second instance or a restart-in-place.
        _className = $"PasteJumpMsgWnd_{Environment.ProcessId}_{Guid.NewGuid():n}{classNameSuffix}";

        // Held in a field so the GC cannot collect the delegate while Windows still holds the
        // native function pointer. Losing this is a classic, hard-to-diagnose crash.
        _wndProc = WindowProcedure;

        var moduleHandle = NativeMethods.GetModuleHandle(null);

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = moduleHandle,
            lpszClassName = _className,
        };

        if (NativeMethods.RegisterClassEx(ref wndClass) == 0)
        {
            throw new InvalidOperationException(
                $"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        Handle = NativeMethods.CreateWindowEx(
            0,
            _className,
            null,
            0,
            0,
            0,
            0,
            0,
            NativeConstants.HWND_MESSAGE,
            IntPtr.Zero,
            moduleHandle,
            IntPtr.Zero);

        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }
    }

    public IntPtr Handle { get; private set; }

    private IntPtr WindowProcedure(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var handled = MessageReceived?.Invoke(msg, wParam, lParam);

        return handled ?? NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Handle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
