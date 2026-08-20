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
    /// Slack for the fact that the watchdog only samples the keyboard every so often, so it always believes Ctrl
    /// went down slightly later than it did.
    /// </summary>
    /// <remarks>
    /// Load-bearing, and its absence was a false positive rather than a rounding error. The first version compared
    /// the silence against a fixed threshold, which fires on a perfectly healthy hook: hold Ctrl for a second
    /// while reading the overlay and no further key events arrive, so the silence grows past any fixed number even
    /// though the Ctrl-down was received. The question has to be <i>relative</i> - has anything been heard since
    /// Ctrl went down - and this covers the quarter second by which the sampler is always late.
    /// </remarks>
    public const double DefaultObservationMarginMs = 300;

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
        double observationMarginMs = DefaultObservationMarginMs)
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

        // Nothing is concluded from silence during an open session, and that is deliberate rather than a gap.
        // Silence there is ordinary - somebody holding Ctrl while they read the overlay sends no further keys - so
        // there is no evidence to act on. If we HAVE gone deaf mid-gesture, the rule above catches it the moment
        // the user gives up and lets go of Ctrl, which needs no hook at all to notice.
        if (sessionActive)
        {
            return HookHealthDecision.Nothing;
        }

        // Ctrl has been down a while and nothing has been heard for LONGER than it has been down - so we missed
        // its key-down, which is the one event we can be certain was delivered to somebody. Relative, not a fixed
        // threshold: an innocent long hold produces no further events either, and comparing against a constant
        // reports that as deafness. See DefaultObservationMarginMs.
        var heardNothingSinceCtrlWentDown =
            msSinceLastHookEvent >= msCtrlHeldFor + observationMarginMs;

        var deaf = ctrlHeld
            && msCtrlHeldFor >= deafnessMs
            && heardNothingSinceCtrlWentDown;

        return deaf
            ? new HookHealthDecision(ReinstallHook: true, AbandonStuckSession: false)
            : HookHealthDecision.Nothing;
    }
}
