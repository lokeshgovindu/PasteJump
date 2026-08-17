using System.Text;
using PasteJump.Core;
using PasteJump.Core.Capture;
using PasteJump.Core.Model;
using PasteJump.Core.Settings;
using PasteJump.Core.Storage;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Suppression of repeat copies.
/// <para>
/// The store already collapses byte-identical repeats by content hash, and that was assumed to be
/// enough. It is not, for the reason the middle test here pins down: the hash spans every clipboard
/// format, and the rich formats travelling with text are not reproducible between two copies of the
/// same selection. Anything copied out of Word, Excel or a browser therefore hashed differently every
/// time, so both the clip stack and the history accumulated entries the user sees as identical.
/// </para>
/// </summary>
public sealed class ConsecutiveDuplicateTests : IDisposable
{
    private readonly string _root;
    private readonly ClipStore _store;
    private readonly FakeClipboardAccess _clipboard = new();
    private readonly FakeForegroundWindowInfo _foreground = new("winword.exe");
    private readonly ManualScheduler _scheduler = new();
    private readonly SelfWriteGuard _selfWrites = new();
    private PasteJumpSettings _settings = new();

    public ConsecutiveDuplicateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pastejump-dedup-tests", Guid.NewGuid().ToString("n"));
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
        schedule: _scheduler.Schedule);

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
    /// Text plus a rich format whose bytes differ per copy, exactly as Word and Excel produce. The
    /// <c>marker</c> stands in for the generator id / object descriptor / timestamp that varies.
    /// </summary>
    private static ClipboardSnapshot RichTextSnapshot(string text, string marker)
    {
        var unicode = new ClipPayload(13, null, Encoding.Unicode.GetBytes(text));
        var rtf = new ClipPayload(0xC004, "Rich Text Format", Encoding.ASCII.GetBytes($@"{{\rtf1 {marker} {text}}}"));

        return new ClipboardSnapshot([unicode, rtf], text, ClipKind.Text, "winword.exe");
    }

    [Fact]
    public void The_same_text_copied_twice_is_stored_once()
    {
        _clipboard
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("repeated"))
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("repeated"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(1, _store.Count);
        Assert.Single(_store.SearchHistory("repeated"));
    }

    [Fact]
    public void The_same_text_is_suppressed_even_when_its_rich_formats_differ()
    {
        // Two copies of one Word paragraph. Identical text, different RTF bytes, so two different
        // content hashes - which is precisely why hash dedup could never catch this.
        var first = RichTextSnapshot("Quarterly figures", "generator-1");
        var second = RichTextSnapshot("Quarterly figures", "generator-2");

        Assert.NotEqual(first.ContentHash, second.ContentHash);
        Assert.Equal(first.DedupKey, second.DedupKey);

        _clipboard.EnqueueRead(first).EnqueueRead(second);

        var capture = Build();
        capture.Prime();
        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(1, _store.Count);
        Assert.Single(_store.SearchHistory("Quarterly"));
        Assert.Equal(1, capture.ConsecutiveDuplicateSkipCount);
    }

    [Fact]
    public void Only_the_immediately_previous_capture_suppresses()
    {
        // A, B, A. The third copy is a genuine user action - they went back for it - so it is stored.
        _clipboard
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("alpha"))
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("beta"))
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("alpha"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);
        SignalChange(capture);
        SignalChange(capture);

        // Two rows: "alpha" was promoted back to the front by the store's hash match rather than
        // inserted a second time.
        Assert.Equal(2, _store.Count);
        Assert.Equal(0, capture.ConsecutiveDuplicateSkipCount);
    }

    [Fact]
    public void Trailing_whitespace_alone_does_not_make_it_a_different_clip()
    {
        // Selecting a line in one app yields a trailing newline and in another it does not.
        _clipboard
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("same line"))
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("same line\r\n"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(1, _store.Count);
    }

    [Fact]
    public void Deleting_the_clip_lets_the_same_text_be_captured_again()
    {
        _clipboard
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("recreate me"))
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("recreate me"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        var clip = _store.GetOrdered(1).Single();
        _store.Delete(clip.Id);
        Assert.Equal(0, _store.Count);

        SignalChange(capture);

        // Suppressing against a row the user has deleted would look like the app ignoring them.
        Assert.Equal(1, _store.Count);
        Assert.Equal(0, capture.ConsecutiveDuplicateSkipCount);
    }

    [Fact]
    public void Allowing_duplicates_turns_suppression_off()
    {
        _settings = new PasteJumpSettings { AllowDuplicateClips = true };

        _clipboard
            .EnqueueRead(RichTextSnapshot("twice over", "gen-1"))
            .EnqueueRead(RichTextSnapshot("twice over", "gen-2"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(2, _store.Count);
        Assert.Equal(0, capture.ConsecutiveDuplicateSkipCount);
    }

    [Fact]
    public void A_suppressed_duplicate_still_reports_that_a_copy_happened()
    {
        // Suppressing the clip must not also suppress the acknowledgement. With no feedback, a repeat
        // Ctrl+C is indistinguishable from PasteJump having missed the copy entirely - and the user's
        // reasonable conclusion is that the app is broken.
        _clipboard
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("acknowledge me"))
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("acknowledge me"));

        var capture = Build();
        capture.Prime();

        var observed = 0;
        capture.CaptureObserved += () => observed++;

        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(1, _store.Count);
        Assert.Equal(1, capture.ConsecutiveDuplicateSkipCount);

        // Once, for the suppressed second copy. The first raised ClipCaptured instead.
        Assert.Equal(1, observed);
    }

    [Fact]
    public void A_stored_capture_does_not_raise_the_observed_only_event()
    {
        _clipboard.EnqueueRead(FakeClipboardAccess.TextSnapshot("stored normally"));

        var capture = Build();
        capture.Prime();

        var observed = 0;
        var captured = 0;
        capture.CaptureObserved += () => observed++;
        capture.ClipCaptured += _ => captured++;

        SignalChange(capture);

        // Exactly one notification per copy - never both, or the toast would fire twice.
        Assert.Equal(1, captured);
        Assert.Equal(0, observed);
    }

    [Fact]
    public void Different_text_is_never_suppressed()
    {
        _clipboard
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("first"))
            .EnqueueRead(FakeClipboardAccess.TextSnapshot("second"));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(2, _store.Count);
        Assert.Equal(0, capture.ConsecutiveDuplicateSkipCount);
    }

    [Fact]
    public void Non_text_clips_still_key_on_their_full_content()
    {
        // No text to compare, so the dedup key falls back to the content hash. Two images with
        // different bytes must both be kept.
        var a = new ClipboardSnapshot([new ClipPayload(8, null, [1, 2, 3])], null, ClipKind.Image, "mspaint.exe");
        var b = new ClipboardSnapshot([new ClipPayload(8, null, [4, 5, 6])], null, ClipKind.Image, "mspaint.exe");

        Assert.NotEqual(a.DedupKey, b.DedupKey);

        _clipboard.EnqueueRead(a).EnqueueRead(b);

        var capture = Build();
        capture.Prime();
        SignalChange(capture);
        SignalChange(capture);

        Assert.Equal(2, _store.Count);
    }
}
