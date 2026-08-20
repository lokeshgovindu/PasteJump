using PasteJump.Core.PasteMode;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The rules for noticing that Windows has dropped our keyboard hook. Every one of these is a state the
/// application can be left in with no way out but a restart, which is what makes them worth pinning.
/// </summary>
public class HookHealthPolicyTests
{
    private static HookHealthDecision Decide(
        bool gestureEnabled = true,
        bool hookInstalled = true,
        bool sessionActive = false,
        bool ctrlHeld = false,
        double msSinceLastHookEvent = 0,
        double msCtrlHeldFor = 0)
        => HookHealthPolicy.Decide(
            gestureEnabled,
            hookInstalled,
            sessionActive,
            ctrlHeld,
            msSinceLastHookEvent,
            msCtrlHeldFor);

    [Fact]
    public void AnIdleMachineNeedsNothing()
    {
        Assert.False(Decide(msSinceLastHookEvent: 60_000).AnythingToDo);
    }

    /// <summary>
    /// The reported case: Ctrl held, nothing heard, no session opened. We must have missed the Ctrl key-down,
    /// which is the one event we can be sure was delivered to somebody.
    /// </summary>
    [Fact]
    public void CtrlHeldWithNothingHeardMeansWeAreDeaf()
    {
        var decision = Decide(ctrlHeld: true, msCtrlHeldFor: 600, msSinceLastHookEvent: 600);

        Assert.True(decision.ReinstallHook);
        Assert.False(decision.AbandonStuckSession);
    }

    /// <summary>
    /// Both conditions are needed. Silence on its own is an idle machine, and Ctrl going down while we are
    /// listening produces an event - which is what keeps this from firing during ordinary use.
    /// </summary>
    [Fact]
    public void SilenceAloneIsNotEvidence()
    {
        Assert.False(Decide(ctrlHeld: false, msSinceLastHookEvent: 5_000).AnythingToDo);
    }

    [Fact]
    public void CtrlHeldButHeardRecentlyIsHealthy()
    {
        Assert.False(Decide(ctrlHeld: true, msCtrlHeldFor: 5_000, msSinceLastHookEvent: 20).AnythingToDo);
    }

    [Fact]
    public void CtrlOnlyJustDownIsNotYetEvidence()
    {
        Assert.False(Decide(ctrlHeld: true, msCtrlHeldFor: 80, msSinceLastHookEvent: 80).AnythingToDo);
    }

    /// <summary>
    /// The stuck overlay, and the reason it happens: the recogniser reconciles a missed Ctrl release only when a
    /// key event arrives, and a dead hook delivers none.
    /// </summary>
    [Fact]
    public void ASessionOpenWithCtrlPhysicallyUpIsAbandoned()
    {
        var decision = Decide(sessionActive: true, ctrlHeld: false, msSinceLastHookEvent: 3_000);

        Assert.True(decision.AbandonStuckSession);
        Assert.True(decision.ReinstallHook);
    }

    /// <summary>
    /// Reinstalled but NOT abandoned. Silence during a gesture is ordinary - somebody reading the overlay - so
    /// ending the session on that basis would throw away a paste they were still thinking about.
    /// </summary>
    [Fact]
    public void ALongSilenceDuringASessionReinstallsWithoutEndingIt()
    {
        var decision = Decide(sessionActive: true, ctrlHeld: true, msSinceLastHookEvent: 2_000);

        Assert.True(decision.ReinstallHook);
        Assert.False(decision.AbandonStuckSession);
    }

    [Fact]
    public void AShortPauseDuringASessionIsLeftAlone()
    {
        Assert.False(Decide(sessionActive: true, ctrlHeld: true, msSinceLastHookEvent: 300).AnythingToDo);
    }

    /// <summary>
    /// The one that would be a bug rather than an oversight: "disabled" means the hook is uninstalled on purpose,
    /// so a watchdog that reinstalled it would hand Ctrl+V back from whatever the user gave it to.
    /// </summary>
    [Fact]
    public void NothingIsEverReinstalledWhileTheUserHasSwitchedItOff()
    {
        foreach (var ctrl in new[] { true, false })
        {
            foreach (var session in new[] { true, false })
            {
                var decision = HookHealthPolicy.Decide(
                    gestureEnabled: false,
                    hookInstalled: false,
                    sessionActive: session,
                    ctrlHeld: ctrl,
                    msSinceLastHookEvent: 30_000,
                    msCtrlHeldFor: 30_000);

                Assert.False(decision.AnythingToDo);
            }
        }
    }

    /// <summary>
    /// A session cannot be left stranded just because the hook is already gone - the overlay still has to come
    /// down. Nothing is reinstalled, because there is nothing installed to replace.
    /// </summary>
    [Fact]
    public void AStrandedSessionIsStillAbandonedWhenTheHookIsAlreadyGone()
    {
        var decision = Decide(hookInstalled: false, sessionActive: true, ctrlHeld: false);

        Assert.True(decision.AbandonStuckSession);
        Assert.False(decision.ReinstallHook);
    }

    [Fact]
    public void AnUninstalledHookIsNotReinstalledOnDeafnessEvidenceAlone()
    {
        // Nothing to diagnose: with no hook installed and no session open, the application is simply not
        // listening, and only the code that uninstalled it knows whether that was intended.
        Assert.False(
            Decide(hookInstalled: false, ctrlHeld: true, msCtrlHeldFor: 5_000, msSinceLastHookEvent: 5_000)
                .AnythingToDo);
    }
}
