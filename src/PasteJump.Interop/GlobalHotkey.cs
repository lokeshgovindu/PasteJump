using PasteJump.Core.Settings;
using PasteJump.Interop.Win32;

namespace PasteJump.Interop;

/// <summary>
/// A system-wide hotkey, delivered through the existing message-only window.
/// <para>
/// <c>RegisterHotKey</c> rather than the low-level hook, and the contrast with
/// <see cref="LowLevelKeyboardHook"/> is the point. The hook exists because the paste gesture needs to know
/// that Ctrl is still down while V is tapped again, which a registered hotkey cannot express. This is the
/// opposite shape: a chord that fires once and does one thing. Using the hook for it would mean adding a
/// second responsibility to a callback that blocks all keyboard input machine-wide while it runs, whereas
/// <c>WM_HOTKEY</c> arrives as an ordinary queued message with no such constraint.
/// </para>
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    /// <summary>
    /// Any value unique within this window. Hotkey ids are per-window, not global, so there is no risk of
    /// colliding with another process's choice.
    /// </summary>
    private const int HotkeyId = 0xA17;

    private readonly MessageOnlyWindow _window;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkey(MessageOnlyWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.MessageReceived += OnMessage;
    }

    /// <summary>Raised on the message thread when the chord is pressed.</summary>
    public event Action? Pressed;

    /// <summary>The chord currently registered, or the unset spec when none is.</summary>
    public HotkeySpec Current { get; private set; }

    /// <summary>
    /// Registers <paramref name="spec"/>, replacing whatever was registered before. Returns false when
    /// Windows refused - almost always because another application already owns the chord.
    /// <para>
    /// An unset or modifierless spec is treated as "none" and reported as success: there is nothing to
    /// register and nothing went wrong. See <see cref="HotkeySpec.HasModifier"/> for why a bare key is
    /// never accepted.
    /// </para>
    /// </summary>
    public bool TryRegister(HotkeySpec spec)
    {
        Unregister();

        if (!spec.IsValid)
        {
            return true;
        }

        var modifiers = NativeConstants.MOD_NOREPEAT;

        if (spec.Control)
        {
            modifiers |= NativeConstants.MOD_CONTROL;
        }

        if (spec.Alt)
        {
            modifiers |= NativeConstants.MOD_ALT;
        }

        if (spec.Shift)
        {
            modifiers |= NativeConstants.MOD_SHIFT;
        }

        if (spec.Windows)
        {
            modifiers |= NativeConstants.MOD_WIN;
        }

        _registered = NativeMethods.RegisterHotKey(
            _window.Handle,
            HotkeyId,
            modifiers,
            (uint)spec.VirtualKey);

        Current = _registered ? spec : default;
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
        _registered = false;
        Current = default;
    }

    private IntPtr? OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeConstants.WM_HOTKEY || (int)wParam != HotkeyId)
        {
            return null;
        }

        Pressed?.Invoke();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        _window.MessageReceived -= OnMessage;
    }
}
