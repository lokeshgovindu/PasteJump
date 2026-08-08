using PasteJump.Core.Formatting;
using PasteJump.Core.Model;

namespace PasteJump.Core.PasteMode;

/// <summary>
/// The paste-mode state machine: hold the modifier, tap keys, release to commit.
/// <para>
/// This class is the product. It is deliberately pure - no Win32, no clipboard, no windows, no
/// timers - so every transition and every invariant below is testable without a UI. In the
/// original this logic was spread across the <c>paste:</c>, <c>ctrlCheck:</c>, <c>moveBack:</c>,
/// <c>cancel:</c>, <c>delete:</c>, <c>cutclip:</c>, <c>copyclip:</c>, <c>deleteall:</c> and
/// <c>endPastemode:</c> labels, coordinated through about a dozen mutable globals and
/// <c>Critical</c> sections, which is precisely why it could not be reasoned about.
/// </para>
/// <para>Invariants, each covered by a test:</para>
/// <list type="number">
/// <item>Releasing the modifier always ends a <see cref="PasteSessionState.Browsing"/> session.</item>
/// <item>Cancel, Delete and DeleteAll restore the clipboard that existed before the session.</item>
/// <item>A paste intentionally leaves the pasted clip on the clipboard, so a following native
/// Ctrl+V repeats it.</item>
/// <item>In <see cref="PasteSessionState.Searching"/>, releasing the modifier does <em>not</em>
/// commit.</item>
/// <item>An empty store passes Ctrl+V through instead of swallowing it.</item>
/// </list>
/// </summary>
public sealed class PasteModeController
{
    /// <summary>How much clip text the overlay shows. The original used 200 characters.</summary>
    public const int OverlayPreviewChars = 400;

    private readonly IClipCatalog _catalog;
    private readonly IPasteModeHost _host;
    private readonly FormatterRegistry _formatters;
    private readonly PasteModeOptions _options;

    private List<Clip> _window = [];
    private int _cursor;
    private int _jumpDirection = 1;
    private long? _preservedClipId;

    public PasteModeController(
        IClipCatalog catalog,
        IPasteModeHost host,
        FormatterRegistry formatters,
        PasteModeOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _formatters = formatters ?? throw new ArgumentNullException(nameof(formatters));
        _options = options ?? new PasteModeOptions();
        Formatter = _formatters.Resolve(_options.DefaultFormatterId);
    }

    public PasteSessionState State { get; private set; } = PasteSessionState.Idle;

    public PasteCommitMode CommitMode { get; private set; } = PasteCommitMode.Paste;

    public IClipFormatter Formatter { get; private set; }

    /// <summary>Set by the hook as Shift goes up and down. Shift on release deletes after pasting.</summary>
    public bool ShiftHeld { get; set; }

    public string SearchQuery { get; private set; } = string.Empty;

    public int CursorIndex => _cursor;

    public IReadOnlyList<Clip> Window => _window;

    public Clip? Current => _cursor >= 0 && _cursor < _window.Count ? _window[_cursor] : null;

    public bool IsActive => State != PasteSessionState.Idle;

    /// <summary>
    /// Enters paste mode. Returns the commit kind when the session ended immediately - which
    /// happens when there is nothing to show and the keystroke is passed through.
    /// </summary>
    public PasteCommitKind Begin()
    {
        if (IsActive)
        {
            // Already browsing: a repeat of the entry chord means "advance", which is what makes
            // tapping V a second time step to the next clip.
            Advance();
            return PasteCommitKind.None;
        }

        _host.SnapshotExistingClipboard();

        CommitMode = PasteCommitMode.Paste;
        SearchQuery = string.Empty;
        _jumpDirection = 1;
        RefreshWindow();

        if (_window.Count == 0)
        {
            // Invariant 5. Never swallow Ctrl+V when we have nothing to offer.
            State = PasteSessionState.Idle;
            _host.PassThroughPaste();
            return PasteCommitKind.PassedThrough;
        }

        State = PasteSessionState.Browsing;

        _cursor = ResolveInitialCursor();

        if (_options.OpenSearchImmediately)
        {
            State = PasteSessionState.Searching;
        }

        if (_options.ResetFormatterOnEntry)
        {
            Formatter = _formatters.Resolve(_options.DefaultFormatterId);
        }

        Render();
        return PasteCommitKind.None;
    }

