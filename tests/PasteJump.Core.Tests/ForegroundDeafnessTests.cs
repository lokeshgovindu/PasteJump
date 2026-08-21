using PasteJump.Core.PasteMode;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Noticing that the hook hears nothing in one application while working everywhere else.
/// <para>
/// Half of these tests are about <em>not</em> reporting. That is the hard half: silence from an application is
/// ambiguous - somebody reading a page with the mouse produces exactly the same evidence as somebody being
/// deafened - so a rule that fires easily would accuse an innocent application, which is worse than saying
/// nothing. The measured case that motivated the feature is pinned at the bottom.
/// </para>
/// </summary>
public sealed class ForegroundDeafnessTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The reported shape: one application silent through many long visits while others deliver keys.
    /// </summary>
    [Fact]
    public void An_application_that_never_delivers_a_key_is_reported()
    {
        var tracker = new ForegroundDeafnessTracker();

        Working(tracker);

        for (var visit = 0; visit < 4; visit++)
        {
            tracker.NoteFocusSpell("msedge.exe", Minute, keysHeard: 0);
        }

        var notice = tracker.TryClaimNotice();

        Assert.NotNull(notice);
        Assert.Equal("msedge.exe", notice!.Value.Application);
        Assert.Equal(4, notice.Value.Spells);
        Assert.False(notice.Value.Corroborated);
    }

    /// <summary>
    /// The notice is said once per application, not once per watchdog tick - and the watchdog asks four times a
    /// second, so a missing claim would mean a toast storm rather than a cosmetic repeat.
    /// </summary>
    [Fact]
    public void An_application_is_reported_only_once()
    {
        var tracker = new ForegroundDeafnessTracker();

        Working(tracker);

        for (var visit = 0; visit < 6; visit++)
        {
            tracker.NoteFocusSpell("msedge.exe", Minute, keysHeard: 0);
        }

        Assert.NotNull(tracker.TryClaimNotice());
        Assert.Null(tracker.TryClaimNotice());
        Assert.Null(tracker.TryClaimNotice());
    }

    /// <summary>
    /// Nothing is concluded until other applications have proved the hook works. Without that comparison the
    /// honest diagnosis is a dead hook, which is <c>HookHealthPolicy</c>'s job - and blaming one application for
    /// a machine-wide silence would send the user hunting for a policy that does not exist.
    /// </summary>
    [Fact]
    public void Nothing_is_reported_while_no_other_application_has_been_heard()
    {
        var tracker = new ForegroundDeafnessTracker();

        for (var visit = 0; visit < 10; visit++)
        {
            tracker.NoteFocusSpell("msedge.exe", Minute, keysHeard: 0);
        }

        Assert.Null(tracker.TryClaimNotice());

        // One working application is still not a comparison; two is.
        tracker.NoteFocusSpell("devenv.exe", Minute, keysHeard: 40);
        Assert.Null(tracker.TryClaimNotice());

        tracker.NoteFocusSpell("WindowsTerminal.exe", Minute, keysHeard: 200);
        Assert.NotNull(tracker.TryClaimNotice());
    }

    /// <summary>
    /// One key exonerates an application for the rest of the run. A per-application filter does not come and go
    /// within a session, so forgetting would let a quiet afternoon in a working application produce a false
    /// report - which is the failure mode this whole class is built to avoid.
    /// </summary>
    [Fact]
    public void One_key_exonerates_an_application_for_the_whole_run()
    {
        var tracker = new ForegroundDeafnessTracker();

        Working(tracker);

        tracker.NoteFocusSpell("msedge.exe", TimeSpan.FromSeconds(2), keysHeard: 1);

        for (var visit = 0; visit < 20; visit++)
        {
            tracker.NoteFocusSpell("msedge.exe", Minute, keysHeard: 0);
        }

        Assert.Null(tracker.TryClaimNotice());
    }

    /// <summary>
    /// A window left in the foreground while nobody is at the machine is the commonest innocent explanation, so
    /// total time alone must not be enough.
    /// </summary>
    [Fact]
    public void One_long_unattended_visit_is_not_enough()
    {
        var tracker = new ForegroundDeafnessTracker();

        Working(tracker);

        tracker.NoteFocusSpell("msedge.exe", TimeSpan.FromHours(3), keysHeard: 0);

        Assert.Null(tracker.TryClaimNotice());
    }

    /// <summary>
    /// And many brief glances are not enough either - clicking through windows racks up visits without ever
    /// being an attempt to type.
    /// </summary>
    [Fact]
    public void Many_brief_visits_are_not_enough()
    {
        var tracker = new ForegroundDeafnessTracker();

        Working(tracker);

        for (var visit = 0; visit < 50; visit++)
        {
            tracker.NoteFocusSpell("msedge.exe", TimeSpan.FromSeconds(1), keysHeard: 0);
        }

        Assert.Null(tracker.TryClaimNotice());
    }

    /// <summary>
    /// A copy made in an application we have never heard a key from is the one witness this failure cannot
    /// silence: capture rides <c>WM_CLIPBOARDUPDATE</c>, which no hook can suppress. It buys a much shorter wait.
    /// </summary>
    [Fact]
    public void A_copy_nobody_heard_reports_far_sooner()
    {
        var tracker = new ForegroundDeafnessTracker();

        Working(tracker);

        tracker.NoteClipboardActivity("msedge.exe");
        tracker.NoteFocusSpell("msedge.exe", TimeSpan.FromSeconds(20), keysHeard: 0);

        var notice = tracker.TryClaimNotice();

        Assert.NotNull(notice);
        Assert.True(notice!.Value.Corroborated);
    }

    /// <summary>
    /// A copy in a working application says nothing, and must not arm anything: it is heard from, so the
    /// corroboration has no subject.
    /// </summary>
    [Fact]
    public void A_copy_in_a_working_application_is_ignored()
    {
        var tracker = new ForegroundDeafnessTracker();

        Working(tracker);

        tracker.NoteFocusSpell("devenv.exe", TimeSpan.FromSeconds(30), keysHeard: 5);
        tracker.NoteClipboardActivity("devenv.exe");
        tracker.NoteFocusSpell("devenv.exe", TimeSpan.FromSeconds(30), keysHeard: 0);

        Assert.Null(tracker.TryClaimNotice());
    }

    /// <summary>An unidentifiable foreground - a secure desktop gives one - is not an application.</summary>
    [Fact]
    public void A_nameless_foreground_is_ignored()
    {
        var tracker = new ForegroundDeafnessTracker();

        Working(tracker);

        for (var visit = 0; visit < 10; visit++)
        {
            tracker.NoteFocusSpell(null, Minute, keysHeard: 0);
            tracker.NoteFocusSpell(string.Empty, Minute, keysHeard: 0);
        }

        Assert.Null(tracker.TryClaimNotice());
    }

    /// <summary>
    /// The notice has to admit that it is a guess - the same rule <c>RivalClipboardManagers</c> follows, and for
    /// the same reason: the evidence is consistent with somebody simply not typing. Asserted rather than trusted,
    /// because the hedge is exactly the sort of thing a later edit tightens into a false accusation.
    /// </summary>
    [Fact]
    public void The_notice_names_the_application_and_hedges()
    {
        var text = ForegroundDeafnessTracker.Describe(
            new DeafApplication("msedge.exe", TimeSpan.FromMinutes(4), 6, Corroborated: false));

        Assert.Contains("msedge.exe", text);
        Assert.Contains("If Ctrl+V does not open the overlay", text);
        Assert.Contains("endpoint security", text);

        // It must never claim PasteJump is at fault, nor that anything has been lost.
        Assert.Contains("clips are safe", text);

        // And the remedy it offers has to be one that exists: the history window has a Copy button and no Paste
        // button, and PasteJump cannot deliver a keystroke to the affected application either - so the only
        // honest advice is to copy there and paste by hand.
        Assert.Contains("press Copy", text);
        Assert.Contains("your own Ctrl+V", text);
    }

    /// <summary>
    /// Not elevated: offer elevation, which is the measured remedy - an elevated hook received keys inside the
    /// affected application at the same moments a medium-integrity one received none.
    /// </summary>
    [Fact]
    public void When_not_elevated_the_notice_offers_running_as_administrator()
    {
        var text = ForegroundDeafnessTracker.Describe(
            new DeafApplication("msedge.exe", TimeSpan.FromMinutes(4), 6, Corroborated: false),
            runningElevated: false);

        Assert.Contains("as administrator", text);
        Assert.Contains("fewer privileges", text);
    }

    /// <summary>
    /// Already elevated: the one remedy has been taken, so repeating it would read as the application not
    /// knowing its own state. Say what is left instead.
    /// </summary>
    [Fact]
    public void When_already_elevated_the_notice_does_not_repeat_the_advice()
    {
        var text = ForegroundDeafnessTracker.Describe(
            new DeafApplication("msedge.exe", TimeSpan.FromMinutes(4), 6, Corroborated: false),
            runningElevated: true);

        Assert.Contains("already running as administrator", text);
        Assert.DoesNotContain("Running PasteJump as administrator usually restores", text);
        Assert.Contains("press Copy", text);
        Assert.DoesNotContain("error", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The measured case, kept as a test so the thresholds cannot drift past it: on 2026-08-21 Edge held the
    /// foreground for 2,736 seconds across 49 spells and delivered 33 keys, all but 5 of them in three gestures
    /// before the break. Every spell after 07:55 delivered nothing.
    /// </summary>
    [Fact]
    public void The_reported_machine_would_have_been_told()
    {
        var tracker = new ForegroundDeafnessTracker();

        // Terminal and Visual Studio, which kept working all day.
        tracker.NoteFocusSpell("WindowsTerminal.exe", TimeSpan.FromMinutes(3), keysHeard: 314);
        tracker.NoteFocusSpell("devenv.exe", TimeSpan.FromMinutes(2), keysHeard: 223);

        // Edge after the break: the real spell lengths from the log.
        foreach (var seconds in new[] { 49.0, 53.1, 85.4, 30.5, 18.8, 426.9 })
        {
            tracker.NoteFocusSpell("msedge.exe", TimeSpan.FromSeconds(seconds), keysHeard: 0);
        }

        var notice = tracker.TryClaimNotice();

        Assert.NotNull(notice);
        Assert.Equal("msedge.exe", notice!.Value.Application);
    }

    /// <summary>Two applications delivering keys, which every positive case needs first.</summary>
    private static void Working(ForegroundDeafnessTracker tracker)
    {
        tracker.NoteFocusSpell("WindowsTerminal.exe", TimeSpan.FromSeconds(30), keysHeard: 120);
        tracker.NoteFocusSpell("devenv.exe", TimeSpan.FromSeconds(30), keysHeard: 80);
    }
}
