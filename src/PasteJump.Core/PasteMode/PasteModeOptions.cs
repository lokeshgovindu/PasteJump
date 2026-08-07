namespace PasteJump.Core.PasteMode;

/// <summary>
/// Behavioural knobs for paste mode. Mirrors the handful of original settings that actually
/// changed how the gesture felt, rather than every INI key it had.
/// </summary>
public sealed class PasteModeOptions
{
    /// <summary>
    /// Reopen paste mode on the clip that was active last time, instead of the newest.
    /// Original: <c>ini_PreserveClipPos</c>.
    /// </summary>
    public bool PreserveClipPosition { get; init; } = true;

    /// <summary>
    /// Open straight into the search box. Original: <c>startSearch</c>.
    /// </summary>
    public bool OpenSearchImmediately { get; init; }

    /// <summary>
    /// Reset the formatter to the configured default on every entry, rather than remembering the
    /// last one used. Original: <c>revFormat2def</c>.
    /// </summary>
    public bool ResetFormatterOnEntry { get; init; }

    /// <summary>Formatter id applied on entry when <see cref="ResetFormatterOnEntry"/> is set.</summary>
    public string? DefaultFormatterId { get; init; }
}
