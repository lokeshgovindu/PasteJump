using System.Text;
using PasteJump.Core.Abstractions;
using PasteJump.Core.Imaging;
using PasteJump.Core.Model;
using PasteJump.Core.Settings;
using PasteJump.Core.Storage;

namespace PasteJump.Core.Capture;

/// <summary>
/// Turns clipboard-change notifications into stored clips.
/// <para>
/// Lives in Core, with no dependency on the clipboard listener or on WPF, so the capture path -
/// the most important behaviour in the app - is unit-testable. The caller is responsible only for
/// invoking <see cref="OnClipboardChanged"/> when Windows says something changed.
/// </para>
/// <para>
/// The whole path is: notification, one read, one insert. No polling, no flag protocol, no second
/// read - all of which the original needed because it used the live clipboard as its working buffer.
/// </para>
/// </summary>
public sealed class CaptureService
{
    /// <summary>How many deferred re-reads to attempt after a failed read.</summary>
    public const int MaxDeferredRetries = 2;

    /// <summary>
    /// How many times the settle window may restart before the read happens anyway. Five windows at the default
    /// 120 ms is 600 ms, which is far longer than any publish measured and still short enough that an application
    /// rewriting the clipboard in a loop cannot silence capture altogether.
    /// </summary>
    public const int MaxSettleExtensions = 4;

    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(350);

    private readonly IClipboardAccess _clipboard;
    private readonly ClipStore _store;
    private readonly SelfWriteGuard _selfWrites;
    private readonly IForegroundWindowInfo _foreground;
    private readonly Func<PasteJumpSettings> _settings;
    private readonly IClock _clock;
    private readonly Action<TimeSpan, Action>? _schedule;
    private readonly TimeSpan _retryDelay;

    /// <summary>
    /// Where a one-line account of each decision goes, or null for none.
    /// </summary>
    /// <remarks>
    /// Added because two attempts at the "one copy, two clips" report were fixed blind: the timing between an
    /// application's publishing steps is what decides the behaviour, it differs per application - 45 ms for one
    /// writer, 345 ms for another, sometimes under the same sequence number - and none of it was visible after the
    /// fact. Metadata only: kinds, byte counts and hash prefixes, never a clip's content.
    /// </remarks>
    private readonly Action<string>? _trace;

    private uint _lastSequenceNumber;
    private bool _settleScheduled;
    private DateTimeOffset? _lastCapturedUtc;
    private long _lastTotalBytes;
    private bool _settleExtended;
    private string? _lastDedupKey;
    private long? _lastClipId;

    public CaptureService(
        IClipboardAccess clipboard,
        ClipStore store,
        SelfWriteGuard selfWrites,
        IForegroundWindowInfo foreground,
        Func<PasteJumpSettings> settings,
        IClock? clock = null,
        Action<TimeSpan, Action>? schedule = null,
        TimeSpan? retryDelay = null,
        Action<string>? trace = null)
    {
        _clipboard = clipboard;
        _store = store;
        _selfWrites = selfWrites;
        _foreground = foreground;
        _settings = settings;
        _clock = clock ?? SystemClock.Instance;
        _schedule = schedule;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _trace = trace;
    }

    /// <summary>Raised after a clip is captured, so open windows can refresh.</summary>
    public event Action<Clip>? ClipCaptured;

    /// <summary>
    /// Raised for a copy that was recognised but deliberately not stored, because it repeated the
    /// previous one. Exists so the UI can still confirm the copy happened: the absence of any
    /// acknowledgement reads as a missed capture, which is worse than a redundant one.
    /// </summary>
    public event Action? CaptureObserved;

    /// <summary>Notifications recognised as our own writes and skipped.</summary>
    public int SelfWriteSkipCount { get; private set; }

    /// <summary>
    /// Second and later notifications for one of our own writes, skipped without announcing anything. Worth
    /// counting separately: a non-zero value here is the signature of an application that republishes the
    /// clipboard after the settle window, which is exactly the case that used to toast after every paste.
    /// </summary>
    public int SelfWriteEchoSkipCount { get; private set; }

