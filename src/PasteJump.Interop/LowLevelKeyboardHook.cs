using System.Diagnostics;
using System.Runtime.InteropServices;
using PasteJump.Interop.Win32;

namespace PasteJump.Interop;

/// <summary>A single key transition seen by the hook.</summary>
/// <param name="IsInjected">
/// <c>LLKHF_INJECTED</c> was set. True for any synthetic input, whoever produced it.
/// </param>
/// <param name="IsOwnInjection">
/// The event carries our <see cref="NativeConstants.PasteJumpInputSignature"/>, so it is a keystroke
/// this process synthesised. Prefer this over <paramref name="IsInjected"/> for loop-guarding:
/// treating all injected input as ours makes the gesture unusable under Remote Desktop, in VM guest
/// windows, and for anyone driving the keyboard from a macro tool or on-screen keyboard.
/// </param>
public readonly record struct KeyboardHookEvent(
    int VirtualKey,
    bool IsKeyDown,
    bool IsInjected,
    bool IsOwnInjection);

/// <summary>
/// A <c>WH_KEYBOARD_LL</c> hook.
/// <para>
/// This exists because <c>RegisterHotKey</c> fundamentally cannot express the gesture. A
/// registered hotkey fires once per chord; it has no way to say "Ctrl is still down and V was
/// tapped again", which is the entire interaction model of this app.
/// </para>
/// <para>
/// Two hard constraints come with it. First, the callback runs on the thread that installed the
/// hook and blocks all keyboard input machine-wide until it returns - exceed
/// <c>LowLevelHooksTimeout</c> (300 ms by default) and Windows silently discards the hook, at
/// which point the app appears to work but has stopped receiving keys. So the handler must stay
/// cheap and must never throw. Second, our own synthesised keystrokes come back through here,
/// which is what <see cref="KeyboardHookEvent.IsInjected"/> is for - without that check, sending
/// Ctrl+V to paste would re-enter paste mode forever.
/// </para>
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly NativeMethods.HookProc _callback;
    private readonly Func<KeyboardHookEvent, bool> _handler;
    private IntPtr _hook;
    private bool _disposed;

    /// <summary>
    /// Rolling count of handler exceptions. A throwing handler would tear down the hook, so they
    /// are swallowed here; this counter exists so the failure is at least observable.
    /// </summary>
    public int HandlerFaultCount { get; private set; }

    /// <summary>Longest handler execution seen, for verifying we stay well inside the OS timeout.</summary>
    public TimeSpan WorstHandlerDuration { get; private set; }

    /// <param name="handler">
    /// Returns true to swallow the keystroke, false to let it through. Must be fast and must not
    /// block.
    /// </param>
    public LowLevelKeyboardHook(Func<KeyboardHookEvent, bool> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));

        // Field-held for the same reason as the window procedure: Windows keeps a raw function
        // pointer and the GC must not move or collect the delegate behind it.
        _callback = HookCallback;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>
    /// Installs the hook. Must be called from a thread with a running message pump - the WPF UI
    /// thread qualifies.
    /// </summary>
    public void Install()
    {
        if (IsInstalled)
        {
            return;
        }

        // A low-level hook is global, so hMod is ignored and the thread id must be 0. Passing the
        // current module handle here is a widespread copy-paste error that happens to work.
        _hook = NativeMethods.SetWindowsHookEx(
            NativeConstants.WH_KEYBOARD_LL,
            _callback,
            IntPtr.Zero,
            0);

        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"SetWindowsHookEx(WH_KEYBOARD_LL) failed: {Marshal.GetLastWin32Error()}");
        }
    }

    public void Uninstall()
    {
        if (!IsInstalled)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>How many times the hook has been reinstalled because it appeared to have stopped receiving keys.</summary>
    /// <remarks>
    /// Diagnostics, and the number to ask for when somebody reports that Ctrl+V went quiet: a value climbing here
    /// means this machine really is losing hook events, rather than that PasteJump has a logic bug.
    /// </remarks>
    public int ReinstallCount { get; private set; }

    /// <summary>
    /// Uninstalls and installs again, to recover from Windows having silently discarded the hook.
    /// </summary>
    /// <remarks>
    /// There is no API that answers "is my hook still registered" - Windows drops a hook whose callback exceeded
    /// <c>LowLevelHooksTimeout</c> and tells nobody - so recovery cannot be conditional on detecting it directly.
    /// Reinstalling unconditionally when the evidence points that way is the whole technique, and it is safe to do
    /// when the diagnosis was wrong: the handle is replaced, no queued input is lost, and the cost is a pair of
    /// user-mode calls.
    /// <para>
    /// Uninstall first even though the old handle may already be dead: <c>UnhookWindowsHookEx</c> on a discarded
    /// hook simply fails, while leaking the handle each time would eventually run the process out of hooks.
    /// </para>
    /// </remarks>
    public void Reinstall()
    {
        Uninstall();
        Install();
        ReinstallCount++;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Reading the structure is the only thing that has to happen here; deciding what it MEANS lives in
        // KeyboardHookDecoder, which is pure and therefore testable - installing a real hook needs a message loop
        // and a live keyboard, so every decision left in this method is a decision nothing can check.
        KeyboardHookEvent? decoded;

        try
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            decoded = KeyboardHookDecoder.Decode(
                nCode,
                (int)wParam,
                (int)info.vkCode,
                info.flags,
                info.dwExtraInfo,
                NativeConstants.PasteJumpInputSignature);
        }
        catch (Exception)
        {
            HandlerFaultCount++;
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        if (decoded is not { } keyEvent)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        bool swallow;

        try
        {
            var start = Stopwatch.GetTimestamp();

            swallow = _handler(keyEvent);

            var elapsed = Stopwatch.GetElapsedTime(start);

            if (elapsed > WorstHandlerDuration)
            {
                WorstHandlerDuration = elapsed;
            }
        }
        catch (Exception)
        {
            // Letting this propagate into native code would tear the hook down and leave the
            // machine's keyboard handling in our debt. Count it and pass the key through.
            HandlerFaultCount++;
            swallow = false;
        }

        return swallow
            ? new IntPtr(1)
            : NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Uninstall();
    }
}
