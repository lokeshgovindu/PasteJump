using PasteJump.Interop.Win32;

namespace PasteJump.Interop;

/// <summary>
/// Lets a second copy of PasteJump ask the one already running to show itself, instead of exiting without a
/// word.
/// <para>
/// Silence was the problem worth fixing. A second instance has nothing to add - it would install a second
/// keyboard hook and fight the first over the clipboard and the database - so it must not keep running. But
/// PasteJump has no window, and its tray icon is often hidden in the notification-area overflow, so
/// double-clicking it and getting *nothing* is indistinguishable from a crash.
/// </para>
/// <para>
/// The mechanism is a window message posted to the running instance's message-only window. Two constraints
/// shape it, and both rule out the obvious alternatives:
/// </para>
/// <list type="bullet">
/// <item><c>HWND_BROADCAST</c> reaches only top-level windows, so it cannot find a message-only window. The
/// window has to be located with <c>FindWindowEx</c> rooted at <c>HWND_MESSAGE</c>.</item>
/// <item>Window messages do not cross session boundaries. The single-instance mutex is machine-wide, so the
/// holder may be another user's copy in another session - in which case there is nothing to find, and
/// <see cref="TryNotifyRunningInstance"/> says so rather than pretending it worked.</item>
/// </list>
/// </summary>
public static class SingleInstanceSignal
{
    /// <summary>
    /// Title of the running instance's message-only window. Carries the same suffix as the mutex so the pair
    /// is recognisable as one mechanism, and so it cannot collide with another application's window title.
    /// </summary>
    public const string WindowName = "PasteJump.SingleInstance.9F2C41A6";

    /// <summary>
    /// Interned message id, identical in every process that asks for this name. Preferred over a
    /// <c>WM_APP</c> offset, which is only unique within one window class and would be a silent collision if
    /// anything else ever posted to this window.
    /// </summary>
    private static readonly uint ShowRequest = NativeMethods.RegisterWindowMessage("PasteJump.ShowExisting");

    /// <summary>
    /// Interned message asking the running instance to shut down.
    /// </summary>
    /// <remarks>
    /// Exists for deployment. Replacing the executable means stopping the running copy first, and
    /// <c>Stop-Process</c> cannot touch an elevated one from an ordinary prompt - PasteJump legitimately runs
    /// elevated where endpoint security intercepts keyboard input above medium integrity. Asking it to leave
    /// works across that boundary, provided the elevated instance has opted in with
    /// <see cref="AllowRequestsFromLowerIntegrity"/>.
    /// <para>
    /// A request, not a kill: the instance shuts down through its own <c>Exit</c> path, so settings are saved
    /// and the database is closed cleanly rather than being cut off mid-write.
    /// </para>
    /// </remarks>
    private static readonly uint ExitRequest = NativeMethods.RegisterWindowMessage("PasteJump.RequestExit");

    /// <summary>Whether a received message is the "show yourself" request.</summary>
    public static bool IsShowRequest(uint message) => ShowRequest != 0 && message == ShowRequest;

    /// <summary>Whether a received message is the "please shut down" request.</summary>
    public static bool IsExitRequest(uint message) => ExitRequest != 0 && message == ExitRequest;

    /// <summary>
    /// Opts this window in to receiving our two requests from processes of lower integrity.
    /// </summary>
    /// <remarks>
    /// Only matters while elevated, and harmless otherwise - which is why it is called unconditionally rather
    /// than behind a privilege check that could get the answer wrong. Without it, an elevated PasteJump is
    /// unreachable: a deployment script cannot ask it to exit, and a second launch cannot ask it to show
    /// itself, so both appear to do nothing at all.
    /// <para>
    /// Per message, deliberately. Nothing else is let through.
    /// </para>
    /// </remarks>
    public static void AllowRequestsFromLowerIntegrity(IntPtr window)
    {
        const uint allow = 1;  // MSGFLT_ALLOW

        foreach (var message in new[] { ShowRequest, ExitRequest })
        {
            if (message != 0)
            {
                NativeMethods.ChangeWindowMessageFilterEx(window, message, allow, IntPtr.Zero);
            }
        }
    }

    /// <summary>
    /// Asks the running instance to shut down. False when there is none to reach.
    /// </summary>
    /// <remarks>
    /// Posted rather than sent, for the reason the show request is: the other instance's UI thread may be
    /// mid-gesture holding the keyboard hook, and blocking on it would block every keystroke on the machine.
    /// The caller therefore has to wait for the process to actually exit rather than assuming it has.
    /// </remarks>
    public static bool TryRequestExit()
    {
        if (ExitRequest == 0)
        {
            return false;
        }

        var target = NativeMethods.FindWindowEx(
            NativeConstants.HWND_MESSAGE,
            IntPtr.Zero,
            null,
            WindowName);

        return target != IntPtr.Zero
            && NativeMethods.PostMessage(target, ExitRequest, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Asks the running instance to surface itself. Returns false when there is none to reach - no window in
    /// this session, or the post failed - in which case the caller should say something to the user rather
    /// than exit silently.
    /// </summary>
    public static bool TryNotifyRunningInstance()
    {
        if (ShowRequest == 0)
        {
            return false;
        }

        // Null class, matching title: the class name is unique per instance by design, so the title is the
        // only stable handle on it.
        var target = NativeMethods.FindWindowEx(
            NativeConstants.HWND_MESSAGE,
            IntPtr.Zero,
            null,
            WindowName);

        if (target == IntPtr.Zero)
        {
            return false;
        }

        // No AllowSetForegroundWindow here, and that is worth a note rather than silence. The running instance
        // answers this with a toast, which is topmost and never activates, so it needs no foreground rights.
        // It WOULD need them if the answer ever became a real window: Windows grants SetForegroundWindow only
        // to a process that already has it, so the other instance could not raise its own window and it would
        // open behind everything - looking exactly like nothing happened. This process has the foreground (the
        // user just launched it) and would have to hand that right over first.
        //
        // Posted, not sent: SendMessage would block this process until the other one's UI thread got round to
        // it, and that thread may be mid-gesture with the keyboard hook held.
        return NativeMethods.PostMessage(target, ShowRequest, IntPtr.Zero, IntPtr.Zero);
    }
}
