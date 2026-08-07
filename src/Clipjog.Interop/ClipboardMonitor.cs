using Clipjog.Interop.Win32;

namespace Clipjog.Interop;

/// <summary>
/// Raises an event whenever the system clipboard changes.
/// <para>
/// Uses <c>AddClipboardFormatListener</c> rather than the legacy
/// <c>SetClipboardViewer</c> chain. The old chain requires every participant to forward messages
/// correctly, so one badly written clipboard tool anywhere on the machine silently breaks
/// everyone downstream of it. The listener API has no such coupling.
/// </para>
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private readonly MessageOnlyWindow _window;
    private readonly bool _ownsWindow;
    private bool _listening;
    private bool _disposed;

    /// <summary>Raised on the thread that owns the message window.</summary>
    public event Action? ClipboardChanged;

    public ClipboardMonitor(MessageOnlyWindow? window = null)
    {
        _ownsWindow = window is null;
        _window = window ?? new MessageOnlyWindow("_clip");
        _window.MessageReceived += OnMessage;
    }

    public bool IsListening => _listening;

    public void Start()
    {
        if (_listening)
        {
            return;
        }

        _listening = NativeMethods.AddClipboardFormatListener(_window.Handle);
    }

    public void Stop()
    {
        if (!_listening)
        {
            return;
        }

        NativeMethods.RemoveClipboardFormatListener(_window.Handle);
        _listening = false;
    }

    private IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeConstants.WM_CLIPBOARDUPDATE)
        {
            return null;
        }

        ClipboardChanged?.Invoke();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _window.MessageReceived -= OnMessage;

        if (_ownsWindow)
        {
            _window.Dispose();
        }
    }
}
