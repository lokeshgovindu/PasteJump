using PasteJump.Core.Formatting;
using PasteJump.Core.Model;

namespace PasteJump.Core.PasteMode;

/// <summary>Where a paste-mode session currently is.</summary>
public enum PasteSessionState
{
    Idle,

    /// <summary>Overlay up, modifier held, cursor walking the stack.</summary>
    Browsing,

    /// <summary>
    /// Incremental search box up. Critically, the modifier is no longer what keeps the session
    /// alive - see <see cref="PasteModeController.ModifierReleased"/>.
    /// </summary>
    Searching,
}

/// <summary>
/// What releasing the modifier will do. The <c>X</c> key cycles this.
/// <para>
/// Note there is no route back to <see cref="Paste"/> once <c>X</c> has been pressed - cycling
/// runs Cancel to Delete to DeleteAll and around again. That is deliberate parity with the
/// original: having a destructive cycle silently loop back through "paste" would make an
/// over-eager keypress paste something the user was trying to delete.
/// </para>
/// </summary>
public enum PasteCommitMode
{
    Paste,
    Cancel,
    Delete,
    DeleteAll,
}

/// <summary>The outcome of a committed session, reported back to the caller.</summary>
public enum PasteCommitKind
{
    None,
    Pasted,
    Cancelled,
    Deleted,

    /// <summary>
    /// <c>DeleteAll</c> was committed and the host has been <em>asked</em> to confirm it. Nothing has been
    /// deleted yet.
    /// <para>
    /// Deliberately not "DeletedAll". The confirmation cannot be answered inside
    /// <see cref="PasteModeController.ModifierReleased"/>, which runs in the keyboard hook, so the deletion
    /// happens later - or not at all. A caller treating this as "done" would report a deletion that the user
    /// may still refuse.
    /// </para>
    /// </summary>
    DeleteAllRequested,

    PushedToClipboard,

    /// <summary>
    /// Nothing to paste, so the keystroke was handed straight through as a native Ctrl+V.
    /// <para>
    /// This case is not cosmetic. Our keyboard hook swallows Ctrl+V to build the gesture, so
    /// an empty store without this path would make Ctrl+V silently stop working across the
    /// whole machine - the single worst failure this app could have.
    /// </para>
    /// </summary>
    PassedThrough,
}

/// <summary>Keys that act inside paste mode.</summary>
public enum PasteAction
{
    /// <summary><c>V</c> - step towards older clips.</summary>
    Advance,

    /// <summary><c>C</c> - step back towards newer clips.</summary>
    Back,

    /// <summary><c>X</c> - cycle Cancel / Delete / DeleteAll.</summary>
    CycleCommitMode,

    /// <summary>
    /// <c>Delete</c> - remove the current clip immediately and carry on browsing.
    /// <para>
    /// Distinct from <see cref="CycleCommitMode"/>, which only <em>arms</em> a deletion for the moment Ctrl
    /// is released. This acts at once and leaves the session open, which is what the key means everywhere
    /// else in Windows. It deliberately does not touch <see cref="PasteCommitMode"/>: a Delete key that
    /// silently rearmed what releasing Ctrl would do could delete a second clip the user never chose.
    /// </para>
    /// </summary>
    DeleteCurrentClip,

    /// <summary><c>A</c> or <c>Home</c> - jump to the newest clip.</summary>
    JumpToNewest,

    /// <summary>
    /// <c>End</c> - jump to the oldest clip in the window.
    /// <para>
    /// No Clipjump equivalent. It exists because Home now means "newest", and a Home with no End is a
    /// half-finished idea: the far end of the stack was otherwise reachable only by holding the trigger.
    /// </para>
    /// </summary>
    JumpToOldest,

    /// <summary><c>Q</c> - move the current clip to the front of the stack.</summary>
    PromoteToFront,

    /// <summary><c>F</c> - toggle the incremental search box.</summary>
    ToggleSearch,

    /// <summary><c>Z</c> - cycle paste formatters.</summary>
    CycleFormatter,

    /// <summary>
    /// <c>K</c> - narrow the stack to one kind of clip: all, text, images, files.
    /// <para>
    /// K for Kind. Wraps back to "all", unlike the <c>X</c> commit cycle, because nothing here is destructive and
    /// getting back to seeing everything must not cost three more taps.
    /// </para>
    /// </summary>
    CycleKindFilter,

