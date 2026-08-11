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

    /// <summary>
    /// Whether <see cref="MaxClips"/> is enforced at all. Original: <c>limit_MaxClips</c>.
    /// <para>
    /// On by default, and worth leaving on. With the limit off the stack grows without bound, and clips are not
    /// uniformly small - the clipboard hands out images uncompressed, so a handful of screenshots costs more
    /// than thousands of text clips. Nothing else caps this store: <see cref="HistoryRetentionDays"/> prunes
    /// the history archive, which is a separate thing.
    /// </para>
    /// </summary>
    public bool LimitMaxClips { get; set; } = true;

    /// <summary>
    /// The cap to actually apply: <see cref="MaxClips"/> when limiting, otherwise 0.
    /// <para>
    /// Zero rather than <see cref="int.MaxValue"/>, because <c>ClipStore.EvictBeyond</c> already treats a
    /// non-positive cap as "do nothing" - so the unlimited case skips the query entirely instead of running an
    /// eviction that can never match. Every caller goes through here so the rule lives in one place; a call
    /// site reading <c>MaxClips</c> directly would silently ignore the setting.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public int EffectiveMaxClips => LimitMaxClips ? MaxClips : 0;

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

    /// <summary>
    /// Characters of text kept in a clip's or history entry's <c>preview</c> column.
    /// <para>
    /// Not merely cosmetic, which is why it is exposed: <c>history_fts</c> indexes the preview, so this is also
    /// how far into a long clip search can reach. Raising it makes long clips searchable at the cost of a larger
    /// database; text beyond it is still archived whole as a blob, so nothing is lost either way. Changing it
    /// affects captures from here on - rows already written keep the length they were written at.
    /// </para>
    /// </summary>
    public int PreviewMaxChars { get; set; } = 4096;

    /// <summary>
    /// Most rows the history window will load at once.
    /// <para>
    /// A backstop against an enormous store making the window unresponsive, not a page size. It was once 500,
    /// which was low enough to read as a bug: an imported Clipjump history of 11,000 entries showed only the
    /// newest 500 and looked like an import that had failed.
    /// </para>
    /// </summary>
    public int HistoryLoadLimit { get; set; } = 50_000;

    /// <summary>
    /// Widest an image is decoded for the history window's preview pane, in pixels. Larger is sharper on a
    /// high-resolution screen and costs more per row selected; the image is never enlarged past its own size.
    /// </summary>
    public int HistoryPreviewMaxWidth { get; set; } = 640;

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
    /// <summary>
    /// What a left click on the tray icon does. Right click always opens the menu - see
    /// <see cref="TrayClickAction"/> for why that one is not negotiable.
    /// </summary>
    public TrayClickAction TrayLeftClick { get; set; } = TrayClickAction.History;

    public string PasteModeTriggerKey { get; set; } = PasteMode.TriggerKey.Default.ToString();

    /// <summary>
    /// Which letter fires which paste-mode action, as <c>name=letter</c> pairs: <c>back=C;newest=A;pin=P</c>. An
    /// empty letter switches that action off.
    /// <para>
    /// A string rather than a dictionary for two reasons that both bite: the Advanced tab decides whether a row
    /// differs from its default by comparing values, which a dictionary does not do usefully, and the same tab
    /// has to render it as one readable line. See <see cref="PasteMode.PasteKeyMap"/> for the format and for why
    /// only letters are configurable.
    /// </para>
    /// </summary>
    public string PasteModeKeys { get; set; } = PasteMode.PasteKeyMap.Default.ToSettingsString();

    /// <summary>
    /// Fixed overlay position in physical pixels. Null means "follow the caret, else the cursor".
    /// <para>
    /// Both halves must be set for either to apply: half a position would move the overlay in one axis and
    /// track the caret in the other, which reads as a bug rather than as a setting. <see cref="Normalise"/>
    /// enforces that, so a hand-edited file cannot express it.
    /// </para>
    /// </summary>
    public int? OverlayX { get; set; }

    /// <inheritdoc cref="OverlayX"/>
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

    /// <summary>
    /// Largest size, in device-independent pixels, at which the paste overlay draws an image preview.
    /// <para>
    /// A <em>maximum</em>, not a size: the overlay never enlarges a picture, so anything smaller than this is
    /// drawn at its own dimensions. Raising it makes a screenshot legible during the gesture at the cost of an
    /// overlay that covers more of what you are pasting into, which is why it is a preference rather than a
    /// constant.
    /// </para>
    /// </summary>
    public int OverlayPreviewMaxWidth { get; set; } = 600;

    /// <inheritdoc cref="OverlayPreviewMaxWidth"/>
    public int OverlayPreviewMaxHeight { get; set; } = 400;

    /// <summary>
    /// Characters of a text clip shown in the paste overlay before it is elided.
    /// <para>
    /// Separate from <see cref="PreviewMaxChars"/>, which is about what gets <em>stored</em>. This one is about
    /// how much of it is worth reading during a gesture that is over in a second - so it is deliberately far
    /// smaller, and raising it trades a taller overlay for more context.
    /// </para>
    /// </summary>
    public int OverlayPreviewChars { get; set; } = 400;

    /// <summary>
    /// Show a one-line key reminder along the bottom of the paste overlay.
    /// <para>
    /// On by default, and it earns that: two features were reported as missing while both already worked -
    /// jumping back to the newest clip with <c>A</c>, and reading the whole key list with <c>F1</c>. A gesture
    /// with no menu and no window has nowhere else to advertise itself, so it has to carry its own signposts.
    /// </para>
    /// </summary>
    public bool ShowOverlayKeyHint { get; set; } = true;

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

    /// <summary>
    /// Length of that tone in milliseconds. Matches the original's <c>BeepAt</c> default.
    /// <para>
    /// Kept short on purpose. The tone is synchronous inside <c>Console.Beep</c>, so it is played on the thread
    /// pool - but it is still a sound on every single copy, and anything much longer than a click becomes the
    /// thing you notice rather than a confirmation.
    /// </para>
    /// </summary>
    public int BeepDurationMs { get; set; } = 150;

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
        OverlayPreviewChars = OverlayPreviewChars,
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

        // Re-rendered through the parser, which drops unknown names and restores anything unparseable to its
        // default - so a hand-edited file cannot leave an action bound to nothing without saying so.
        PasteModeKeys = PasteMode.PasteKeyMap.Parse(PasteModeKeys).ToSettingsString();

        // Re-rendered through the parser, so a hand-typed "control + shift + h" becomes the canonical
        // "Ctrl+Shift+H" and anything unparseable becomes empty rather than sitting there looking valid.
        HistoryHotkey = HotkeySpec.ParseOrNone(HistoryHotkey).ToString();

        BeepFrequencyHz = Math.Clamp(BeepFrequencyHz, 37, 32_767);
        BeepDurationMs = Math.Clamp(BeepDurationMs, 20, 2_000);

        // Floor is high enough that search still has something to index; the ceiling is where a preview column
        // stops being a preview. Text past it is archived whole regardless, so neither bound loses data.
        PreviewMaxChars = Math.Clamp(PreviewMaxChars, 256, 65_536);

        // Either both or neither. See OverlayX for why half a fixed position is not a state worth having.
        if (OverlayX is null || OverlayY is null)
        {
            OverlayX = null;
            OverlayY = null;
        }

        HistoryLoadLimit = Math.Clamp(HistoryLoadLimit, 100, 1_000_000);
        HistoryPreviewMaxWidth = Math.Clamp(HistoryPreviewMaxWidth, 120, 4_096);
        OverlayPreviewChars = Math.Clamp(OverlayPreviewChars, 40, 4_000);

        PasteSettleDelayMs = Math.Clamp(PasteSettleDelayMs, 0, 500);
        CopyNotificationMs = Math.Clamp(CopyNotificationMs, 250, 10_000);

        // Floors are low enough to be useful for someone who wants the overlay out of the way, and the ceilings
        // are what a 1080p screen can show without the overlay becoming the thing being looked at rather than
        // the document underneath it.
        OverlayPreviewMaxWidth = Math.Clamp(OverlayPreviewMaxWidth, 120, 1400);
        OverlayPreviewMaxHeight = Math.Clamp(OverlayPreviewMaxHeight, 80, 900);

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
