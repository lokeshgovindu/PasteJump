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

    /// <summary>Advances the sequence number, as a real clipboard change would.</summary>
    private void SignalChange(CaptureService capture)
    {
        _clipboard.SequenceNumber++;
        capture.OnClipboardChanged();
    }

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
        var long_ = new string('a', ClipStore.PreviewMaxChars + 500);
        _clipboard.EnqueueRead(FakeClipboardAccess.TextSnapshot(long_, "devenv.exe"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        var entry = Assert.Single(_store.SearchHistory(null));

        Assert.Equal(ClipStore.PreviewMaxChars, entry.Preview.Length);
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
        Assert.Equal(1, _scheduler.ScheduledCount);

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

        Assert.Equal(1, _scheduler.ScheduledCount);

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
}