    /// <summary><c>Space</c> - pin / unpin.</summary>
    TogglePin,

    /// <summary>
    /// <c>J</c> - mark or unmark this clip, so releasing Ctrl pastes every marked clip joined into one.
    /// <para>
    /// The other half of joining, the first being selecting rows in the history window. Distinct from
    /// <see cref="Multipaste"/>, which pastes clips one after another as separate pastes - that leaves the target
    /// application to decide what happens between them, and in a spreadsheet it means separate cells.
    /// </para>
    /// <para>
    /// Marks are per session, like <see cref="PasteKindFilter"/> and for the same reason: a mark surviving into
    /// the next gesture would make an ordinary Ctrl+V paste something the user assembled minutes ago.
    /// </para>
    /// </summary>
    ToggleJoinMark,

    /// <summary><c>T</c> - edit tags.</summary>
    EditTags,

    /// <summary><c>S</c> - put the clip on the Windows clipboard without pasting.</summary>
    PushToClipboard,

    /// <summary><c>O</c> - open the clip in an editor.</summary>
    EditClip,

    /// <summary>
    /// <c>H</c> - open the clipboard history window.
    /// <para>
    /// <c>H</c> used to open the clip in an editor, which is the binding Clipjump had and the one that read as
    /// "help" to everybody. It moved to <c>O</c> first and then gave the letter up entirely, because H for
    /// History is the mnemonic that made the original confusing. Nothing was lost in the move: the editor still
    /// answers to <c>O</c>.
    /// </para>
    /// <para>
    /// Ends the session, like <see cref="Help"/> and for the same reason - the history window takes the
    /// keyboard, and an overlay left up would go on swallowing the keys the user then tries to search with.
    /// </para>
    /// </summary>
    ShowHistory,

    /// <summary><c>E</c> - export the clip to a file.</summary>
    ExportClip,

    /// <summary><c>Enter</c> - paste but keep the session open.</summary>
    Multipaste,

    /// <summary>
    /// <c>F1</c> - show the key list.
    /// <para>
    /// Ends the session first, like the other actions that open a window. It did not, and that was reported:
    /// the card appeared over a live overlay, so the keys it was describing were still being swallowed by the
    /// gesture rather than reaching the window the user was now reading.
    /// </para>
    /// </summary>
    Help,

    /// <summary><c>-</c> - flip the direction that digit jumps move in.</summary>
    ToggleJumpDirection,

    /// <summary><c>Esc</c> - hard cancel.</summary>
    Escape,
}

/// <summary>The clip collection the controller navigates. Implemented over <c>ClipStore</c>; faked in tests.</summary>
public interface IClipCatalog
{
    /// <summary>Clips in display order: pinned first, then newest first.</summary>
    IReadOnlyList<Clip> Snapshot();

    void Delete(long id);

    /// <summary>Clears unpinned clips. Pinned ones survive, which is the point of pinning.</summary>
    void DeleteAllUnpinned();

    void SetPinned(long id, bool pinned);

    void MoveToFront(long id);
}

/// <summary>
/// Side effects the controller asks for but never performs itself. Keeping these behind an
/// interface is what lets the state machine be tested without a clipboard, a window or a
/// message loop.
/// </summary>
public interface IPasteModeHost
{
    /// <summary>Capture whatever is on the clipboard now, so a cancel can put it back.</summary>
    void SnapshotExistingClipboard();

    /// <summary>Restore the clipboard captured by <see cref="SnapshotExistingClipboard"/>.</summary>
    void RestoreExistingClipboard();

    /// <summary>Write the clip to the clipboard and synthesise Ctrl+V.</summary>
    void PasteClip(Clip clip, IClipFormatter formatter);

    /// <summary>
    /// Write the text of several clips, joined, to the clipboard and synthesise Ctrl+V.
    /// <para>
    /// The clips arrive in the order they were marked, which the controller records - unlike the history window,
    /// where the order rows were clicked is not something a <c>DataGrid</c> reports and display order is used
    /// instead. During the gesture the sequence is knowable and deliberate, so it is honoured.
    /// </para>
    /// <para>
    /// Joining itself is the host's job, not the controller's: it needs each clip's payload text, which means the
    /// store, and the separator, which is a setting. The controller knows only which clips were chosen.
    /// </para>
    /// </summary>
    void PasteJoined(IReadOnlyList<Clip> clips, IClipFormatter formatter);