    /// <summary>Read attempts that could not open the clipboard, including retries.</summary>
    public int ReadFailureCount { get; private set; }

    /// <summary>
    /// Reads that found only OLE bookkeeping and were retried instead of stored. Routinely non-zero on a
    /// machine that copies from OLE applications; it is the normal cost of catching a copy mid-publish, not
    /// a fault. See <see cref="BookkeepingFormats"/>.
    /// </summary>
    public int BookkeepingOnlySkipCount { get; private set; }

    /// <summary>Captures abandoned after every retry was exhausted. Should stay at zero.</summary>
    public int DroppedCaptureCount { get; private set; }

    /// <summary>
    /// Notifications whose content was already in the stack, so promoted rather than inserted.
    /// Non-zero is normal - an OLE copy raises two changes for one logical action.
    /// </summary>
    public int DuplicateNotificationCount { get; private set; }

    /// <summary>Notifications skipped because the foreground process is on the ignore list.</summary>
    public int IgnoredProcessSkipCount { get; private set; }

    /// <summary>
    /// Captures dropped because they repeated the immediately-previous one. Distinct from
    /// <see cref="DuplicateNotificationCount"/>, which counts byte-identical repeats the store
    /// itself collapsed; this counts ones only recognisable at the text level.
    /// </summary>
    public int ConsecutiveDuplicateSkipCount { get; private set; }

    /// <summary>Clipboard notifications seen, before coalescing. Diagnostics only.</summary>
    public int NotificationCount { get; private set; }

    /// <summary>
    /// Notifications absorbed into a read that was already scheduled - so, second and later steps of a
    /// multi-step publish. This is the number that says the coalescing is doing anything.
    /// </summary>
    public int CoalescedNotificationCount { get; private set; }

    /// <summary>Reads recognised as the same copy published a second time. Diagnostics only.</summary>
    public int RepublishCount { get; private set; }

    /// <summary>Republishes that carried more than what was stored, so the clip was replaced. Diagnostics only.</summary>
    public int EnrichedCount { get; private set; }

    /// <summary>Times the settle window restarted because another notification arrived. Diagnostics only.</summary>
    public int SettleExtensionCount { get; private set; }

    /// <summary>Call once at start-up so the first real change is not mistaken for a new one.</summary>
    public void Prime() => _lastSequenceNumber = _clipboard.SequenceNumber;

    /// <summary>Handles a clipboard-change notification.</summary>
    /// <remarks>
    /// <para>
    /// Coalescing, not reading. One copy raises more than one notification: an OLE writer announces its data
    /// object and then renders it, each step with its own sequence number, so reading on both stored two clips for
    /// one screenshot - and since the two reads do not always return identical bytes, the duplicate check could not
    /// collapse them. Reading during the first step is also how a clipboard with no pixels in it yet got stored.
    /// </para>
    /// <para>
    /// So the first notification schedules a read <see cref="PasteJumpSettings.ClipboardSettleMs"/> ahead and the
    /// ones that arrive while it is pending are absorbed - the read that eventually runs sees the finished
    /// clipboard, whatever number of steps the writer took to publish it. The sequence-number check inside
    /// <see cref="Capture"/> then skips the absorbed notifications for free, since by then the number it recorded is
    /// the final one.
    /// </para>
    /// <para>
    /// Falls back to reading inline when there is no scheduler (which is how the tests that predate this drive it)
    /// or when the setting is zero.
    /// </para>
    /// </remarks>
    public void OnClipboardChanged()
    {
        var settleMs = _settings().ClipboardSettleMs;

        if (settleMs <= 0 || _schedule is null)
        {
            Capture(attempt: 0);
            return;
        }

        NotificationCount++;

        // Absorbed - and the window starts again from here. A fixed window measured from the FIRST notification
        // was the first version of this, and it only helps when both steps of a publish land inside it: the second
        // step arriving just after it would begin a fresh read, be recognised as a repeat of what the first read
        // stored, and produce the "Same as the last copy" toast on a copy that was nothing of the sort. Restarting
        // means the read happens once the clipboard has actually stopped changing, whatever the gap.
        if (_settleScheduled)
        {
            CoalescedNotificationCount++;
            _settleExtended = true;
            _trace?.Invoke($"notify seq={_clipboard.SequenceNumber} coalesced, window restarted");
            return;
        }

        _trace?.Invoke($"notify seq={_clipboard.SequenceNumber} read scheduled in {settleMs}ms");
        ScheduleSettledRead(settleMs, MaxSettleExtensions);
    }

