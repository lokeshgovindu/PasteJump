namespace Clipjog.Core.PasteMode;

/// <summary>
/// Platform-neutral names for the keys the gesture cares about. Interop maps Windows virtual-key
/// codes onto these, which keeps the recogniser itself testable without Win32.
/// </summary>
public enum GestureKey
{
    None = 0,

    /// <summary>The modifier that holds a session open.</summary>
    Control,

    /// <summary>Tracked only so paste-popping knows whether Shift is down.</summary>
    Shift,

    /// <summary>Entry key, and the "step to older clip" key once a session is open.</summary>
    Paste,

    Back,
    CycleCommitMode,
    JumpToNewest,
    PromoteToFront,
    ToggleSearch,
    CycleFormatter,
    TogglePin,
    EditTags,
    PushToClipboard,
    EditClip,
    ExportClip,
    Commit,
    Help,
    ToggleJumpDirection,
    Escape,

    /// <summary>Used by the search box.</summary>
    Backspace,

    Digit1,
    Digit2,
    Digit3,
    Digit4,
    Digit5,
    Digit6,
    Digit7,
    Digit8,
    Digit9,
}

internal static class GestureKeyExtensions
{
    /// <summary>Digit value 1-9, or 0 when the key is not a digit.</summary>
    public static int DigitValue(this GestureKey key) => key switch
    {
        GestureKey.Digit1 => 1,
        GestureKey.Digit2 => 2,
        GestureKey.Digit3 => 3,
        GestureKey.Digit4 => 4,
        GestureKey.Digit5 => 5,
        GestureKey.Digit6 => 6,
        GestureKey.Digit7 => 7,
        GestureKey.Digit8 => 8,
        GestureKey.Digit9 => 9,
        _ => 0,
    };

    public static PasteAction? ToAction(this GestureKey key) => key switch
    {
        GestureKey.Paste => PasteAction.Advance,
        GestureKey.Back => PasteAction.Back,
        GestureKey.CycleCommitMode => PasteAction.CycleCommitMode,
        GestureKey.JumpToNewest => PasteAction.JumpToNewest,
        GestureKey.PromoteToFront => PasteAction.PromoteToFront,
        GestureKey.ToggleSearch => PasteAction.ToggleSearch,
        GestureKey.CycleFormatter => PasteAction.CycleFormatter,
        GestureKey.TogglePin => PasteAction.TogglePin,
        GestureKey.EditTags => PasteAction.EditTags,
        GestureKey.PushToClipboard => PasteAction.PushToClipboard,
        GestureKey.EditClip => PasteAction.EditClip,
        GestureKey.ExportClip => PasteAction.ExportClip,
        GestureKey.Commit => PasteAction.Multipaste,
        GestureKey.Help => PasteAction.Help,
        GestureKey.ToggleJumpDirection => PasteAction.ToggleJumpDirection,
        GestureKey.Escape => PasteAction.Escape,
        _ => null,
    };
}