    /// <summary>Handles a paste-mode key. No-op when idle.</summary>
    public PasteCommitKind Handle(PasteAction action)
    {
        if (!IsActive)
        {
            return PasteCommitKind.None;
        }

        switch (action)
        {
            case PasteAction.Advance:
                Advance();
                break;

            case PasteAction.Back:
                Step(-1);
                break;

            case PasteAction.CycleCommitMode:
                CommitMode = CommitMode switch
                {
                    PasteCommitMode.Paste => PasteCommitMode.Cancel,
                    PasteCommitMode.Cancel => PasteCommitMode.Delete,
                    PasteCommitMode.Delete => PasteCommitMode.DeleteAll,
                    _ => PasteCommitMode.Cancel,
                };
                Render();
                break;

            case PasteAction.JumpToNewest:
                _cursor = 0;
                Render();
                break;

            case PasteAction.PromoteToFront:
                return PromoteToFront();

            case PasteAction.ToggleSearch:
                State = State == PasteSessionState.Searching
                    ? PasteSessionState.Browsing
                    : PasteSessionState.Searching;

                if (State == PasteSessionState.Browsing)
                {
                    // Leaving search keeps the clip you landed on but drops the filter.
                    var landed = Current?.Id;
                    SearchQuery = string.Empty;
                    RefreshWindow();
                    RestoreCursorTo(landed);
                }

                Render();
                break;

            case PasteAction.CycleFormatter:
                Formatter = _formatters.Next(Formatter);
                Render();
                break;

            case PasteAction.TogglePin:
                return TogglePin();

            case PasteAction.EditTags:
                return EndAndDelegate(static (host, clip) => host.RequestTagEditor(clip));

            case PasteAction.PushToClipboard:
                return PushCurrentToClipboard();

            case PasteAction.EditClip:
                return EndAndDelegate(static (host, clip) => host.RequestClipEditor(clip));

            case PasteAction.ExportClip:
                return EndAndDelegate(static (host, clip) => host.RequestExport(clip));

            case PasteAction.Multipaste:
                return Multipaste();

            case PasteAction.Help:
                _host.ShowShortcutHelp();
                break;

            case PasteAction.ToggleJumpDirection:
                _jumpDirection = -_jumpDirection;
                Render();
                break;

            case PasteAction.Escape:
                return Abort();

            default:
                break;
        }

        return PasteCommitKind.None;
    }

    /// <summary>Digit keys 1-9: jump that many clips in the current direction.</summary>
    public PasteCommitKind HandleDigit(int digit)
    {
        if (!IsActive || digit is < 1 or > 9)
        {
            return PasteCommitKind.None;
        }

        Step(_jumpDirection * digit);
        return PasteCommitKind.None;
    }

    /// <summary>Updates the incremental search filter. Only meaningful while searching.</summary>
    public void SetSearchQuery(string query)
    {
        if (State != PasteSessionState.Searching)
        {
            return;
        }

        SearchQuery = query ?? string.Empty;
        RefreshWindow();
        _cursor = _window.Count == 0 ? 0 : Math.Clamp(_cursor, 0, _window.Count - 1);
        Render();
    }

    /// <summary>
    /// The modifier came up. Commits a browsing session; deliberately does nothing while
    /// searching (invariant 4), because in search mode the user needs both hands free to type and
    /// the session is ended explicitly with Enter or Escape.
    /// </summary>
    public PasteCommitKind ModifierReleased()
    {
        if (State != PasteSessionState.Browsing)
        {
            return PasteCommitKind.None;
        }

        return Commit();
    }

    /// <summary>Explicit commit, used by Enter inside search mode.</summary>
    public PasteCommitKind CommitExplicitly()
    {
        return IsActive ? Commit() : PasteCommitKind.None;
    }

    /// <summary>
    /// Tears the session down without committing anything and restores the clipboard.
    /// Safe to call at any time - used by Escape, by focus loss and by app shutdown.
    /// </summary>
    public PasteCommitKind Abort()
    {
        if (!IsActive)
        {
            return PasteCommitKind.None;
        }

        _host.RestoreExistingClipboard();
        EndSession();
        return PasteCommitKind.Cancelled;
    }

    // ------------------------------------------------------------- transitions