    /// <summary>
    /// Queues the one read, re-queueing it while notifications keep arriving.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. An application that rewrites the clipboard on a timer would otherwise push the read
    /// back for ever and capture nothing at all - so after <see cref="MaxSettleExtensions"/> extensions the read
    /// happens regardless, and the worst case becomes the old behaviour rather than silence.
    /// </remarks>
    private void ScheduleSettledRead(int settleMs, int extensionsLeft)
    {
        _settleScheduled = true;
        _settleExtended = false;

        _schedule!(TimeSpan.FromMilliseconds(settleMs), () =>
        {
            if (_settleExtended && extensionsLeft > 0)
            {
                SettleExtensionCount++;
                ScheduleSettledRead(settleMs, extensionsLeft - 1);
                return;
            }

            _settleScheduled = false;
            Capture(attempt: 0);
        });
    }

    private void Capture(int attempt)
    {
        var settings = _settings();

        if (!settings.MonitorClipboard)
        {
            return;
        }

        var sequence = _clipboard.SequenceNumber;

        if (attempt == 0)
        {
            // Cheap duplicate-notification filter: Windows can raise WM_CLIPBOARDUPDATE more than
            // once for a single logical change, and the sequence number settles it without paying
            // to open the clipboard.
            if (sequence == _lastSequenceNumber)
            {
                _trace?.Invoke($"read skipped: sequence {sequence} unchanged since the last capture");
                return;
            }

            _lastSequenceNumber = sequence;
        }
        else if (sequence != _lastSequenceNumber)
        {
            _trace?.Invoke($"retry {attempt} abandoned: sequence moved {_lastSequenceNumber} -> {sequence}");
            // The clipboard moved on while we were waiting to retry. That newer change has its own
            // notification, so retrying this one would store stale content.
            return;
        }

        var foregroundProcess = _foreground.GetForegroundProcessName();

        // Checked before reading, not after: reading first would pull a password manager's
        // clipboard into this process's memory before deciding to discard it.
        if (settings.IsProcessIgnored(foregroundProcess))
        {
            IgnoredProcessSkipCount++;
            _trace?.Invoke($"skipped: {foregroundProcess} is an excluded application");
            return;
        }

        var snapshot = _clipboard.TryRead();

        _trace?.Invoke(snapshot is null
            ? $"read seq={sequence} attempt={attempt}: FAILED to open the clipboard"
            : $"read seq={sequence} attempt={attempt}: kind={snapshot.Kind} bytes={snapshot.TotalBytes} "
              + $"formats={snapshot.Payloads.Count} key={Describe(snapshot.DedupKey)} from={foregroundProcess}");

        // An OLE source announces its data object before rendering any of it, so a read that lands between
        // the two sees only bookkeeping. Treated exactly like a failed read - retried on a delay rather than
        // stored - because that is what it is: the copy is real, the content is simply not there yet.
        var contentless = snapshot is { IsEmpty: false }
            && BookkeepingFormats.CarriesNoUserContent(snapshot.Payloads);

        if (snapshot is null || snapshot.IsEmpty || contentless)
        {
            if (contentless)
            {
                BookkeepingOnlySkipCount++;
            }
            else
            {
                ReadFailureCount++;
            }

            // The clipboard is a machine-wide lock, so a read can genuinely lose the race against
            // whichever process is still holding it. Inline backoff alone was measured dropping
            // captures, and silently losing a copy is the worst failure this app can have - so a
            // failed read is retried on a delay rather than abandoned.
            if (attempt < MaxDeferredRetries && _schedule is not null)
            {
                var next = attempt + 1;
                _schedule(_retryDelay, () => Capture(next));
            }
            else
            {
                DroppedCaptureCount++;
            }

            return;
        }

        if (_selfWrites.IsOwnWrite(snapshot.ContentHash))
        {
            SelfWriteSkipCount++;
            _trace?.Invoke("skipped: this is our own write, put there in order to paste");
            return;
        }

        // The SECOND notification for one paste. IsOwnWrite above consumes its entry, so an application that
        // publishes again after the settle window has closed produced a read we no longer recognised - which then
        // matched the consecutive-duplicate branch below, and that branch announces itself. Every paste into such
        // an application therefore ended with a "Same as the last copy" toast. Silent here, deliberately: the
        // paste was never a copy, so there is nothing to acknowledge.
        if (_selfWrites.IsEchoOfOwnWrite(snapshot.ContentHash))
        {
            SelfWriteEchoSkipCount++;
            _trace?.Invoke("skipped: a second notification for the paste we just made - no clip, no notice");
            return;
        }

        if (snapshot.Kind == ClipKind.Image && !settings.StoreImages)
        {
            return;
        }

        if (!settings.AllowDuplicateClips && IsConsecutiveDuplicate(snapshot))
        {
            // The same copy, published a second time - not a repeat the user made. See the setting for the
            // measurements: plain text first, the formatted versions ~190ms later, which is far outside any settle
            // window and used to be announced as "Same as the last copy" on a copy made exactly once.
            // Two conditions, and the second one is what keeps a genuine repeat distinguishable. Time alone cannot
            // tell "the application published the same copy again" from "the user pressed Ctrl+C twice quickly", but
            // a second publish carries MORE than the first - that is the whole reason it happens, the formatted
            // versions arriving after the plain text. An identical repeat carries exactly the same bytes, so it
            // falls through to the notice below, as before.
            if (snapshot.TotalBytes > _lastTotalBytes
                && _lastClipId is { } enrichId
                && IsSameCopyPublishedAgain(settings, out var since))
            {
                RepublishCount++;

                if (_store.ReplacePayloads(enrichId, snapshot))
                {
                    EnrichedCount++;
                    _lastTotalBytes = snapshot.TotalBytes;

                    _trace?.Invoke($"ENRICHED clip {enrichId} {since.TotalMilliseconds:F0}ms after storing it: "
                        + $"{snapshot.Payloads.Count} formats, {snapshot.TotalBytes} bytes replace the poorer set. "
                        + "Same copy published twice - no new clip, no notice.");
                }
                else
                {
                    // The clip went away between the two publishes, so there is nothing to enrich. Still not a
                    // repeat the user made, so still nothing to announce.
                    _trace?.Invoke($"republish arrived {since.TotalMilliseconds:F0}ms later but clip {enrichId} is gone");
                }

                // Deliberately no CaptureObserved: the copy was acknowledged when it was stored, and saying
                // anything here is the bug that was reported.
                return;
            }

            ConsecutiveDuplicateSkipCount++;
            _trace?.Invoke($"SUPPRESSED as a repeat of the previous clip (key={Describe(snapshot.DedupKey)}, "
                + $"clip {_lastClipId}) - this is what shows the \"Same as the last copy\" notice");

            // Reported even though nothing was stored. The user pressed Ctrl+C and is entitled to
            // feedback that it registered; staying silent here made a repeat copy indistinguishable
            // from PasteJump having missed the copy altogether, which is the more alarming reading.
            CaptureObserved?.Invoke();
            return;
        }

        var clip = _store.Add(snapshot, settings.AllowDuplicateClips, out var wasNewCapture);

        _trace?.Invoke($"STORED clip {clip.Id} (new={wasNewCapture}) kind={snapshot.Kind} bytes={snapshot.TotalBytes}");

        _lastDedupKey = snapshot.DedupKey;
        _lastClipId = clip.Id;
        _lastCapturedUtc = _clock.UtcNow;
        _lastTotalBytes = snapshot.TotalBytes;

        if (!wasNewCapture)
        {
            DuplicateNotificationCount++;
        }

        // History records only genuinely new captures. A repeat notification promoted an existing
        // clip rather than adding one, and logging it again would duplicate every OLE-sourced copy.
        if (settings.RecordHistory && wasNewCapture)
        {
            RecordHistory(snapshot);
        }

        _store.EvictBeyond(settings.EffectiveMaxClips);

        ClipCaptured?.Invoke(clip);
    }

