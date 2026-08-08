using PasteJump.Core.Abstractions;
using PasteJump.Core.Capture;
using PasteJump.Core.Imaging;
using PasteJump.Core.Model;
using PasteJump.Core.Settings;

namespace PasteJump.Core.Paste;

/// <summary>
/// Puts a clip on the clipboard and then, only if that succeeded, sends the paste keystroke.
/// <para>
/// This lives in Core rather than in the WPF host precisely so the ordering rule below can be
/// tested. It was originally inline in the host as "write, then send Ctrl+V" with the write's return
/// value discarded, and that is a data-loss-shaped bug rather than a cosmetic one: clipboard writes
/// genuinely fail, because the clipboard is a machine-wide lock any process can be holding, and when
/// the write failed the target application still received Ctrl+V and pasted whatever was there
/// before - the user's previous content, or the previously-selected clip. Silently pasting the wrong
/// thing is worse than pasting nothing, so the keystroke is now strictly conditional on the write.
/// </para>
/// </summary>
public sealed class ClipboardPaster
{
    /// <summary>Total write attempts before a paste is abandoned.</summary>
    public const int MaxWriteAttempts = 4;

    /// <summary>Base delay between write attempts, multiplied by the attempt number.</summary>
    public static readonly TimeSpan WriteRetryDelay = TimeSpan.FromMilliseconds(30);

    private readonly IClipboardAccess _clipboard;
    private readonly IPasteSender _sender;
    private readonly SelfWriteGuard _selfWrites;
    private readonly Action<TimeSpan, Action> _schedule;

    private TimeSpan _settleDelay = TimeSpan.FromMilliseconds(25);
    private PasteKeystroke _keystroke = PasteKeystroke.CtrlV;

    /// <param name="schedule">
    /// Runs an action after a delay. Injected rather than using a timer directly because this code
    /// is reached from inside the low-level keyboard hook callback, which blocks all keyboard input
    /// until it returns and is silently discarded if it outlives <c>LowLevelHooksTimeout</c> - so
    /// nothing here may ever sleep. Tests pass a scheduler that runs actions immediately.
    /// </param>
    public ClipboardPaster(
        IClipboardAccess clipboard,
        IPasteSender sender,
        SelfWriteGuard selfWrites,
        Action<TimeSpan, Action> schedule)
    {
        _clipboard = clipboard;
        _sender = sender;
        _selfWrites = selfWrites;
        _schedule = schedule;
    }

    /// <summary>Raised with a short message that should be surfaced to the user transiently.</summary>
    public event Action<string>? Message;

    /// <summary>Successful clipboard writes.</summary>
    public int WriteCount { get; private set; }

    /// <summary>Failed write attempts, including ones a later retry rescued.</summary>
    public int WriteFailureCount { get; private set; }

    /// <summary>Pastes abandoned because every write attempt failed. Should stay at zero.</summary>
    public int AbandonedCount { get; private set; }

    /// <summary>Keystrokes the OS refused to deliver. Non-zero means an elevated target window.</summary>
    public int SendFailureCount { get; private set; }

    /// <summary>Paste keystrokes successfully delivered.</summary>
    public int PasteCount { get; private set; }

    /// <summary>
    /// Gap between a successful write and the keystroke. Settable because the applications that need
    /// it - Office, Electron shells, remote-desktop clients - differ in how long they take to drop
    /// their cached copy of the clipboard.
    /// </summary>
    public void SetSettleDelay(int milliseconds)
        => _settleDelay = TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 0, 500));

    /// <summary>
    /// Chord used to make the target application paste. See <see cref="PasteKeystroke"/> for why this is
    /// not simply always Ctrl+V.
    /// </summary>
    public void SetPasteKeystroke(PasteKeystroke keystroke) => _keystroke = keystroke;

    /// <summary>The chord currently in use. Exposed so the setting can be asserted rather than assumed.</summary>
    public PasteKeystroke Keystroke => _keystroke;

    /// <summary>Writes the payloads, then pastes if <paramref name="thenPaste"/> and the write worked.</summary>
    public void Write(IReadOnlyList<ClipPayload> payloads, bool thenPaste)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        if (payloads.Count == 0)
        {
            return;
        }

        Attempt(payloads, thenPaste, attempt: 1);
    }

    /// <summary>
    /// Sends Ctrl+V without touching the clipboard, for the pass-through path where the store is
    /// empty and the user's own Ctrl+V has to be honoured rather than swallowed.
    /// </summary>
    public bool SendPasteOnly() => SendPaste();

    private void Attempt(IReadOnlyList<ClipPayload> payloads, bool thenPaste, int attempt)
    {
        payloads = NormaliseImageAlpha(payloads);

        // Register the hash before writing, so the clipboard-change notification our own write
        // provokes is recognised as ours instead of being captured as a brand new clip. Computed on
        // the normalised payloads, since those are what actually reach the clipboard.
        var snapshot = new ClipboardSnapshot(payloads, null, ClipKind.Other, null);
        _selfWrites.NoteWrite(snapshot.ContentHash);

        if (_clipboard.TryWrite(payloads))
        {
            WriteCount++;

            if (thenPaste)
            {
                _schedule(_settleDelay, () => SendPaste());
            }

            return;
        }

        WriteFailureCount++;

        if (attempt < MaxWriteAttempts)
        {
            _schedule(WriteRetryDelay * attempt, () => Attempt(payloads, thenPaste, attempt + 1));
            return;
        }

        AbandonedCount++;

        if (thenPaste)
        {
            // No keystroke. See the type remarks: pasting stale content here is the bug.
            Message?.Invoke("Clipboard is busy - nothing was pasted");
        }
    }

    /// <summary>
    /// Repairs image payloads whose alpha channel is entirely zero, immediately before they go on the
    /// clipboard.
    /// <para>
    /// Applied on write rather than on capture on purpose: what we captured is a faithful record of
    /// what the source application published, and rewriting it in the store would destroy that. The
    /// repair belongs at the point where the bytes are handed to a consumer that will interpret alpha.
    /// </para>
    /// <para>
    /// See <see cref="DibConverter.TryMakeOpaqueIfFullyTransparent"/> for why an all-zero channel is
    /// safe to treat as "alpha not meaningful" while a partially-zero one is not.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ClipPayload> NormaliseImageAlpha(IReadOnlyList<ClipPayload> payloads)
    {
        List<ClipPayload>? repaired = null;

        for (var i = 0; i < payloads.Count; i++)
        {
            var payload = payloads[i];

            // CF_DIB and CF_DIBV5.
            if (payload.FormatId is not (8 or 17))
            {
                continue;
            }

            var opaque = DibConverter.TryMakeOpaqueIfFullyTransparent(payload.Data);

            if (opaque is null)
            {
                continue;
            }

            // Copy-on-first-change: the overwhelmingly common case is nothing to do, and that path
            // should not allocate.
            repaired ??= [.. payloads];
            repaired[i] = new ClipPayload(payload.FormatId, payload.FormatName, opaque);
        }

        return repaired ?? payloads;
    }

    private bool SendPaste()
    {
        if (_sender.SendPaste(_keystroke))
        {
            PasteCount++;
            return true;
        }

        // SendInput refused outright. The realistic cause is an elevated foreground window: UIPI
        // discards synthetic input aimed at a higher integrity level, and retrying cannot help.
        SendFailureCount++;

        Message?.Invoke(
            "Could not paste into this window. It is probably running as administrator, " +
            "which means PasteJump has to run elevated too in order to send keys to it.");

        return false;
    }
}
