using PasteJump.Core;
using PasteJump.Core.Capture;
using PasteJump.Core.Model;
using PasteJump.Core.Settings;
using PasteJump.Core.Storage;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The capture path. These tests exist because a live smoke test caught this code silently
/// dropping a clipboard change and double-logging history - both invisible without them.
/// </summary>
public sealed class CaptureServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ClipStore _store;
    private readonly FakeClipboardAccess _clipboard = new();
    private readonly FakeForegroundWindowInfo _foreground = new("notepad.exe");
    private readonly ManualScheduler _scheduler = new();
    private readonly SelfWriteGuard _selfWrites = new();
    private PasteJumpSettings _settings = new();

    public CaptureServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pastejump-capture-tests", Guid.NewGuid().ToString("n"));
        _store = new ClipStore(AppPaths.At(_root));
    }

    public void Dispose()
    {
        _store.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private CaptureService Build() => new(
        _clipboard,
        _store,
        _selfWrites,
        _foreground,
        () => _settings,
        clock: null,
        schedule: _scheduler.Schedule,
        retryDelay: TimeSpan.FromMilliseconds(10));

    /// <summary>
    /// Advances the sequence number and lets the settle window elapse, as a real clipboard change does.
    /// </summary>
    /// <remarks>
    /// The read is scheduled rather than immediate since coalescing arrived - one copy raises more than one
    /// notification, so PasteJump waits for the clipboard to stop changing before reading it. Draining the
    /// scheduler here rather than setting <c>ClipboardSettleMs</c> to zero in these tests is deliberate: it keeps
    /// every test in this file exercising the path the application actually takes.
    /// </remarks>
    private void SignalChange(CaptureService capture)
    {
        _clipboard.SequenceNumber++;
        capture.OnClipboardChanged();

        // The scheduled read. Nothing else is queued at this point, so this cannot swallow a retry.
        _scheduler.RunPending();
    }

    /// <summary>
    /// A clipboard holding only OLE's <c>DataObject</c> marker is not a clip. Reported as
    /// <c>[binary]</c>, 8 bytes, from the Snipping Tool and every other OLE source, because
    /// <c>OleSetClipboard</c> announces the data object before <c>OleFlushClipboard</c> renders anything.
    /// </summary>
    [Fact]
    public void DoesNotStoreAClipboardHoldingOnlyOleBookkeeping()
    {
        _clipboard.EnqueueRead(BookkeepingSnapshot());

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        Assert.Equal(0, _store.Count);
        Assert.Equal(0, _store.HistoryCount);
        Assert.Equal(1, capture.BookkeepingOnlySkipCount);
    }

    /// <summary>
    /// And the copy is not lost: the read is retried, which is what picks up the real formats once the
    /// source has flushed them.
    /// </summary>
    [Fact]
    public void RetriesAfterABookkeepingOnlyRead()
    {
        _clipboard.EnqueueRead(BookkeepingSnapshot());
        _clipboard.EnqueueRead(FakeClipboardAccess.TextSnapshot("the real payload", "devenv.exe"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        // The scheduler is manual, so the deferred re-read only happens when the test lets it.
        _scheduler.RunPending();

        Assert.Equal(1, _store.Count);
        Assert.Equal("the real payload", _store.GetOrdered()[0].Preview);
    }

    /// <summary>
    /// A descriptor alongside real content must not suppress the capture - only a clipboard that is
    /// <em>nothing but</em> bookkeeping is skipped.
    /// </summary>
    [Fact]
    public void StoresAClipThatCarriesBookkeepingAlongsideContent()
    {
        var payloads = new[]
        {
            new ClipPayload(49161, "DataObject", new byte[8]),
            new ClipPayload(13, null, System.Text.Encoding.Unicode.GetBytes("real text")),
        };

        _clipboard.EnqueueRead(new ClipboardSnapshot(payloads, "real text", ClipKind.Text, "devenv.exe"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        Assert.Equal(1, _store.Count);
        Assert.Equal(0, capture.BookkeepingOnlySkipCount);
    }

    /// <summary>
    /// The clip from the report: a ShareX screenshot stored as <c>Other</c>, 708 bytes, while the same
    /// screenshot saved to disk perfectly and the next one captured as a 7.2 MB image.
    /// </summary>
    /// <remarks>
    /// The payload set is that clip's, byte for byte. It slipped past the bookkeeping rule because
    /// <c>System.Drawing.Bitmap</c> was not on the list, so "all bookkeeping" was false and a half-written
    /// clipboard was stored as though the copy were finished. Both screenshots were 1912x987, whose pixels are
    /// 7,548,576 bytes - 484 cannot be any part of one.
    /// </remarks>
    [Fact]
    public void DoesNotStoreAnOleWriteCaughtBeforeItsPixelsWereRendered()
    {
        _clipboard.EnqueueRead(HalfWrittenScreenshotSnapshot());

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        Assert.Equal(0, _store.Count);
        Assert.Equal(1, capture.BookkeepingOnlySkipCount);
    }

    /// <summary>
    /// And the screenshot is not lost: the retry is what stores it, once the writer has flushed its formats.
    /// Measured at 51 ms behind the notification in a probe of a WinForms <c>SetDataObject(..., copy: true)</c>.
    /// </summary>
    [Fact]
    public void TheRetryStoresTheScreenshotOnceItsPixelsArrive()
    {
        _clipboard.EnqueueRead(HalfWrittenScreenshotSnapshot());
        _clipboard.EnqueueRead(ScreenshotSnapshot());

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        _scheduler.RunPending();

        Assert.Equal(1, _store.Count);
        Assert.Equal(ClipKind.Image, _store.GetOrdered()[0].Kind);
    }

    /// <summary>
    /// The other side of the same rule: a rendered image is stored even though the same OLE entries are
    /// beside it. Without this the fix above would suppress every OLE image copy - which is most of them.
    /// </summary>
    [Fact]
    public void StoresARenderedImageThatCarriesTheSameOleEntries()
    {
        _clipboard.EnqueueRead(ScreenshotSnapshot());

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        Assert.Equal(1, _store.Count);
        Assert.Equal(0, capture.BookkeepingOnlySkipCount);
    }

    /// <summary>The reported clip's payload set, exactly as the store held it.</summary>
    private static ClipboardSnapshot HalfWrittenScreenshotSnapshot() => new(
        [
            new ClipPayload(50198, "System.Drawing.Bitmap", new byte[484]),
            new ClipPayload(49171, "Ole Private Data", new byte[216]),
            new ClipPayload(49161, "DataObject", new byte[8]),
        ],
        null,
        ClipKind.Other,
        "msedge.exe");

    /// <summary>The same copy after the writer flushed it: CF_DIB present, OLE entries still there.</summary>
    private static ClipboardSnapshot ScreenshotSnapshot() => new(
        [
            new ClipPayload(8, null, new byte[7_548_628]),
            new ClipPayload(49171, "Ole Private Data", new byte[216]),
            new ClipPayload(49161, "DataObject", new byte[8]),
        ],
        null,
        ClipKind.Image,
        "msedge.exe");

    /// <summary>The eight-byte marker set observed in a real store, for the tests above.</summary>
    private static ClipboardSnapshot BookkeepingSnapshot() => new(
        [
            new ClipPayload(49161, "DataObject", new byte[8]),
            new ClipPayload(49171, "Ole Private Data", new byte[216]),
        ],
        null,
        ClipKind.Other,
        "devenv.exe");

    /// <summary>
    /// Text longer than the preview column can hold is archived in full as a blob.
    /// <para>
    /// Without this the history archive kept only the first <see cref="ClipStore.PreviewMaxChars"/> characters,
    /// and the History window's Copy handed that back as though it were the whole clip - silently, and for an
    /// entry no longer in the stack that was the only copy left.
    /// </para>
    /// </summary>
    [Fact]
    public void RecordsFullTextForEntriesTooLongForThePreview()
    {
        var long_ = new string('a', _store.PreviewMaxChars + 500);
        _clipboard.EnqueueRead(FakeClipboardAccess.TextSnapshot(long_, "devenv.exe"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        var entry = Assert.Single(_store.SearchHistory(null));

        Assert.Equal(_store.PreviewMaxChars, entry.Preview.Length);
        Assert.NotNull(entry.BlobHash);

        var archived = _store.Blobs.TryRead(entry.BlobHash!);

        Assert.NotNull(archived);
        Assert.Equal(long_, System.Text.Encoding.UTF8.GetString(archived!));
    }

    /// <summary>
    /// And nothing is archived when the preview already holds the whole thing - the preview is the payload for
    /// short text, so a blob would be a duplicate copy of it in every history row.
    /// </summary>
    [Fact]
    public void DoesNotArchiveTextThatFitsInThePreview()
    {
        _clipboard.EnqueueRead(FakeClipboardAccess.TextSnapshot("short enough", "devenv.exe"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        Assert.Null(Assert.Single(_store.SearchHistory(null)).BlobHash);
    }

    [Fact]
    public void CapturesAClipAndRecordsHistory()
    {
        _clipboard.EnqueueRead(FakeClipboardAccess.TextSnapshot("hello world", "devenv.exe"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        Assert.Equal(1, _store.Count);
        Assert.Equal(1, _store.HistoryCount);
        Assert.Equal("devenv.exe", _store.GetOrdered()[0].SourceExecutable);
    }

    [Fact]
    public void IgnoresANotificationWithNoSequenceChange()
    {
        _clipboard.Standing = FakeClipboardAccess.TextSnapshot("hello");

        var capture = Build();
        capture.Prime();

        // Same sequence number as priming: Windows can raise a redundant notification.
        capture.OnClipboardChanged();

        Assert.Equal(0, _clipboard.ReadCallCount);
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void RepeatNotificationForSameContent_PromotesButDoesNotDoubleLogHistory()
    {
        var snapshot = FakeClipboardAccess.TextSnapshot("ole copy");
        _clipboard.EnqueueRead(snapshot).EnqueueRead(snapshot);

        var capture = Build();
        capture.Prime();

        // An OLE copy performs OleSetClipboard then OleFlushClipboard: two real clipboard changes
        // with DIFFERENT sequence numbers carrying identical content, so the sequence filter
        // cannot collapse them.
        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(1, _store.Count);
        Assert.Equal(1, _store.HistoryCount);

        // Caught by consecutive-duplicate suppression, which runs before the store's hash match and
        // subsumes it: the text is identical, so the capture is dropped without an insert to promote.
        // DuplicateNotificationCount now only counts byte-identical repeats that got past that check
        // - a non-consecutive re-copy of something still in the stack.
        Assert.Equal(1, capture.ConsecutiveDuplicateSkipCount);
        Assert.Equal(0, capture.DuplicateNotificationCount);
    }

    [Fact]
    public void FailedRead_IsRetriedOnADelayAndSucceeds()
    {
        _clipboard
            .EnqueueRead(null)
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("recovered after retry"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        // First read lost the race against whoever held the clipboard.
        Assert.Equal(0, _store.Count);
        Assert.Equal(1, capture.ReadFailureCount);

        // Two schedules, not one: the settle window SignalChange has already drained, and the retry that failed
        // read then queued. Counting them separately is what would have hidden the retry going missing.
        Assert.Equal(2, _scheduler.ScheduledCount);

        _scheduler.RunAll();

        Assert.Equal(1, _store.Count);
        Assert.Equal(0, capture.DroppedCaptureCount);
        Assert.Equal("recovered after retry", _store.GetOrdered()[0].Preview);
    }

    [Fact]
    public void RetriesAreBoundedAndTheDropIsCounted()
    {
        _clipboard.Standing = null;

        var capture = Build();
        capture.Prime();
        SignalChange(capture);
        _scheduler.RunAll();

        // Bounded on purpose: retrying for ever would be the original's unbounded spin all over again.
        Assert.Equal(CaptureService.MaxDeferredRetries + 1, capture.ReadFailureCount);
        Assert.Equal(1, capture.DroppedCaptureCount);
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void RetryIsAbandonedIfTheClipboardMovedOnMeanwhile()
    {
        _clipboard.EnqueueRead(null);

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        // The settle read, plus the retry it queued after failing.
        Assert.Equal(2, _scheduler.ScheduledCount);

        // A newer change arrives before the retry fires. That change has its own notification, so
        // this retry must not store what is now stale content.
        _clipboard.SequenceNumber++;
        _clipboard.Standing = FakeClipboardAccess.TextSnapshot("newer content");

        _scheduler.RunPending();

        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void OurOwnWriteIsNotCaptured()
    {
        var snapshot = FakeClipboardAccess.TextSnapshot("pasted by us");
        _clipboard.EnqueueRead(snapshot);
        _selfWrites.NoteWrite(snapshot.ContentHash);

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        Assert.Equal(0, _store.Count);
        Assert.Equal(1, capture.SelfWriteSkipCount);
    }

    /// <summary>
    /// Reported as "I copied in Notepad, closed it, and saw the copy notification again". An application
    /// rendering its clipboard formats as it closes republishes byte-identical content, so nothing but the
    /// ownership tells that apart from a copy made again: a live process reports an owning window, a flush
    /// reports none. Measured 2026-08-20, both cases.
    /// </summary>
    [Fact]
    public void AFlushFromAClosingApplicationIsNotAnnouncedAsARepeat()
    {
        var copied = FakeClipboardAccess.TextSnapshot("copied in notepad");
        var flushed = FakeClipboardAccess.TextSnapshot("copied in notepad", hasOwner: false);

        _clipboard.EnqueueRead(copied);
        _clipboard.EnqueueRead(flushed);

        var notices = 0;
        var capture = Build();
        capture.CaptureObserved += () => notices++;
        capture.Prime();

        SignalChange(capture);   // the copy
        SignalChange(capture);   // the application closing and flushing what it had copied

        Assert.Equal(1, _store.Count);
        Assert.Equal(1, capture.OwnerlessRepublishSkipCount);
        Assert.Equal(0, notices);
    }

    /// <summary>
    /// The counterpart, and the reason ownership is the test rather than the content: a repeat the user really
    /// made comes from a live application, which owns the clipboard. That still earns its acknowledgement -
    /// staying silent would make a repeat copy indistinguishable from PasteJump having missed it.
    /// </summary>
    [Fact]
    public void ARepeatFromALiveApplicationIsStillAnnounced()
    {
        var copied = FakeClipboardAccess.TextSnapshot("copied twice by hand");

        _clipboard.EnqueueRead(copied);
        _clipboard.EnqueueRead(copied);

        var notices = 0;
        var capture = Build();
        capture.CaptureObserved += () => notices++;
        capture.Prime();

        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(1, _store.Count);
        Assert.Equal(0, capture.OwnerlessRepublishSkipCount);
        Assert.Equal(1, notices);
    }

    /// <summary>
    /// The bug reported as "after paste, I am getting copied overlay also". One paste is not one notification:
    /// an application that republishes the clipboard after the settle window closes produces a second read of
    /// the same bytes. <c>IsOwnWrite</c> consumes its entry, so that second read used to fall through to the
    /// consecutive-duplicate branch - which deliberately announces itself - and every paste into such an
    /// application ended with a "Same as the last copy" toast.
    /// </summary>
    [Fact]
    public void ASecondNotificationForOnePasteIsSilent()
    {
        var snapshot = FakeClipboardAccess.TextSnapshot("pasted by us");
        _clipboard.EnqueueRead(snapshot);
        _clipboard.EnqueueRead(snapshot);
        _selfWrites.NoteWrite(snapshot.ContentHash);

        var notices = 0;
        var capture = Build();
        capture.CaptureObserved += () => notices++;
        capture.Prime();

        SignalChange(capture);   // the paste's own write
        SignalChange(capture);   // the application publishing it again, after the settle window

        Assert.Equal(0, _store.Count);
        Assert.Equal(1, capture.SelfWriteSkipCount);
        Assert.Equal(1, capture.SelfWriteEchoSkipCount);
        Assert.Equal(0, notices);
    }


    [Fact]
    public void MonitoringDisabled_ReadsNothingAtAll()
    {
        _settings = new PasteJumpSettings { MonitorClipboard = false };
        _clipboard.Standing = FakeClipboardAccess.TextSnapshot("should not be read");

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        Assert.Equal(0, _clipboard.ReadCallCount);
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void IgnoredProcess_IsSkippedWithoutEvenReadingTheClipboard()
    {
        _settings = new PasteJumpSettings { IgnoredProcesses = ["keepass.exe"] };
        _foreground.ProcessName = "KeePass.exe";
        _clipboard.Standing = FakeClipboardAccess.TextSnapshot("a password");

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        // Not reading is the point: a password must not enter this process's memory before being
        // discarded. Matching is case-insensitive.
        Assert.Equal(0, _clipboard.ReadCallCount);
        Assert.Equal(1, capture.IgnoredProcessSkipCount);
        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void ImageIsSkippedWhenImageStorageIsOff()
    {
        _settings = new PasteJumpSettings { StoreImages = false };

        _clipboard.EnqueueRead(new ClipboardSnapshot(
            [new ClipPayload(8, null, [1, 2, 3, 4])], null, ClipKind.Image, null));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void EvictionKeepsTheStackWithinTheConfiguredCeiling()
    {
        _settings = new PasteJumpSettings { MaxClips = 3 };

        var capture = Build();
        capture.Prime();

        for (var i = 1; i <= 8; i++)
        {
            _clipboard.EnqueueRead(FakeClipboardAccess.TextSnapshot($"clip {i}"));
            SignalChange(capture);
        }

        Assert.Equal(3, _store.Count);

        // History is the archive and is not subject to the stack ceiling.
        Assert.Equal(8, _store.HistoryCount);
    }

    [Fact]
    public void CapturedEventFiresForEachNewClip()
    {
        var captured = new List<Clip>();

        var capture = Build();
        capture.ClipCaptured += captured.Add;
        capture.Prime();

        _clipboard.EnqueueRead(FakeClipboardAccess.TextSnapshot("one"));
        SignalChange(capture);
        _clipboard.EnqueueRead(FakeClipboardAccess.TextSnapshot("two"));
        SignalChange(capture);

        Assert.Equal(2, captured.Count);
    }

    [Fact]
    public void WithoutAScheduler_AFailedReadIsDroppedRatherThanHanging()
    {
        _clipboard.Standing = null;

        var capture = new CaptureService(
            _clipboard, _store, _selfWrites, _foreground, () => _settings);

        capture.Prime();
        _clipboard.SequenceNumber++;
        capture.OnClipboardChanged();

        Assert.Equal(1, capture.DroppedCaptureCount);
    }
    /// <summary>
    /// One copy, two notifications, one clip. This is the reported bug, and it is the whole reason notifications
    /// are coalesced.
    /// </summary>
    /// <remarks>
    /// An OLE writer publishes in two steps - the data object is announced, then its formats are rendered - and
    /// each raises <c>WM_CLIPBOARDUPDATE</c> with its own sequence number. PasteJump read on both and stored two
    /// clips per screenshot. Worse, the two reads did not always return identical bytes, so the duplicate check
    /// could not collapse them: a real store held 665,745 bytes twice, with different hashes, one second apart,
    /// from a single capture. That is what "I take one screenshot and see the same thing twice" was.
    ///
    /// Note the two snapshots here differ deliberately, exactly as the two reads did. Making them identical would
    /// let the duplicate check pass this test even with no coalescing at all.
    /// </remarks>
    [Fact]
    public void One_copy_publishing_in_two_steps_is_stored_once()
    {
        // The clipboard as it ENDS UP, not a queue of reads: the point is that only one read happens, and that it
        // happens after the writer has finished. A queue would have modelled the old behaviour instead.
        _clipboard.Standing = FakeClipboardAccess.TextSnapshot("the finished clipboard");

        var capture = Build();
        capture.Prime();

        // Both notifications arrive before the settle window elapses, which is what ~45ms apart means.
        _clipboard.SequenceNumber++;
        capture.OnClipboardChanged();
        _clipboard.SequenceNumber++;
        capture.OnClipboardChanged();

        _scheduler.RunAll();

        Assert.Equal(1, _store.Count);
        Assert.Equal("the finished clipboard", _store.GetOrdered()[0].Preview);
        Assert.Equal(2, capture.NotificationCount);
        Assert.Equal(1, capture.CoalescedNotificationCount);

        // The load-bearing assertion. One clip alone would pass without any coalescing at all, since two identical
        // reads collapse in the duplicate check anyway - what proves the fix is that the clipboard was read ONCE.
        Assert.Equal(1, _clipboard.ReadCallCount);
    }

    /// <summary>
    /// The other half: two genuinely separate copies are still two clips. Without this, coalescing could "fix" the
    /// duplicate by swallowing real copies, which is the worse failure of the two.
    /// </summary>
    [Fact]
    public void Two_separate_copies_are_still_two_clips()
    {
        _clipboard
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("first copy"))
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("second copy"));

        var capture = Build();
        capture.Prime();

        // SignalChange drains the settle window each time, so these are two separate bursts.
        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(2, _store.Count);
        Assert.Equal(0, capture.CoalescedNotificationCount);
    }

    /// <summary>
    /// Zero restores the old behaviour, for anyone who would rather have the duplicates than the delay - and it is
    /// also how a reader can tell this setting does what it says.
    /// </summary>
    [Fact]
    public void A_settle_of_zero_reads_on_every_notification()
    {
        _settings.ClipboardSettleMs = 0;

        _clipboard
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("step one"))
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("step two"));

        var capture = Build();
        capture.Prime();

        _clipboard.SequenceNumber++;
        capture.OnClipboardChanged();
        _clipboard.SequenceNumber++;
        capture.OnClipboardChanged();

        // Both read inline, so both stored - the behaviour the report was about.
        Assert.Equal(2, _store.Count);
        Assert.Equal(0, capture.CoalescedNotificationCount);
    }

    /// <summary>
    /// A second publishing step that lands just AFTER the window still yields one clip, because the window
    /// restarts on every notification.
    /// </summary>
    /// <remarks>
    /// This is the case the first version of the fix missed. With a fixed window measured from the first
    /// notification, a step arriving late began a fresh read, that read saw what the first had already stored, and
    /// the user got "Same as the last copy" on a copy that was nothing of the sort - reported as "I double clicked
    /// one word, first time it is copied, and immediately that warning came".
    /// </remarks>
    [Fact]
    public void A_late_second_step_still_produces_one_clip_and_no_duplicate_notice()
    {
        _clipboard.Standing = FakeClipboardAccess.TextSnapshot("the selected word");

        var capture = Build();
        capture.Prime();

        var duplicateNotices = 0;
        capture.CaptureObserved += () => duplicateNotices++;

        // Step one.
        _clipboard.SequenceNumber++;
        capture.OnClipboardChanged();

        // Step two, arriving while the scheduled read is still pending, so it extends the window.
        _clipboard.SequenceNumber++;
        capture.OnClipboardChanged();

        // The timer fires, sees the extension, and re-queues rather than reading.
        _scheduler.RunPending();
        Assert.Equal(1, capture.SettleExtensionCount);
        Assert.Equal(0, _clipboard.ReadCallCount);

        // The re-queued read is the only one that runs.
        _scheduler.RunAll();

        Assert.Equal(1, _store.Count);
        Assert.Equal(1, _clipboard.ReadCallCount);

        // And the point of the whole exercise: no "Same as the last copy" for a single copy.
        Assert.Equal(0, duplicateNotices);
    }

    /// <summary>
    /// The bound: an application rewriting the clipboard in a loop must not defer the read for ever. After
    /// <see cref="CaptureService.MaxSettleExtensions"/> restarts the read happens regardless.
    /// </summary>
    [Fact]
    public void The_settle_window_cannot_be_extended_indefinitely()
    {
        _clipboard.Standing = FakeClipboardAccess.TextSnapshot("something that keeps changing");

        var capture = Build();
        capture.Prime();

        _clipboard.SequenceNumber++;
        capture.OnClipboardChanged();

        // One more notification per round, for more rounds than the ceiling allows.
        for (var round = 0; round < CaptureService.MaxSettleExtensions + 3; round++)
        {
            _clipboard.SequenceNumber++;
            capture.OnClipboardChanged();
            _scheduler.RunPending();
        }

        Assert.Equal(CaptureService.MaxSettleExtensions, capture.SettleExtensionCount);
        Assert.True(_clipboard.ReadCallCount >= 1, "the read must happen even while notifications keep arriving");
    }

    /// <summary>
    /// The same copy published twice - plain text first, the formatted versions a fraction of a second later - is
    /// one clip, is not announced as a repeat, and keeps the RICHER payloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The numbers are from a real capture log, where Windows Terminal running a busy console application published
    /// every selection twice: <c>4 formats, 40 bytes</c> and then, 190 ms later, the same text as
    /// <c>6 formats, 216 bytes</c> with HTML and RTF added. The second read was suppressed as a repeat, which put
    /// "Same as the last copy, so not added again" on screen for a copy made exactly once - reported as "I double
    /// clicked one word, first time it is copied, and immediately that warning came".
    /// </para>
    /// <para>
    /// The formatting mattered too: keeping the 40-byte version and discarding the 216-byte one loses the rich text
    /// for good, so a paste into Word arrives plain from a copy that carried RTF.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_same_copy_published_again_with_more_formats_enriches_the_clip_silently()
    {
        const string word = "selection";

        _clipboard
            .EnqueueRead(PublishedSnapshot(word, rich: false))
            .EnqueueRead(PublishedSnapshot(word, rich: true));

        var capture = Build();
        capture.Prime();

        var duplicateNotices = 0;
        capture.CaptureObserved += () => duplicateNotices++;

        // Two separate bursts: the second publish is far outside any settle window, which is exactly why the
        // window alone could not fix this.
        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(1, _store.Count);
        Assert.Equal(0, duplicateNotices);
        Assert.Equal(1, capture.RepublishCount);
        Assert.Equal(1, capture.EnrichedCount);
        Assert.Equal(0, capture.ConsecutiveDuplicateSkipCount);

        // The richer set is what survived - all of it, not just a larger byte count.
        var clip = _store.GetOrdered()[0];
        var formats = _store.GetPayloads(clip.Id);

        Assert.Equal(3, formats.Count);
        Assert.Contains(formats, f => f.FormatName == "HTML Format");
        Assert.Contains(formats, f => f.FormatName == "Rich Text Format");
    }

    /// <summary>
    /// And the enrichment is not a second history entry: it was not a second copy.
    /// </summary>
    [Fact]
    public void An_enriched_republish_does_not_log_history_twice()
    {
        _clipboard
            .EnqueueRead(PublishedSnapshot("once", rich: false))
            .EnqueueRead(PublishedSnapshot("once", rich: true));

        var capture = Build();
        capture.Prime();

        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(1, _store.HistoryCount);
    }

    /// <summary>
    /// The limit of the rule: a republish carrying nothing extra is still reported as a repeat, because that is
    /// what a user pressing Ctrl+C twice looks like and the acknowledgement is wanted there.
    /// </summary>
    [Fact]
    public void A_repeat_carrying_nothing_extra_is_still_announced()
    {
        _clipboard
            .EnqueueRead(PublishedSnapshot("same", rich: false))
            .EnqueueRead(PublishedSnapshot("same", rich: false));

        var capture = Build();
        capture.Prime();

        var duplicateNotices = 0;
        capture.CaptureObserved += () => duplicateNotices++;

        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(1, _store.Count);
        Assert.Equal(1, duplicateNotices);
        Assert.Equal(1, capture.ConsecutiveDuplicateSkipCount);
        Assert.Equal(0, capture.EnrichedCount);
    }

    /// <summary>
    /// Zero switches the behaviour off, which is also how a reader can see what the setting does.
    /// </summary>
    [Fact]
    public void A_republish_window_of_zero_restores_the_repeat_notice()
    {
        _settings.ClipboardRepublishMs = 0;

        _clipboard
            .EnqueueRead(PublishedSnapshot("word", rich: false))
            .EnqueueRead(PublishedSnapshot("word", rich: true));

        var capture = Build();
        capture.Prime();

        var duplicateNotices = 0;
        capture.CaptureObserved += () => duplicateNotices++;

        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(1, duplicateNotices);
        Assert.Equal(0, capture.EnrichedCount);
    }

    /// <summary>
    /// One publish of a text selection: the plain formats alone, or those plus the formatted versions an
    /// application adds on its second pass. Modelled on a real log - see the enrichment test above.
    /// </summary>
    private static ClipboardSnapshot PublishedSnapshot(string text, bool rich)
    {
        var payloads = new List<ClipPayload>
        {
            new(13, null, System.Text.Encoding.Unicode.GetBytes(text)),
        };

        if (rich)
        {
            payloads.Add(new ClipPayload(49_381, "HTML Format", System.Text.Encoding.UTF8.GetBytes($"<span>{text}</span>")));
            payloads.Add(new ClipPayload(49_382, "Rich Text Format", System.Text.Encoding.UTF8.GetBytes($"{{\rtf1 {text}}}")));
        }

        return new ClipboardSnapshot(payloads, text, ClipKind.Text, "WindowsTerminal.exe");
    }

}
