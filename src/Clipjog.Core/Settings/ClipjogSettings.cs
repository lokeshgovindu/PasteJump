using System.Text.Json.Serialization;
using Clipjog.Core.Formatting;
using Clipjog.Core.PasteMode;

namespace Clipjog.Core.Settings;

/// <summary>
/// User settings, persisted as JSON next to the executable.
/// <para>
/// Every property has a working default, so a missing or partially-corrupt file degrades to
/// sensible behaviour rather than failing startup. The original stored these across a UTF-16
/// <c>settings.ini</c> with sections named <c>[Main]</c>, <c>[Advanced]</c>, <c>[System]</c> and
/// keys like <c>Minimum_No_Of_Clips_to_be_Active</c> - the names are preserved only in the
/// importer, not here.
/// </para>
/// </summary>
public sealed class ClipjogSettings
{
    // ------------------------------------------------------------ capture

    /// <summary>Maximum clips kept in the working stack. Pinned clips are exempt.</summary>
    public int MaxClips { get; set; } = 200;

    /// <summary>Watch the clipboard at all. Turning this off makes the app inert but resident.</summary>
    public bool MonitorClipboard { get; set; } = true;

    /// <summary>
    /// Record an identical copy as a new clip rather than promoting the existing one.
    /// Original: <c>is_duplicate_copied</c>.
    /// </summary>
    public bool AllowDuplicateClips { get; set; }

    /// <summary>Store images from the clipboard. Off keeps the database small.</summary>
    public bool StoreImages { get; set; } = true;

    /// <summary>
    /// Skip capture entirely while one of these processes is in the foreground - password
    /// managers being the obvious case. Compared case-insensitively against the executable name.
    /// </summary>
    public List<string> IgnoredProcesses { get; set; } = [];

    // ------------------------------------------------------------ history

    /// <summary>Days of history to keep. Zero or less keeps everything.</summary>
    public int HistoryRetentionDays { get; set; } = 180;

    /// <summary>Write captured clips to the long-term history archive as well as the stack.</summary>
    public bool RecordHistory { get; set; } = true;

    // ------------------------------------------------------------ paste mode

    /// <summary>Reopen on the previously active clip. Original: <c>ini_PreserveClipPos</c>.</summary>
    public bool PreserveClipPosition { get; set; } = true;

    /// <summary>Open directly into search. Original: <c>startSearch</c>.</summary>
    public bool OpenSearchImmediately { get; set; }

    /// <summary>Revert to the default formatter on each entry. Original: <c>revFormat2def</c>.</summary>
    public bool ResetFormatterOnEntry { get; set; }

    /// <summary>
    /// Formatter id applied to every paste unless the user cycles it with <c>Z</c>.
    /// <para>
    /// Stored explicitly, defaulting to <see cref="FormatterRegistry.DefaultId"/>, rather than using
    /// null to mean "Original". Both spellings resolve identically, and that ambiguity was a real
    /// (if cosmetic) bug: the default was null while the settings dialog wrote <c>"original"</c>, so
    /// the Advanced page reported the setting as changed from its default when nothing had changed.
    /// A null or blank value is still accepted from a hand-edited file and normalised away.
    /// </para>
    /// </summary>
    public string? DefaultFormatterId { get; set; } = FormatterRegistry.DefaultId;

    /// <summary>Fixed overlay position. Null means "follow the caret, else the cursor".</summary>
    public int? OverlayX { get; set; }

    public int? OverlayY { get; set; }

    /// <summary>
    /// Gap in milliseconds between putting a clip on the clipboard and sending Ctrl+V.
    /// <para>
    /// Exposed because the applications most likely to need it differ in how long they take to drop
    /// their cached copy of the clipboard: Office, Electron shells and remote-desktop clients all
    /// cache, and a keystroke arriving too early can be served from the stale cache. Raise it if a
    /// particular application pastes the previous clip.
    /// </para>
    /// </summary>
    public int PasteSettleDelayMs { get; set; } = 25;

    // ------------------------------------------------------------ appearance

    /// <summary>Colour scheme. Light by default.</summary>
    public AppTheme Theme { get; set; } = AppTheme.Light;

    /// <summary>Row spacing in the history list. Cozy by default.</summary>
    public GridDensity GridDensity { get; set; } = GridDensity.Cozy;

    /// <summary>Show a brief notification near the cursor after each copy, as Clipjump did.</summary>
    public bool ShowCopyNotification { get; set; } = true;

    /// <summary>How long the copy notification stays on screen, in milliseconds.</summary>
    public int CopyNotificationMs { get; set; } = 1200;

    // ------------------------------------------------------------ system

    /// <summary>Start with Windows via a shortcut in the user's Startup folder.</summary>
    public bool RunAtLogon { get; set; }

    /// <summary>External editor used by the <c>H</c> key.</summary>
    public string TextEditor { get; set; } = "notepad.exe";

    /// <summary>Whether the legacy Clipjump import has already run, so it is not offered twice.</summary>
    public bool LegacyImportCompleted { get; set; }

    [JsonIgnore]
    public PasteModeOptions PasteModeOptions => new()
    {
        PreserveClipPosition = PreserveClipPosition,
        OpenSearchImmediately = OpenSearchImmediately,
        ResetFormatterOnEntry = ResetFormatterOnEntry,
        DefaultFormatterId = DefaultFormatterId,
    };

    /// <summary>Clamps anything a hand-edited JSON file could have made nonsensical.</summary>
    public void Normalise()
    {
        MaxClips = Math.Clamp(MaxClips, 1, 100_000);

        if (HistoryRetentionDays < 0)
        {
            HistoryRetentionDays = 0;
        }

        if (string.IsNullOrWhiteSpace(TextEditor))
        {
            TextEditor = "notepad.exe";
        }

        PasteSettleDelayMs = Math.Clamp(PasteSettleDelayMs, 0, 500);
        CopyNotificationMs = Math.Clamp(CopyNotificationMs, 250, 10_000);

        if (!Enum.IsDefined(Theme))
        {
            Theme = AppTheme.Light;
        }

        if (!Enum.IsDefined(GridDensity))
        {
            GridDensity = GridDensity.Cozy;
        }

        if (string.IsNullOrWhiteSpace(DefaultFormatterId))
        {
            DefaultFormatterId = FormatterRegistry.DefaultId;
        }

        // An id that is merely *unknown* is deliberately left alone rather than reset. It may belong to
        // a formatter registered later, and FormatterRegistry.Resolve already falls back safely - so
        // rewriting it here would silently discard a valid preference.

        IgnoredProcesses = IgnoredProcesses
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(static p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool IsProcessIgnored(string? processName)
    {
        if (string.IsNullOrEmpty(processName) || IgnoredProcesses.Count == 0)
        {
            return false;
        }

        return IgnoredProcesses.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase));
    }
}
