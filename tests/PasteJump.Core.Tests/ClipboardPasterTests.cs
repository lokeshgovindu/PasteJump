using PasteJump.Core.Capture;
using PasteJump.Core.Model;
using PasteJump.Core.Paste;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The paste commit path. The rule under test throughout is that the Ctrl+V keystroke is strictly
/// conditional on the clipboard write having succeeded.
/// <para>
/// This matters because the failure it prevents is silent and actively misleading. Clipboard writes
/// do fail - the clipboard is a machine-wide lock any process can hold - and an unconditional
/// keystroke after a failed write makes the target application paste whatever was on the clipboard
/// beforehand. The user asked for clip 7, saw clip 7 in the overlay, and got their previous clipboard
/// contents with no indication anything went wrong.
/// </para>
/// </summary>
public sealed class ClipboardPasterTests
{
    private static IReadOnlyList<ClipPayload> Payloads(string text = "hello")
        => [new ClipPayload(13, null, System.Text.Encoding.Unicode.GetBytes(text))];

    private static (ClipboardPaster Paster, FakeClipboardAccess Clipboard, FakePasteSender Sender, ManualScheduler Scheduler, List<string> Messages) Build()
    {
        var clipboard = new FakeClipboardAccess();
        var sender = new FakePasteSender();
        var scheduler = new ManualScheduler();
        var messages = new List<string>();

        var paster = new ClipboardPaster(clipboard, sender, new SelfWriteGuard(), scheduler.Schedule);
        paster.Message += messages.Add;

        return (paster, clipboard, sender, scheduler, messages);
    }

    [Fact]
    public void Paste_sends_the_keystroke_after_a_successful_write()
    {
        var (paster, clipboard, sender, scheduler, _) = Build();

        paster.Write(Payloads(), thenPaste: true);

        // The keystroke is deliberately deferred by the settle delay, so nothing has been sent yet.
        Assert.Single(clipboard.Writes);
        Assert.Equal(0, sender.SendCount);

        scheduler.RunAll();

        Assert.Equal(1, sender.SendCount);
        Assert.Equal(1, paster.PasteCount);
        Assert.Equal(0, paster.AbandonedCount);
    }

    [Fact]
    public void Paste_is_not_sent_when_every_write_fails()
    {
        var (paster, clipboard, sender, scheduler, messages) = Build();
        clipboard.WriteSucceeds = false;

        paster.Write(Payloads(), thenPaste: true);
        scheduler.RunAll();

        // The regression this whole file exists for: no keystroke at all. Sending one would have
        // pasted the previous clipboard contents while the user believed they were pasting a clip.
        Assert.Equal(0, sender.SendCount);
        Assert.Equal(ClipboardPaster.MaxWriteAttempts, clipboard.Writes.Count);
        Assert.Equal(1, paster.AbandonedCount);
        Assert.Contains(messages, m => m.Contains("busy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_write_that_succeeds_on_retry_still_pastes_exactly_once()
    {
        var (paster, clipboard, sender, scheduler, _) = Build();
        clipboard.EnqueueWriteResult(false).EnqueueWriteResult(false).EnqueueWriteResult(true);

        paster.Write(Payloads(), thenPaste: true);
        scheduler.RunAll();

        Assert.Equal(3, clipboard.Writes.Count);
        Assert.Equal(2, paster.WriteFailureCount);
        Assert.Equal(1, paster.WriteCount);

        // Exactly one - a retry must not queue a second keystroke.
        Assert.Equal(1, sender.SendCount);
        Assert.Equal(0, paster.AbandonedCount);
    }

    [Fact]
    public void Retry_delays_back_off_rather_than_hammering_the_clipboard()
    {
        var (paster, clipboard, _, scheduler, _) = Build();
        clipboard.WriteSucceeds = false;

        paster.Write(Payloads(), thenPaste: true);
        scheduler.RunAll();

        // Bounded and increasing, unlike the original's unbounded OpenClipboard spin.
        var retryDelays = scheduler.Delays.Where(d => d >= ClipboardPaster.WriteRetryDelay).ToList();

        Assert.Equal(ClipboardPaster.MaxWriteAttempts - 1, retryDelays.Count);
        Assert.Equal(retryDelays.OrderBy(d => d).ToList(), retryDelays);
    }

    [Fact]
    public void A_refused_keystroke_is_reported_rather_than_failing_silently()
    {
        var (paster, _, sender, scheduler, messages) = Build();
        sender.Succeeds = false;

        paster.Write(Payloads(), thenPaste: true);
        scheduler.RunAll();

        Assert.Equal(1, sender.SendCount);
        Assert.Equal(1, paster.SendFailureCount);
        Assert.Equal(0, paster.PasteCount);

        // An elevated target window is the realistic cause, and the user can act on that.
        Assert.Contains(messages, m => m.Contains("administrator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Restoring_the_previous_clipboard_never_sends_a_keystroke()
    {
        var (paster, clipboard, sender, scheduler, _) = Build();

        paster.Write(Payloads(), thenPaste: false);
        scheduler.RunAll();

        Assert.Single(clipboard.Writes);
        Assert.Equal(0, sender.SendCount);
    }

    [Fact]
    public void A_failed_restore_is_retried_but_reports_nothing_to_the_user()
    {
        var (paster, clipboard, sender, scheduler, messages) = Build();
        clipboard.WriteSucceeds = false;

        paster.Write(Payloads(), thenPaste: false);
        scheduler.RunAll();

        Assert.Equal(ClipboardPaster.MaxWriteAttempts, clipboard.Writes.Count);
        Assert.Equal(0, sender.SendCount);

        // Nothing was pasted wrongly, so there is nothing the user needs to know about.
        Assert.Empty(messages);
    }

    [Fact]
    public void An_empty_payload_set_does_nothing_at_all()
    {
        var (paster, clipboard, sender, scheduler, _) = Build();

        paster.Write([], thenPaste: true);
        scheduler.RunAll();

        Assert.Empty(clipboard.Writes);
        Assert.Equal(0, sender.SendCount);
    }

    [Fact]
    public void The_written_content_is_registered_as_our_own_before_the_write_lands()
    {
        var clipboard = new FakeClipboardAccess();
        var guard = new SelfWriteGuard();
        var scheduler = new ManualScheduler();
        var paster = new ClipboardPaster(clipboard, new FakePasteSender(), guard, scheduler.Schedule);

        var payloads = Payloads("registered");
        paster.Write(payloads, thenPaste: true);
        scheduler.RunAll();

        // Ordering, not just presence: the notification our write provokes can arrive before
        // TryWrite has even returned, so the hash has to be registered first or the app captures its
        // own paste as a brand new clip.
        var hash = new ClipboardSnapshot(payloads, null, ClipKind.Other, null).ContentHash;
        Assert.True(guard.IsOwnWrite(hash));
    }

    [Fact]
    public void Pass_through_paste_sends_a_keystroke_without_touching_the_clipboard()
    {
        var (paster, clipboard, sender, _, _) = Build();

        // The empty-store path: the hook consumed the user's Ctrl+V to build the gesture, so it has
        // to be honoured, and the clipboard must be left exactly as it was.
        Assert.True(paster.SendPasteOnly());

        Assert.Equal(1, sender.SendCount);
        Assert.Empty(clipboard.Writes);
    }
}