    /// <summary>
    /// True when this capture is the same thing the previous capture was, and that previous clip is
    /// still the newest in the stack.
    /// <para>
    /// Compares <see cref="ClipboardSnapshot.DedupKey"/> rather than the content hash, because the
    /// hash covers every clipboard format and the rich formats accompanying text differ between two
    /// copies of the same selection - so hash dedup silently failed for anything copied out of Word,
    /// Excel or a browser, and both the stack and the history filled with apparent duplicates.
    /// </para>
    /// <para>
    /// The newest-clip check is what keeps this from becoming its own annoyance: after deleting a
    /// clip, re-copying the same text must store it again rather than being suppressed against a row
    /// that no longer exists.
    /// </para>
    /// </summary>
    /// <summary>
    /// True when an identical read arrived soon enough after the clip was stored to be the same copy being
    /// published again rather than the user copying the same thing twice.
    /// </summary>
    private bool IsSameCopyPublishedAgain(PasteJumpSettings settings, out TimeSpan since)
    {
        since = TimeSpan.Zero;

        if (settings.ClipboardRepublishMs <= 0 || _lastCapturedUtc is not { } stored)
        {
            return false;
        }

        since = _clock.UtcNow - stored;

        return since >= TimeSpan.Zero && since.TotalMilliseconds <= settings.ClipboardRepublishMs;
    }