    /// <summary>Synthesise Ctrl+V without touching the clipboard.</summary>
    void PassThroughPaste();

    /// <summary>Write the clip to the clipboard without pasting.</summary>
    void PushToClipboard(Clip clip, IClipFormatter formatter);

    void ShowOverlay(PasteOverlayModel model);

    void HideOverlay();

    void RequestTagEditor(Clip clip);

    void RequestClipEditor(Clip clip);

    void RequestExport(Clip clip);

    void ShowShortcutHelp();

    /// <summary>
    /// Open the clipboard history window. Like <see cref="ShowShortcutHelp"/>, the implementation must defer -
    /// this is reached from the keyboard hook, where showing a window inline runs a nested message loop and
    /// blocks every keystroke on the machine.
    /// </summary>
    void RequestHistoryWindow();

    /// <summary>
    /// Ask the user to confirm clearing the stack, and invoke <paramref name="confirmed"/> only if they agree.
    /// <para>
    /// A request rather than a question with a return value, because the only caller runs inside the keyboard
    /// hook. Anything modal there owns the UI thread with its own message loop, which blocks all keyboard input
    /// machine-wide and blows <c>LowLevelHooksTimeout</c> - so the implementation must defer the prompt and
    /// return immediately.
    /// </para>
    /// <para>
    /// <paramref name="confirmed"/> carries the deletion itself so the rule about which clips go - unpinned
    /// only - stays in <see cref="IClipCatalog"/> rather than being restated by whoever draws the dialog.
    /// </para>
    /// </summary>
    /// <param name="unpinnedCount">How many clips would be removed, for the prompt.</param>
    void RequestDeleteAllConfirmation(int unpinnedCount, Action confirmed);

    void ShowTransientMessage(string message);
}

/// <summary>Everything the overlay needs to render one frame. Immutable by design.</summary>
public sealed record PasteOverlayModel
{
    public required int Position { get; init; }

    public required int Total { get; init; }

    public required string PreviewText { get; init; }

    public required ClipKind Kind { get; init; }

    public required bool Pinned { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public required string FormatterName { get; init; }

    public required PasteCommitMode CommitMode { get; init; }

    public required bool IsSearching { get; init; }

    public string SearchQuery { get; init; } = string.Empty;

    public required int MatchCount { get; init; }

    /// <summary>Shift is held, so the clip will be removed after pasting ("paste popping").</summary>
    public required bool PopOnPaste { get; init; }

    /// <summary>
    /// Which kinds of clip the stack is narrowed to.
    /// <para>
    /// The overlay must show anything other than <see cref="PasteKindFilter.All"/>. A filter with no visible sign
    /// of itself is a stack that has silently lost most of its clips.
    /// </para>
    /// </summary>
    public PasteKindFilter KindFilter { get; init; } = PasteKindFilter.All;

    /// <summary>
    /// How many clips are marked to be pasted joined. Zero for an ordinary session.
    /// <para>
    /// The overlay must show a non-zero count, for the same reason it must show a kind filter: marks change what
    /// releasing Ctrl does, and a session that pastes three clips when the preview shows one would read as the
    /// wrong clip being pasted.
    /// </para>
    /// </summary>
    public int MarkedCount { get; init; }

    /// <summary>
    /// Whether the clip on show is one of the marked ones. Distinct from <see cref="MarkedCount"/> being non-zero:
    /// the user needs to know whether pressing the key again would add this clip or remove it.
    /// </summary>
    public bool CurrentIsMarked { get; init; }

    /// <summary>
    /// Lines and characters for a text clip, or null for anything else. Pre-rendered rather than left to the
    /// overlay: the counts depend on how much of the clip was actually stored, which only the controller knows.
    /// </summary>
    public string? TextFacts { get; init; }

    /// <summary>Bytes the clip occupies, as history reports it. Zero when not applicable.</summary>
    public long TotalBytes { get; init; }

    public required bool IsEmpty { get; init; }

    public string? SourceExecutable { get; init; }
}
