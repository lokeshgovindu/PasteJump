namespace PasteJump.Core.PasteMode;

/// <summary>What a health check concluded should be done about the keyboard hook.</summary>
/// <param name="ReinstallHook">
/// Reinstall it: the evidence says we are no longer receiving keys. Cheap and harmless when the diagnosis is
/// wrong, which is deliberate - the cost of a needless <c>SetWindowsHookEx</c> is microseconds, and the cost of
/// staying deaf is that Ctrl+V silently stops working.
/// </param>
/// <param name="AbandonStuckSession">
/// End the open session, restore the clipboard and take the overlay down. Its Ctrl release never arrived.
/// </param>
public readonly record struct HookHealthDecision(bool ReinstallHook, bool AbandonStuckSession)
{
    public static HookHealthDecision Nothing => new(false, false);

    public bool AnythingToDo => ReinstallHook || AbandonStuckSession;
}

/// <summary>
/// Decides when the keyboard hook has stopped receiving keys, and when an open session has been left stranded.
/// </summary>
/// <remarks>
/// <para>
/// Exists because <b>Windows silently discards a low-level hook whose callback exceeds
/// <c>LowLevelHooksTimeout</c></b>, and gives no notification whatsoever. Under heavy load that is not
/// hypothetical: reported 2026-08-20 as the machine at 100% CPU, a paste into the Run dialog leaving the overlay
/// stuck, and Ctrl+V thereafter pasting straight through with no overlay at all - which is exactly what a dropped
/// hook looks like from outside. Until this existed the only recovery was restarting the application.
/// </para>
/// <para>
/// <b>The recogniser already reconciles a missed Ctrl release - but only when a key event arrives</b>
/// (<c>PasteGestureRecognizer.Handle</c>), and a dead hook delivers none. That is why the overlay stays on screen
/// for ever rather than being cleaned up by the next keystroke: there is no next keystroke.
/// </para>
/// <para>
/// Pure and in Core so the thresholds and the reasoning are testable. Reading the keyboard and reinstalling the
/// hook belong to the caller; this only decides.
/// </para>
/// </remarks>
public static class HookHealthPolicy
{
    /// <summary>
    /// How long Ctrl must be physically down, with nothing heard from the hook, before we conclude we are deaf.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. Holding Ctrl for a shortcut normally produces a Ctrl-down event, which resets the
    /// silence - so reaching this threshold means we genuinely missed a key we should have seen, rather than that
    /// the user is being slow.
    /// </remarks>
    public const double DefaultDeafnessMs = 500;

    /// <summary>
    /// How long a session may go without a single keystroke before the hook is reinstalled as a precaution.
    /// </summary>
    /// <remarks>
    /// Only reinstalls; deliberately does <b>not</b> abandon the session. Silence during a gesture is ordinary -
    /// the user is reading the overlay - so ending it on that basis alone would throw away a paste somebody was
    /// still thinking about. Reinstalling costs nothing and, if we had gone deaf, is what lets the Ctrl release
    /// reach us and commit the paste normally.
    /// </remarks>
    public const double DefaultStuckSessionMs = 1500;

    /// <param name="gestureEnabled">
    /// False when the user has switched PasteJump off from the tray. Nothing is ever reinstalled then: an
    /// uninstalled hook is the <i>point</i> of that state, and resurrecting it would hand Ctrl+V back to an
    /// application the user had deliberately given it to.
    /// </param>
    /// <param name="hookInstalled">Whether we believe the hook is installed. Nothing to reinstall if not.</param>
    /// <param name="sessionActive">Whether a paste-mode session is open.</param>
    /// <param name="ctrlHeld">Whether Ctrl is physically down, read live from the keyboard.</param>
    /// <param name="msSinceLastHookEvent">Milliseconds since the hook last called us.</param>
    /// <param name="msCtrlHeldFor">Milliseconds Ctrl has been continuously down, or 0 when it is up.</param>
    public static HookHealthDecision Decide(
        bool gestureEnabled,
        bool hookInstalled,
        bool sessionActive,
        bool ctrlHeld,
        double msSinceLastHookEvent,
        double msCtrlHeldFor,
        double deafnessMs = DefaultDeafnessMs,
        double stuckSessionMs = DefaultStuckSessionMs)
    {
        if (!gestureEnabled)
        {
            return HookHealthDecision.Nothing;
        }

        // A session open while Ctrl is physically up can only be one whose Ctrl release never arrived. Abandoned
        // rather than committed, for the reason the recogniser gives in the same situation: releasing Ctrl is what
        // asks for a paste, and this is precisely the case where we do not know that the user did.
        if (sessionActive && !ctrlHeld)
        {
            return new HookHealthDecision(ReinstallHook: hookInstalled, AbandonStuckSession: true);
        }

        if (!hookInstalled)
        {
            return HookHealthDecision.Nothing;
        }

        if (sessionActive)
        {
            return msSinceLastHookEvent >= stuckSessionMs
                ? new HookHealthDecision(ReinstallHook: true, AbandonStuckSession: false)
                : HookHealthDecision.Nothing;
        }

        // Ctrl has been down a while and we have heard nothing at all in that time - so we missed its key-down,
        // which is the one event we can be certain was delivered to somebody. Both conditions are needed: the
        // silence alone is just an idle machine.
        var deaf = ctrlHeld
            && msCtrlHeldFor >= deafnessMs
            && msSinceLastHookEvent >= deafnessMs;

        return deaf
            ? new HookHealthDecision(ReinstallHook: true, AbandonStuckSession: false)
            : HookHealthDecision.Nothing;
    }
}