    /// <summary>
    /// A dedup key shortened for the trace. Text keys ARE the text, so only a length and a short hash go to the
    /// log - a diagnostics file must never become a copy of everything the user has copied.
    /// </summary>
    private static string Describe(string dedupKey)
    {
        if (dedupKey.StartsWith("h:", StringComparison.Ordinal))
        {
            return dedupKey.Length > 14 ? dedupKey[..14] : dedupKey;
        }

        var hash = (uint)dedupKey.GetHashCode(StringComparison.Ordinal);

        return $"text[{dedupKey.Length}ch #{hash:x8}]";
    }

    private bool IsConsecutiveDuplicate(ClipboardSnapshot snapshot)
    {
        if (_lastDedupKey is null
            || !string.Equals(_lastDedupKey, snapshot.DedupKey, StringComparison.Ordinal))
        {
            return false;
        }

        return _lastClipId is { } id && _store.NewestClipId() == id;
    }

    private void RecordHistory(ClipboardSnapshot snapshot)
    {
        byte[]? blob = null;

        if (snapshot.Kind == ClipKind.Image)
        {
            var dib = snapshot.Payloads.FirstOrDefault(static p => p.FormatId is 8 or 17);

            if (dib is not null)
            {
                blob = DibConverter.TryCreateBitmapFile(dib.Data);
            }
        }
        else if (snapshot.Text is { } full && full.Length > _store.PreviewMaxChars)
        {
            // The preview column is capped, so for anything longer the archive used to keep only the first
            // ClipStore.PreviewMaxChars characters - and History's Copy handed that back as if it were the whole
            // clip, silently. Storing the real text costs little: blobs are content-addressed and deflated, and
            // only entries that actually exceed the cap take this path.
            blob = Encoding.UTF8.GetBytes(full);
        }

        // Files are named here too, and for the stronger reason: this is the row history_fts indexes, so
        // "[files]" meant a file copy could never be found by searching for a file in it.
        var preview = snapshot.Text
            ?? (snapshot.Kind == ClipKind.Files
                ? FileListPreview.TryDescribe(snapshot.Payloads)
                : null)
            ?? snapshot.Kind switch
            {
                ClipKind.Image => "[image]",
                ClipKind.Files => "[files]",

                // Named rather than a bare "[binary]": see BinaryPreview for the report that asked for it.
                _ => BinaryPreview.Describe(snapshot.Payloads),
            };

        _store.AddHistory(_clock.UtcNow, snapshot.Kind, preview, blob, snapshot.TotalBytes);
    }
}
