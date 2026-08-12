using System.Windows.Threading;
using PasteJump.App.Views;
using PasteJump.Core.Abstractions;
using PasteJump.Core.Capture;
using PasteJump.Core.Formatting;
using PasteJump.Core.Imaging;
using PasteJump.Core.Model;
using PasteJump.Core.Paste;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Storage;
using PasteJump.Interop;

namespace PasteJump.App.Services;

/// <summary>
/// Performs the side effects the state machine asks for: clipboard writes, the paste keystroke,
/// overlay rendering and handing off to tool windows.
/// </summary>
public sealed class PasteJumpPasteHost : IPasteModeHost
{
    private readonly ClipStore _store;
    private readonly IClipboardAccess _clipboard;
    private readonly ClipboardPaster _paster;
    private readonly Dispatcher _dispatcher;
    private readonly Func<OverlayWindow> _overlayFactory;

    private OverlayWindow? _overlay;

    private int _previewMaxWidth = 600;
    private int _previewMaxHeight = 400;

    private int? _overlayX;
    private int? _overlayY;

    private bool _showKeyHint = true;
    private char _triggerKey = 'V';

    /// <summary>
    /// What goes between clips when marked ones are pasted joined, in its escaped settings form.
    /// <para>
    /// Held here rather than read from a settings object, like every other setting this host needs: it is
    /// constructed once at start-up and updated on Apply, and reaching back into the live settings would give it
    /// a second source of truth for values it already receives.
    /// </para>
    /// </summary>
    private string _joinSeparator = ClipJoiner.DefaultSeparator;

    /// <summary>Sets the separator used when several marked clips are pasted as one.</summary>
    public void SetJoinSeparator(string separator) => _joinSeparator = separator;

    /// <summary>
    /// Which cosmetic parts of the overlay to draw. Held here for the same reason the key hint is: the overlay is
    /// created lazily on the first gesture, long after the settings were read.
    /// </summary>
    private OverlayParts _overlayParts = OverlayParts.All;

    /// <summary>Sets which parts of the overlay are drawn, applying it to an overlay that already exists.</summary>
    public void SetOverlayParts(OverlayParts parts)
    {
        _overlayParts = parts;
        _overlay?.ApplyParts(parts);
    }

    /// <summary>
    /// Sets the overlay's key reminder and the trigger letter it names. Both together, because the hint has to
    /// name the key that is actually configured.
    /// </summary>
    public void SetKeyHint(bool show, char triggerKey, PasteKeyMap? keyMap = null)
    {
        _showKeyHint = show;
        _triggerKey = triggerKey;
        _keyMap = keyMap;

        _overlay?.ApplyKeyHint(show, triggerKey, keyMap);
    }

    /// <summary>
    /// The letter bindings the hint names. Held because the overlay is created lazily on the first gesture, long
    /// after the settings were read - the same reason the trigger letter is kept here.
    /// </summary>
    private PasteKeyMap? _keyMap;

    /// <summary>
    /// Pins the overlay to a fixed screen position, or restores "follow the caret" when either is null.
    /// <para>
    /// Physical pixels, matching what <see cref="ForegroundWindowInfo.GetPreferredOverlayAnchor"/> returns -
    /// the overlay converts to device-independent units using the DPI of the monitor the anchor lands on, so a
    /// value taken from one display still lands correctly on a mixed-DPI desktop.
    /// </para>
    /// </summary>
    public void SetOverlayAnchor(int? x, int? y)
    {
        _overlayX = x;
        _overlayY = y;
    }

    /// <summary>
    /// Sets the overlay's image-preview ceiling, applying it now if the overlay already exists and remembering
    /// it for when it is next created. Also the width thumbnails are decoded at, so a larger preview is a
    /// larger decode rather than a smaller one stretched.
    /// </summary>
    public void SetPreviewSize(int maxWidth, int maxHeight)
    {
        _previewMaxWidth = maxWidth;
        _previewMaxHeight = maxHeight;

        FileThumbnailCache.SetMaxWidth(maxWidth);
        _overlay?.ApplyPreviewSize(maxWidth, maxHeight);
    }
    private IReadOnlyList<ClipPayload>? _savedClipboard;

    public PasteJumpPasteHost(
        ClipStore store,
        IClipboardAccess clipboard,
        IPasteSender sender,
        SelfWriteGuard selfWrites,
        Dispatcher dispatcher,
        Func<OverlayWindow> overlayFactory)
    {
        _store = store;
        _clipboard = clipboard;
        _dispatcher = dispatcher;
        _overlayFactory = overlayFactory;

        // The scheduler is a one-shot DispatcherTimer rather than a sleep: this code is reached from
        // the keyboard hook callback, which must never block.
        _paster = new ClipboardPaster(clipboard, sender, selfWrites, DelayThen);
        _paster.Message += message => ShowTransientMessage(message);
    }

    /// <summary>Raised with a short message that should be shown to the user transiently.</summary>
    public event Action<string>? TransientMessage;

    /// <summary>Write and paste counters, for diagnostics.</summary>
    public ClipboardPaster Paster => _paster;

