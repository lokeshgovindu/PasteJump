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
    DeletedAll,
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

    /// <summary><c>A</c> - jump to the newest clip.</summary>
    JumpToNewest,

    /// <summary><c>Q</c> - move the current clip to the front of the stack.</summary>
    PromoteToFront,

    /// <summary><c>F</c> - toggle the incremental search box.</summary>
    ToggleSearch,

    /// <summary><c>Z</c> - cycle paste formatters.</summary>
    CycleFormatter,

    /// <summary><c>Space</c> - pin / unpin.</summary>
    TogglePin,

    /// <summary><c>T</c> - edit tags.</summary>
    EditTags,

    /// <summary><c>S</c> - put the clip on the Windows clipboard without pasting.</summary>
    PushToClipboard,

    /// <summary><c>H</c> - open the clip in an editor.</summary>
    EditClip,

    /// <summary><c>E</c> - export the clip to a file.</summary>
    ExportClip,

    /// <summary><c>Enter</c> - paste but keep the session open.</summary>
    Multipaste,

    /// <summary><c>F1</c> - show the key list.</summary>
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

    public required bool IsEmpty { get; init; }

    public string? SourceExecutable { get; init; }
}
