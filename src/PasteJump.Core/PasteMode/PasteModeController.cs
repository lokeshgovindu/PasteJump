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
    /// <summary>
    /// How much clip text the overlay shows when nothing else is configured. The original used 200 characters.
    /// The live value is <see cref="PasteModeOptions.OverlayPreviewChars"/>; this is only its default, kept here
    /// so the constant and the setting cannot disagree.
    /// </summary>
    public const int DefaultOverlayPreviewChars = 400;

    private readonly IClipCatalog _catalog;
    private readonly IPasteModeHost _host;
    private readonly FormatterRegistry _formatters;
    private readonly PasteModeOptions _options;

    private List<Clip> _window = [];
    private int _cursor;
    private int _jumpDirection = 1;
    private long? _preservedClipId;

    /// <summary>
    /// Clips marked to be pasted joined, in the order they were marked.
    /// <para>
    /// A list rather than a set, because the order is the user's: they mark clips in the sequence they want them.
    /// Ids rather than clips, so a mark survives the window being rebuilt by a search or a kind filter - which it
    /// must, since narrowing the stack to find the next clip to mark is the obvious way to use this.
    /// </para>
    /// </summary>
    private readonly List<long> _marked = [];

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

    /// <summary>Which kinds of clip the window is narrowed to. Reset to <see cref="PasteKindFilter.All"/> per session.</summary>
    public PasteKindFilter KindFilter { get; private set; } = PasteKindFilter.All;

    /// <summary>How many clips are marked to be pasted joined. Zero means an ordinary single-clip paste.</summary>
    public int MarkedCount => _marked.Count;

    /// <summary>Whether the clip under the cursor is marked, so the overlay can say so.</summary>
    public bool CurrentIsMarked => Current is { } current && _marked.Contains(current.Id);

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

        // Reset per session, deliberately, and not governed by PreserveClipPosition. A filter that survived would
        // mean opening the gesture on a stack with most of it missing, with only a small chip to explain why -
        // which reads as clips having been lost rather than as a setting still being in force.
        KindFilter = PasteKindFilter.All;

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

            case PasteAction.JumpToOldest:
                // Clamped rather than assumed: the window is empty when a search matches nothing, and -1
                // would then be handed to Current.
                _cursor = Math.Max(0, _window.Count - 1);
                Render();
                break;

            case PasteAction.DeleteCurrentClip:
                return DeleteCurrent();

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

            case PasteAction.CycleKindFilter:
                return CycleKindFilter();

            case PasteAction.TogglePin:
                return TogglePin();

            case PasteAction.ToggleJoinMark:
                return ToggleJoinMark();

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
                // Ends the session, like the tag and clip editors: the card is a real window that takes
                // focus, and leaving the overlay up meant the gesture went on swallowing every key the card
                // was busy explaining. No clip is needed, hence its own path rather than EndAndDelegate.
                return EndAndOpenWindow(static host => host.ShowShortcutHelp());

            case PasteAction.ShowHistory:
                // Same shape, and for a sharper version of the same reason: the history window has a search box,
                // so an overlay left up would eat the query as it was typed.
                return EndAndOpenWindow(static host => host.RequestHistoryWindow());

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
            // Marks win over the cursor, and that is the whole point of having marked: the clips the user picked
            // are what gets pasted, wherever they happen to have left the cursor. Checked before the ordinary
            // path, and independently of whether there is a Current at all - a search matching nothing must not
            // throw away a set of marks.
            case PasteCommitMode.Paste when _marked.Count > 0:
            {
                var marked = MarkedClips();

                if (marked.Count == 0)
                {
                    // Every marked clip was deleted mid-session. Nothing to paste and nothing was chosen at the
                    // cursor either, so this is the same case as an emptied window.
                    _host.PassThroughPaste();
                    EndSession();
                    return PasteCommitKind.PassedThrough;
                }

                var popMarked = ShiftHeld;

                _host.PasteJoined(marked, Formatter);

                if (popMarked)
                {
                    // Pop deletes what was pasted, which with marks means all of them. Consistent rather than
                    // cautious, and it is deliberate on the user's part twice over: they marked each clip, and
                    // they held Shift while letting go of Ctrl.
                    foreach (var clip in marked)
                    {
                        _catalog.Delete(clip.Id);
                    }
                }

                EndSession();
                return PasteCommitKind.Pasted;
            }

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

    /// <summary>
    /// Marks or unmarks this clip for a joined paste, and leaves the session open.
    /// <para>
    /// The cursor deliberately does not move. Marking is not stepping, and a key that advanced as well would make
    /// "mark this one and that one" require counting - the two useful sequences are mark-step-mark and
    /// mark-search-mark, both of which the user drives.
    /// </para>
    /// <para>
    /// Returns <see cref="PasteCommitKind.None"/>, so the session is not reported as finished: nothing has been
    /// pasted. Releasing Ctrl is still what commits.
    /// </para>
    /// </summary>
    private PasteCommitKind ToggleJoinMark()
    {
        if (Current is not { } current)
        {
            return PasteCommitKind.None;
        }

        // Remove-or-add rather than a set, so unmarking and marking again moves the clip to the END of the
        // order. That is the useful reading: the mark order is the paste order, so re-marking is how you correct
        // a sequence without starting again.
        if (!_marked.Remove(current.Id))
        {
            _marked.Add(current.Id);
        }

        Render();
        return PasteCommitKind.None;
    }

    /// <summary>
    /// The marked clips, in mark order, as they exist now.
    /// <para>
    /// Resolved against a fresh catalog snapshot rather than the session's window, for two reasons: the window may
    /// be narrowed by a search or a kind filter, and a marked clip may have been deleted mid-session by the Delete
    /// key. Both would otherwise contribute nothing while still being counted.
    /// </para>
    /// </summary>
    private List<Clip> MarkedClips()
    {
        var byId = _catalog.Snapshot().ToDictionary(static clip => clip.Id);

        return _marked
            .Where(byId.ContainsKey)
            .Select(id => byId[id])
            .ToList();
    }

    /// <summary>
    /// The Delete key: remove this clip now and keep browsing.
    /// <para>
    /// The cursor is deliberately left where it is rather than following the clip that was there, which is
    /// what a file list does - so a run of Delete presses walks forward through the stack instead of stopping.
    /// It is clamped afterwards because deleting the last clip in the window would otherwise leave the cursor
    /// past the end, and clamped to a possibly empty window, which <c>Current</c> already answers null for.
    /// </para>
    /// </summary>
    private PasteCommitKind DeleteCurrent()
    {
        var current = Current;

        if (current is null)
        {
            return PasteCommitKind.None;
        }

        _catalog.Delete(current.Id);

        // Unmarked as well, so the count on the overlay keeps matching what would be pasted. MarkedClips would
        // skip it anyway, but a chip reading JOIN 3 when only two clips remain is a lie about what is about to
        // happen.
        _marked.Remove(current.Id);

        RefreshWindow();

        _cursor = _window.Count == 0 ? 0 : Math.Clamp(_cursor, 0, _window.Count - 1);
        Render();

        // Not PasteCommitKind.Deleted: that reports a committed session, and this one is still open. The
        // caller must not treat it as the gesture having finished.
        return PasteCommitKind.None;
    }

    /// <summary>
    /// Steps the kind filter on and rebuilds the window.
    /// <para>
    /// The clip you were looking at is kept if it survives the new filter, which is what makes cycling feel like
    /// narrowing rather than jumping - and otherwise the cursor goes to the top of the narrowed stack, because the
    /// nearest surviving neighbour is not something the user could predict.
    /// </para>
    /// <para>
    /// A filter that matches nothing is a legal state and shows an empty overlay rather than being skipped. That
    /// keeps the cycle predictable - four taps always returns you to All - and the empty case is already handled
    /// everywhere, since a search matching nothing does the same thing.
    /// </para>
    /// </summary>
    private PasteCommitKind CycleKindFilter()
    {
        var landed = Current?.Id;

        KindFilter = KindFilter.Next();
        RefreshWindow();

        _cursor = 0;
        RestoreCursorTo(landed);
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

    /// <summary>
    /// Ends the session and then opens a window that needs no clip.
    /// <para>
    /// The clip-less sibling of <see cref="EndAndDelegate"/>, and the ordering is the point of both: the
    /// clipboard goes back and the overlay comes down <em>before</em> anything that takes the keyboard appears.
    /// Without that, the gesture carries on swallowing keys aimed at the new window - which is exactly how F1
    /// behaved when it did not end the session.
    /// </para>
    /// </summary>
    private PasteCommitKind EndAndOpenWindow(Action<IPasteModeHost> request)
    {
        _host.RestoreExistingClipboard();
        EndSession();
        request(_host);
        return PasteCommitKind.Cancelled;
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

        // Per session, deliberately, and NOT governed by PreserveClipPosition - the same rule PasteKindFilter
        // follows. A mark that survived would make the next ordinary Ctrl+V paste something assembled minutes
        // ago, which is the sort of surprise that reads as the wrong clip being pasted.
        _marked.Clear();

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

        // Kind first, then the query, so the two compose: filter to images and then search within them. Applied
        // in one place because this is the only place the window is built - which is what made the filter a small
        // change rather than a new mechanism.
        _window =
        [
            .. all
                .Where(c => KindFilter.Admits(c.Kind))
                .Where(c => string.IsNullOrWhiteSpace(SearchQuery) || Matches(c, SearchQuery))
        ];

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
            // The id, not just the position. Position is where the cursor sits in the FILTERED window, so it
            // cannot be used to reach the clip - see the note on PasteOverlayModel.ClipId.
            ClipId = current?.Id,
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
            KindFilter = KindFilter,
            MarkedCount = MarkedCount,
            CurrentIsMarked = CurrentIsMarked,
            TextFacts = DescribeTextFacts(current),
            TotalBytes = current?.TotalBytes ?? 0,
            IsEmpty = current is null,
            SourceExecutable = current?.SourceExecutable,
        });
    }

    /// <summary>
    /// Lines and characters for a text clip, or null for anything else.
    /// <para>
    /// Counted from the clip's <em>stored</em> preview, not from the elided string the overlay draws - the
    /// overlay's limit is a display choice and counting against it would report the width of the window rather
    /// than the size of the clip. Where the stored preview is itself at the cap, the numbers are marked with a
    /// <c>+</c>: what was copied is longer than anything we kept, and saying so is better than a confident wrong
    /// count.
    /// </para>
    /// <para>
    /// Only for text. An image's facts are its dimensions, which the overlay already shows, and a file copy's are
    /// its size - neither has a line count worth printing.
    /// </para>
    /// </summary>
    private string? DescribeTextFacts(Clip? clip)
    {
        if (clip is null || clip.Kind != ClipKind.Text)
        {
            return null;
        }

        return TextMetrics.Describe(clip.Preview, clip.Preview.Length >= _options.PreviewMaxChars);
    }

    private string BuildPreviewText(Clip? clip)
    {
        if (clip is null)
        {
            return string.Empty;
        }

        var limit = _options.OverlayPreviewChars;
        var preview = clip.Preview;

        return preview.Length <= limit
            ? preview
            : preview[..limit] + "…";
    }
}