    /// <summary>Raised when the user asks to edit tags for a clip.</summary>
    public event Action<Clip>? TagEditorRequested;

    /// <summary>Raised when the user asks to open a clip in an external editor.</summary>
    public event Action<Clip>? ClipEditorRequested;

    /// <summary>Raised when the user asks to export a clip to a file.</summary>
    public event Action<Clip>? ExportRequested;

    /// <summary>Raised when the shortcut help should be shown.</summary>
    public event Action? HelpRequested;

    /// <summary>Raised when the clipboard history window should be shown.</summary>
    public event Action? HistoryRequested;

    /// <summary>
    /// Raised with the number of clips at stake and the action that performs the deletion. Handlers must invoke
    /// the action only if the user agrees.
    /// </summary>
    public event Action<int, Action>? DeleteAllConfirmationRequested;

    public void SnapshotExistingClipboard()
    {
        var snapshot = _clipboard.TryRead();
        _savedClipboard = snapshot?.Payloads;
    }

    public void RestoreExistingClipboard()
    {
        var payloads = _savedClipboard;
        _savedClipboard = null;

        if (payloads is null || payloads.Count == 0)
        {
            return;
        }

        QueueClipboardWrite(payloads, thenPaste: false);
    }

    public void PasteClip(Clip clip, IClipFormatter formatter)
    {
        // Payloads are read synchronously, before returning. The controller may delete this clip
        // immediately afterwards (paste-popping with Shift), so deferring the read would race a
        // cascading row delete and paste nothing.
        var payloads = BuildPayloads(clip, formatter);

        if (payloads.Count == 0)
        {
            _dispatcher.BeginInvoke(() => _paster.SendPasteOnly());
            return;
        }

        QueueClipboardWrite(payloads, thenPaste: true);
    }

    /// <summary>
    /// Writes several clips' text as one, joined, and pastes it.
    /// <para>
    /// Read synchronously before returning, exactly as <see cref="PasteClip"/> is and for the same reason: the
    /// controller may delete every one of these immediately afterwards when Shift is held, so a deferred read
    /// would race the deletion and paste nothing.
    /// </para>
    /// <para>
    /// The formatter is applied to the <em>joined</em> text rather than to each clip, so "trim whitespace" trims
    /// the block rather than every line of it, and a formatter that adds a prefix adds one prefix. That is the
    /// reading that matches what the user sees on the overlay: one clip, about to be pasted once.
    /// </para>
    /// </summary>
    public void PasteJoined(IReadOnlyList<Clip> clips, IClipFormatter formatter)
    {
        // Null for anything with no text of its own, which ClipJoiner counts. Decided by kind rather than by
        // whether text turns up, because a clip with no text still has PREVIEW text and that preview is a
        // placeholder - "[image]" - which would otherwise be pasted as though it had been copied.
        var texts = clips.Select(clip => ClipJoiner.HasJoinableText(clip.Kind)
            ? Win32ClipboardAccess.ExtractText(_store.GetPayloads(clip.Id))
            : null);

        var result = ClipJoiner.Join(texts, ClipJoiner.ParseSeparator(_joinSeparator));

        if (result.Joined == 0)
        {
            // Every marked clip was an image. Nothing to write, so paste whatever is already on the clipboard
            // rather than clearing it - the same choice PasteClip makes for a clip with no payloads.
            _dispatcher.BeginInvoke(() => _paster.SendPasteOnly());
            return;
        }

        var text = formatter.Apply(result.Text);

        QueueClipboardWrite(Win32ClipboardAccess.TextOnlyPayloads(text), thenPaste: true);
    }

    public void PassThroughPaste() => _dispatcher.BeginInvoke(() => _paster.SendPasteOnly());

    public void PushToClipboard(Clip clip, IClipFormatter formatter)
    {
        var payloads = BuildPayloads(clip, formatter);

        if (payloads.Count > 0)
        {
            QueueClipboardWrite(payloads, thenPaste: false);
        }

        // Pushing to the clipboard is an explicit "keep this" action, so the pre-session clipboard
        // must not be restored over the top of it afterwards.
        _savedClipboard = null;
    }

    public void ShowOverlay(PasteOverlayModel model)
    {
        if (_overlay is null)
        {
            _overlay = _overlayFactory();

            // Applied on creation as well as on change, because the overlay is created lazily on the first
            // gesture - which is usually long after the settings were loaded.
            _overlay.ApplyPreviewSize(_previewMaxWidth, _previewMaxHeight);
            _overlay.ApplyKeyHint(_showKeyHint, _triggerKey, _keyMap);
            _overlay.ApplyParts(_overlayParts);
        }

        // A configured position wins over the caret. Both halves have to be set for it to mean anything, which
        // is why one alone is not honoured rather than being paired with a caret coordinate - a half-fixed
        // overlay that moves in one axis only reads as a bug rather than as a setting.
        var (anchorX, anchorY) = _overlayX is { } fixedX && _overlayY is { } fixedY
            ? (fixedX, fixedY)
            : ForegroundWindowInfo.GetPreferredOverlayAnchor();

        _overlay.SetImagePayload(model.Kind == ClipKind.Image ? TryLoadImageBytes(model) : null);

        if (!_overlay.IsVisible)
        {
            _overlay.Show();
        }

        _overlay.Render(model, anchorX, anchorY);
    }

