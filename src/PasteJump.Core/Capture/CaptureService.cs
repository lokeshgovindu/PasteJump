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

    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(350);

    private readonly IClipboardAccess _clipboard;
    private readonly ClipStore _store;
    private readonly SelfWriteGuard _selfWrites;
    private readonly IForegroundWindowInfo _foreground;
    private readonly Func<PasteJumpSettings> _settings;
    private readonly IClock _clock;
    private readonly Action<TimeSpan, Action>? _schedule;
    private readonly TimeSpan _retryDelay;

    private uint _lastSequenceNumber;
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
        TimeSpan? retryDelay = null)
    {
        _clipboard = clipboard;
        _store = store;
        _selfWrites = selfWrites;
        _foreground = foreground;
        _settings = settings;
        _clock = clock ?? SystemClock.Instance;
        _schedule = schedule;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
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

    /// <summary>Read attempts that could not open the clipboard, including retries.</summary>
    public int ReadFailureCount { get; private set; }

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

    /// <summary>Call once at start-up so the first real change is not mistaken for a new one.</summary>
    public void Prime() => _lastSequenceNumber = _clipboard.SequenceNumber;

    /// <summary>Handles a clipboard-change notification.</summary>
    public void OnClipboardChanged() => Capture(attempt: 0);

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
                return;
            }

            _lastSequenceNumber = sequence;
        }
        else if (sequence != _lastSequenceNumber)
        {
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
            return;
        }

        var snapshot = _clipboard.TryRead();

        if (snapshot is null || snapshot.IsEmpty)
        {
            ReadFailureCount++;

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
            return;
        }

        if (snapshot.Kind == ClipKind.Image && !settings.StoreImages)
        {
            return;
        }

        if (!settings.AllowDuplicateClips && IsConsecutiveDuplicate(snapshot))
        {
            ConsecutiveDuplicateSkipCount++;

            // Reported even though nothing was stored. The user pressed Ctrl+C and is entitled to
            // feedback that it registered; staying silent here made a repeat copy indistinguishable
            // from PasteJump having missed the copy altogether, which is the more alarming reading.
            CaptureObserved?.Invoke();
            return;
        }

        var clip = _store.Add(snapshot, settings.AllowDuplicateClips, out var wasNewCapture);

        _lastDedupKey = snapshot.DedupKey;
        _lastClipId = clip.Id;

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
        else if (snapshot.Text is { } full && full.Length > ClipStore.PreviewMaxChars)
        {
            // The preview column is capped, so for anything longer the archive used to keep only the first
            // ClipStore.PreviewMaxChars characters - and History's Copy handed that back as if it were the whole
            // clip, silently. Storing the real text costs little: blobs are content-addressed and deflated, and
            // only entries that actually exceed the cap take this path.
            blob = Encoding.UTF8.GetBytes(full);
        }

        var preview = snapshot.Text
            ?? snapshot.Kind switch
            {
                ClipKind.Image => "[image]",
                ClipKind.Files => "[files]",
                _ => "[binary]",
            };

        _store.AddHistory(_clock.UtcNow, snapshot.Kind, preview, blob, snapshot.TotalBytes);
    }
}
