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
        // Nothing heard for a minute, and Ctrl has been down for over half a second of it: the Ctrl-down never
        // reached us.
        var decision = Decide(ctrlHeld: true, msCtrlHeldFor: 600, msSinceLastHookEvent: 60_000);

        Assert.True(decision.ReinstallHook);
        Assert.False(decision.AbandonStuckSession);
    }

    /// <summary>
    /// The false positive the first version shipped with, caught within a minute of deploying it. Holding Ctrl
    /// while reading the overlay sends no further key events, so the silence grows past any fixed threshold on a
    /// perfectly healthy hook - which reported a recovery, and told the user so, for nothing.
    /// </summary>
    [Theory]
    [InlineData(600)]
    [InlineData(1_500)]
    [InlineData(10_000)]
    [InlineData(120_000)]
    public void AnInnocentLongCtrlHoldIsNeverMistakenForDeafness(double heldFor)
    {
        // The Ctrl-down WAS received, so the silence dates from it - never appreciably older than the hold.
        Assert.False(Decide(ctrlHeld: true, msCtrlHeldFor: heldFor, msSinceLastHookEvent: heldFor + 250).AnythingToDo);
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
    /// Nothing is concluded from silence during an open session, however long. Somebody holding Ctrl while they
    /// read the overlay sends no further keys, so there is no evidence to act on - and acting anyway is what
    /// announced a phantom recovery to the user. Deafness mid-gesture is caught the moment they let go of Ctrl,
    /// which needs no hook to notice.
    /// </summary>
    [Theory]
    [InlineData(300)]
    [InlineData(2_000)]
    [InlineData(60_000)]
    public void SilenceDuringAnOpenSessionIsNeverEvidence(double silence)
    {
        Assert.False(Decide(sessionActive: true, ctrlHeld: true, msSinceLastHookEvent: silence).AnythingToDo);
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