    private PasteCommitKind Commit()
    {
        var current = Current;

        switch (CommitMode)
        {
            case PasteCommitMode.Paste when current is not null:
            {
                var pop = ShiftHeld;

                // Order matters: paste first, then delete. Deleting first would drop the payload
                // we are about to write to the clipboard.
                _host.PasteClip(current, Formatter);

                if (pop)
                {
                    _catalog.Delete(current.Id);
                }

                EndSession();
                return PasteCommitKind.Pasted;
            }

            case PasteCommitMode.Paste:
                // Window emptied underneath us (for example everything was deleted mid-session).
                _host.PassThroughPaste();
                EndSession();
                return PasteCommitKind.PassedThrough;

            case PasteCommitMode.Cancel:
                // Invariant 2.
                _host.RestoreExistingClipboard();
                EndSession();
                return PasteCommitKind.Cancelled;

            case PasteCommitMode.Delete:
            {
                if (current is not null)
                {
                    _catalog.Delete(current.Id);
                }

                _host.RestoreExistingClipboard();
                EndSession();
                return PasteCommitKind.Deleted;
            }

            case PasteCommitMode.DeleteAll:
            {
                // Asked, not done. Three taps of X and a natural Ctrl release is a plausible accident, and this
                // is the only irreversible thing the gesture can do - so it now needs an answer. The prompt
                // cannot happen here: this runs in the keyboard hook, where anything modal blocks all keyboard
                // input machine-wide. See IPasteModeHost.RequestDeleteAllConfirmation.
                var unpinned = _catalog.Snapshot().Count(static c => !c.Pinned);

                _host.RequestDeleteAllConfirmation(unpinned, _catalog.DeleteAllUnpinned);

                // Invariant 2 still holds, and holds immediately: the clipboard goes back whether or not the
                // deletion is ever confirmed.
                _host.RestoreExistingClipboard();
                EndSession();
                return PasteCommitKind.DeleteAllRequested;
            }

            default:
                EndSession();
                return PasteCommitKind.None;
        }
    }

    private PasteCommitKind Multipaste()
    {
        var current = Current;

        if (current is null)
        {
            return PasteCommitKind.None;
        }

        _host.PasteClip(current, Formatter);

        // Stay resident and step on, so a run of clips can be pasted without releasing the
        // modifier. Refresh because pasting may have reordered things.
        var landed = current.Id;
        RefreshWindow();
        RestoreCursorTo(landed);
        Advance();

        return PasteCommitKind.Pasted;
    }

    private PasteCommitKind PushCurrentToClipboard()
    {
        var current = Current;

        if (current is null)
        {
            return PasteCommitKind.None;
        }

        _host.PushToClipboard(current, Formatter);
        EndSession();
        return PasteCommitKind.PushedToClipboard;
    }

    private PasteCommitKind TogglePin()
    {
        var current = Current;

        if (current is null)
        {
            return PasteCommitKind.None;
        }

        _catalog.SetPinned(current.Id, !current.Pinned);

        // Pinning reorders the window, so re-find the same clip rather than trusting the index.
        RefreshWindow();
        RestoreCursorTo(current.Id);
        Render();
        return PasteCommitKind.None;
    }

    private PasteCommitKind PromoteToFront()
    {
        var current = Current;

        if (current is null)
        {
            return PasteCommitKind.None;
        }

        _catalog.MoveToFront(current.Id);
        RefreshWindow();
        RestoreCursorTo(current.Id);
        Render();
        return PasteCommitKind.None;
    }

    private PasteCommitKind EndAndDelegate(Action<IPasteModeHost, Clip> request)
    {
        var current = Current;

        if (current is null)
        {
            return PasteCommitKind.None;
        }

        // These open real windows, which needs focus - so the transient session must end first,
        // clipboard restored, before handing over.
        _host.RestoreExistingClipboard();
        EndSession();
        request(_host, current);
        return PasteCommitKind.Cancelled;
    }

    private void Advance() => Step(1);

    private void Step(int delta)
    {
        if (_window.Count == 0)
        {
            return;
        }

        var count = _window.Count;
        _cursor = ((_cursor + delta) % count + count) % count;
        Render();
    }

    private void EndSession()
    {
        _preservedClipId = _options.PreserveClipPosition ? Current?.Id : null;
        State = PasteSessionState.Idle;
        CommitMode = PasteCommitMode.Paste;
        SearchQuery = string.Empty;
        ShiftHeld = false;
        _window = [];
        _cursor = 0;
        _host.HideOverlay();
    }

