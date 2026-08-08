using System.Text.Json.Serialization;
using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;

namespace PasteJump.Core.Settings;

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
public sealed class PasteJumpSettings
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

    /// <summary>
    /// Letter that, held with Ctrl, opens paste mode. Original: <c>paste_k</c>.
    /// <para>
    /// See <see cref="PasteMode.TriggerKey"/>. Only letters not already bound to a paste-mode action are
    /// accepted; anything else is coerced back to <c>V</c> by <see cref="Normalise"/>.
    /// </para>
    /// </summary>
    public string PasteModeTriggerKey { get; set; } = PasteMode.TriggerKey.Default.ToString();

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

    /// <summary>
    /// Chord sent to make the target application paste. See <see cref="Settings.PasteKeystroke"/> - the
    /// short version is that another clipboard manager's keyboard hook can swallow Ctrl+V before the
    /// target window sees it, and Shift+Insert is the way out.
    /// </summary>
    public PasteKeystroke PasteKeystroke { get; set; } = PasteKeystroke.CtrlV;

    /// <summary>
    /// Offer to switch to Shift+Insert at start-up when another clipboard manager is detected. On by
    /// default: without the prompt the symptom is pasting that silently does nothing, which looks like a
    /// PasteJump bug rather than a conflict.
    /// </summary>
    public bool WarnAboutClipboardManagerConflict { get; set; } = true;

    // ------------------------------------------------------------ appearance

    /// <summary>Colour scheme. Light by default.</summary>
    public AppTheme Theme { get; set; } = AppTheme.Light;

    /// <summary>Row spacing in the history list. Cozy by default.</summary>
    public GridDensity GridDensity { get; set; } = GridDensity.Cozy;

    /// <summary>Show a brief notification near the cursor after each copy, as Clipjump did.</summary>
    public bool ShowCopyNotification { get; set; } = true;

    /// <summary>How long the copy notification stays on screen, in milliseconds.</summary>
    public int CopyNotificationMs { get; set; } = 1200;

    /// <summary>
    /// Sound a short tone on each capture. Original: <c>CopyBeep</c>, off by default there too.
    /// <para>
    /// Useful when the notification is off or the copy happened on a monitor you were not looking at -
    /// which is the case it exists for, rather than as decoration.
    /// </para>
    /// </summary>
    public bool BeepOnCopy { get; set; }

    /// <summary>Pitch of that tone in hertz. Original: <c>beepFrequency</c>, also 1500.</summary>
    public int BeepFrequencyHz { get; set; } = 1500;

    // ------------------------------------------------------------ system

    /// <summary>Start with Windows via a shortcut in the user's Startup folder.</summary>
    public bool RunAtLogon { get; set; }

    /// <summary>External editor used by the <c>H</c> key on a text clip.</summary>
    public string TextEditor { get; set; } = "notepad.exe";

    /// <summary>
    /// External editor used by the <c>H</c> key on an image clip. Original: <c>default_image_editor</c>.
    /// <para>
    /// A second setting rather than reusing <see cref="TextEditor"/>: Notepad opening a bitmap is useless,
    /// and before this existed the <c>H</c> key simply refused on image clips.
    /// </para>
    /// </summary>
    public string ImageEditor { get; set; } = "mspaint.exe";

    /// <summary>
    /// System-wide hotkey that opens the clipboard history window, e.g. <c>Ctrl+Shift+H</c>. Empty means
    /// none. Original: <c>history_k</c>, also empty by default.
    /// <para>
    /// Empty by default deliberately. A global hotkey takes that chord away from every other application
    /// on the desktop, which is not a thing to do to someone without being asked.
    /// </para>
    /// </summary>
    public string HistoryHotkey { get; set; } = string.Empty;

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

        if (string.IsNullOrWhiteSpace(ImageEditor))
        {
            ImageEditor = "mspaint.exe";
        }

        // Coerced to a usable letter rather than rejected, since a hand-edited file could hold anything.
        PasteModeTriggerKey = PasteMode.TriggerKey.Normalise(PasteModeTriggerKey).ToString();

        // Re-rendered through the parser, so a hand-typed "control + shift + h" becomes the canonical
        // "Ctrl+Shift+H" and anything unparseable becomes empty rather than sitting there looking valid.
        HistoryHotkey = HotkeySpec.ParseOrNone(HistoryHotkey).ToString();

        BeepFrequencyHz = Math.Clamp(BeepFrequencyHz, 37, 32_767);

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

        if (!Enum.IsDefined(PasteKeystroke))
        {
            PasteKeystroke = PasteKeystroke.CtrlV;
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