    public void HideOverlay()
    {
        if (_overlay is { IsVisible: true })
        {
            _overlay.Hide();
        }
    }

    public void RequestTagEditor(Clip clip) => _dispatcher.BeginInvoke(() => TagEditorRequested?.Invoke(clip));

    public void RequestClipEditor(Clip clip) => _dispatcher.BeginInvoke(() => ClipEditorRequested?.Invoke(clip));

    public void RequestExport(Clip clip) => _dispatcher.BeginInvoke(() => ExportRequested?.Invoke(clip));

    public void ShowShortcutHelp() => _dispatcher.BeginInvoke(() => HelpRequested?.Invoke());

    public void RequestHistoryWindow() => _dispatcher.BeginInvoke(() => HistoryRequested?.Invoke());

    /// <summary>
    /// Queues the confirmation and returns at once. The BeginInvoke is the whole point: this is reached from the
    /// keyboard hook, and showing the dialog inline would run a nested message loop on the UI thread - blocking
    /// every keystroke on the machine until the user answered.
    /// </summary>
    public void RequestDeleteAllConfirmation(int unpinnedCount, Action confirmed)
        => _dispatcher.BeginInvoke(() => DeleteAllConfirmationRequested?.Invoke(unpinnedCount, confirmed));

    public void ShowTransientMessage(string message)
        => _dispatcher.BeginInvoke(() => TransientMessage?.Invoke(message));

    // ------------------------------------------------------------- internals

    /// <summary>
    /// Applies the formatter and produces the payload set to put on the clipboard.
    /// <para>
    /// Note how the HTML byte-offset problem disappears here. A formatter that rewrites text also
    /// declares <see cref="IClipFormatter.TextOnlyOutput"/>, so the rich formats are dropped rather
    /// than shipped alongside altered text - which means we never have to rewrite the offsets in an
    /// <c>HTML Format</c> header, and never have to ship stale HTML next to edited plain text.
    /// </para>
    /// </summary>
    private IReadOnlyList<ClipPayload> BuildPayloads(Clip clip, IClipFormatter formatter)
    {
        var stored = _store.GetPayloads(clip.Id);

        if (stored.Count == 0)
        {
            return [];
        }

        if (!formatter.TextOnlyOutput)
        {
            return stored;
        }

        var text = Win32ClipboardAccess.ExtractText(stored);

        if (text is null)
        {
            // Nothing textual to transform - an image or a file list. Narrowing would produce an
            // empty clipboard, so keep the original formats.
            return stored;
        }

        return Win32ClipboardAccess.TextOnlyPayloads(formatter.Apply(text));
    }

    /// <summary>
    /// Queues a clipboard write, and optionally the paste keystroke, onto the dispatcher.
    /// <para>
    /// Deferring is not cosmetic. These calls originate inside the low-level keyboard hook
    /// callback, which blocks all keyboard input machine-wide until it returns and is discarded
    /// outright if it exceeds <c>LowLevelHooksTimeout</c>. Opening the clipboard and calling
    /// <c>SendInput</c> there would risk both. Queued work also runs FIFO at the same priority,
    /// so restore-then-hide and write-then-paste keep their order.
    /// </para>
    /// </summary>
    private void QueueClipboardWrite(IReadOnlyList<ClipPayload> payloads, bool thenPaste)
        => _dispatcher.BeginInvoke(() => _paster.Write(payloads, thenPaste));

    /// <summary>
    /// Runs an action after a delay on the UI thread. A one-shot <see cref="DispatcherTimer"/>
    /// rather than a sleep, because this code path is reached from the keyboard hook and must never
    /// block: a hook callback that stalls past <c>LowLevelHooksTimeout</c> is silently unhooked and
    /// the app stops seeing keys at all.
    /// </summary>
    private void DelayThen(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Send, _dispatcher) { Interval = delay };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };

        timer.Start();
    }

    private byte[]? TryLoadImageBytes(PasteOverlayModel model)
    {
        // The overlay model carries no clip id, so resolve the current clip by position from the
        // store. Cheap: this is a single indexed read of at most a few hundred rows.
        if (model.IsEmpty || model.Position < 1)
        {
            return null;
        }

        var clips = _store.GetOrdered(model.Position);

        if (clips.Count < model.Position)
        {
            return null;
        }

        var clip = clips[model.Position - 1];

        if (clip.Kind != ClipKind.Image)
        {
            return null;
        }

        var payloads = _store.GetPayloads(clip.Id);

        var dib = payloads.FirstOrDefault(static p => p.FormatId == 8)
            ?? payloads.FirstOrDefault(static p => p.FormatId == 17);

        return dib is null ? null : DibConverter.TryCreateBitmapFile(dib.Data);
    }
}