    private int ResolveInitialCursor()
    {
        if (!_options.PreserveClipPosition || _preservedClipId is not { } id)
        {
            return 0;
        }

        var index = _window.FindIndex(c => c.Id == id);
        return index < 0 ? 0 : index;
    }

    /// <summary>
    /// Tells the controller that a clip was captured, so the next session opens on the newest clip
    /// rather than wherever the last one ended.
    /// <para>
    /// This is a distinct rule from <see cref="PasteModeOptions.PreserveClipPosition"/>, and conflating
    /// the two was a real bug: the remembered position was set when a session ended and then never
    /// cleared, so once the user had browsed to the fifth clip, every later Ctrl+V reopened on that
    /// same clip no matter how much had been copied since. Copy five file paths and the gesture still
    /// offered the old one.
    /// </para>
    /// <para>
    /// The original is unambiguous about the split. <c>clipChange()</c> assigns
    /// <c>TEMPSAVE := CURSAVE</c> on every successful copy (Clipjump.ahk:508 and :517) with no regard
    /// for the setting, while <c>ini_PreserveClipPos</c> is consulted only when a paste session ends
    /// (Clipjump.ahk:1010-1012). So: a new copy always resets the position; the setting only governs
    /// whether the position survives a paste.
    /// </para>
    /// <para>
    /// Deliberately not called for a copy that was suppressed as a consecutive duplicate. Nothing moved
    /// in the stack, and the original likewise never reaches its reset in that case - the duplicate
    /// check returns before it.
    /// </para>
    /// </summary>
    public void NotifyClipCaptured()
    {
        _preservedClipId = null;

        // A capture during an open session is not a reason to move the cursor out from under the user;
        // they can see the overlay and are mid-gesture. The reset applies to the next session.
        if (!IsActive)
        {
            _cursor = 0;
        }
    }

    private void RefreshWindow()
    {
        var all = _catalog.Snapshot();

        _window = string.IsNullOrWhiteSpace(SearchQuery)
            ? [.. all]
            : [.. all.Where(c => Matches(c, SearchQuery))];

        if (_cursor >= _window.Count)
        {
            _cursor = _window.Count == 0 ? 0 : _window.Count - 1;
        }
    }

    private void RestoreCursorTo(long? clipId)
    {
        if (clipId is not { } id)
        {
            return;
        }

        var index = _window.FindIndex(c => c.Id == id);

        if (index >= 0)
        {
            _cursor = index;
        }
        else if (_cursor >= _window.Count)
        {
            _cursor = _window.Count == 0 ? 0 : _window.Count - 1;
        }
    }

    /// <summary>
    /// AND-of-tokens, case-insensitive, over preview text and tags. Matching tags as well as
    /// content is what makes tagging worth having - the original searched both
    /// (searchPasteMode.ahk:91) and dropping it would quietly gut the feature.
    /// </summary>
    internal static bool Matches(Clip clip, string query)
    {
        var tokens = query.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return true;
        }

        var haystack = clip.Tags.Count == 0
            ? clip.Preview
            : clip.Preview + " " + string.Join(' ', clip.Tags);

        foreach (var token in tokens)
        {
            if (!haystack.Contains(token, StringComparison.CurrentCultureIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void Render()
    {
        var current = Current;

        _host.ShowOverlay(new PasteOverlayModel
        {
            Position = _window.Count == 0 ? 0 : _cursor + 1,
            Total = _window.Count,
            PreviewText = BuildPreviewText(current),
            Kind = current?.Kind ?? ClipKind.Text,
            Pinned = current?.Pinned ?? false,
            Tags = current?.Tags ?? [],
            FormatterName = Formatter.DisplayName,
            CommitMode = CommitMode,
            IsSearching = State == PasteSessionState.Searching,
            SearchQuery = SearchQuery,
            MatchCount = _window.Count,
            PopOnPaste = ShiftHeld && CommitMode == PasteCommitMode.Paste,
            IsEmpty = current is null,
            SourceExecutable = current?.SourceExecutable,
        });
    }

    private static string BuildPreviewText(Clip? clip)
    {
        if (clip is null)
        {
            return string.Empty;
        }

        var preview = clip.Preview;
        return preview.Length <= OverlayPreviewChars
            ? preview
            : preview[..OverlayPreviewChars] + "…";
    }
}
