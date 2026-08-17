using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PasteJump.App.Services;
using PasteJump.Core;
using PasteJump.Core.Formatting;
using PasteJump.Core.Paste;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;
using PasteJump.Core.Theming;

namespace PasteJump.App.Views;

/// <summary>
/// Settings editor. Reads a copy of the settings and raises <see cref="SettingsApplied"/> with a
/// fully-populated object, so a cancelled dialog cannot leave partial changes behind.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// The three built-in theme choices and how they are worded. A label-to-name table rather than showing the
    /// stored value directly, so "System" can say what it actually means.
    /// <para>
    /// No longer the whole list: themes from <see cref="ThemeCatalog"/> are appended at load, under their own names.
    /// A user theme is offered by name with no relabelling, which is why the parser refuses these three as names -
    /// a file called "Dark" would appear twice and be unselectable.
    /// </para>
    /// </summary>
    private static readonly (string Name, string Label)[] BuiltInThemeChoices =
    [
        (ThemeNames.Light, "Light"),
        (ThemeNames.Dark, "Dark"),
        (ThemeNames.System, "Same as Windows"),
    ];

    /// <summary>
    /// Theme names in the order the combo lists them, filled at load. Parallel to the combo's items, so the
    /// selected index maps back to a name without parsing the label - which would break the moment a user named a
    /// theme "Light " with a trailing space.
    /// </summary>
    private readonly List<string> _themeNames = [];

    /// <summary>
    /// Paste-chord options. Labelled as the user would name the keys, with the reason for the second one
    /// carried in the combo's tooltip rather than the label.
    /// </summary>
    private static readonly (PasteKeystroke Keystroke, string Label)[] PasteKeystrokeChoices =
    [
        (PasteKeystroke.CtrlV, "Ctrl+V"),
        (PasteKeystroke.ShiftInsert, "Shift+Insert"),
    ];

    /// <summary>
    /// What a left click on the tray icon does. Labelled by what happens rather than by the enum name, and
    /// "Nothing" is offered because someone who keeps catching the icon by accident has no other remedy.
    /// </summary>
    private static readonly (TrayClickAction Action, string Label)[] TrayLeftClickChoices =
    [
        (TrayClickAction.History, "Open the clipboard history"),
        (TrayClickAction.Menu, "Open the menu"),
        (TrayClickAction.Settings, "Open settings"),
        (TrayClickAction.Nothing, "Do nothing"),
    ];

    /// <summary>Density options, labelled as Outlook and Explorer label them.</summary>
    private static readonly (GridDensity Density, string Label)[] DensityChoices =
    [
        (GridDensity.Roomy, "Roomy"),
        (GridDensity.Cozy, "Cozy"),
        (GridDensity.Compact, "Compact"),
    ];

    /// <summary>
    /// Data-location options. Labelled by intent rather than by path, with the resolved path shown
    /// beneath the combo - the paths are long and one of them differs per user.
    /// </summary>
    private static readonly (DataLocation Location, string Label)[] DataLocationChoices =
    [
        (DataLocation.ApplicationFolder, "The PasteJump folder"),
        (DataLocation.UserProfile, "My user profile"),
        (DataLocation.CustomFolder, "A folder I choose…"),
    ];

    private readonly FormatterRegistry _formatters;

    /// <summary>
    /// What is currently in force. Advanced compares against this to mark changed settings, and the
    /// location combos compare against it to decide whether a restart is still pending.
    /// <para>
    /// Not readonly, because Apply moves the baseline. Once a change has been applied it <em>is</em> in
    /// force, so continuing to compare against the values the dialog opened with would keep flagging
    /// settings that no longer differ and keep announcing a restart that has already been requested.
    /// </para>
    /// </summary>
    private PasteJumpSettings _baseline;

    private DataLocation _baselineClipsLocation;
    private DataLocation _baselineSettingsLocation;

    private string? _baselineClipsPath;
    private string? _baselineSettingsPath;

    private string _baselineClipsRoot;
    private string _baselineSettingsRoot;

    /// <summary>
    /// The excluded-application list as the user is editing it.
    /// <para>
    /// An <see cref="ObservableCollection{T}"/> bound to the ListBox, so adding and removing shows up without
    /// rebuilding <c>ItemsSource</c> - which would lose the selection every time and make Remove feel broken
    /// on a multiple selection.
    /// </para>
    /// </summary>
    private readonly ObservableCollection<string> _excluded = [];

    /// <summary>
    /// The themes on offer beyond the three built-in names, or null when there is no catalogue - which is the case
    /// in the UI smoke harness, where there is no data folder to read theme files from.
    /// </summary>
    private readonly ThemeCatalog? _themes;

    /// <param name="clipsPath">The custom folder in force, when <paramref name="clipsLocation"/> is one.</param>
    /// <param name="settingsPath">As <paramref name="clipsPath"/>, for the settings half.</param>
    /// <param name="themes">
    /// Refreshed before the list is built, so a theme file added while the application was running appears without
    /// a restart. Opening this dialog is the moment someone who has just written one will look.
    /// </param>
    public SettingsWindow(
        PasteJumpSettings settings,
        FormatterRegistry formatters,
        DataLocation clipsLocation = DataLocation.ApplicationFolder,
        DataLocation settingsLocation = DataLocation.ApplicationFolder,
        string? clipsPath = null,
        string? settingsPath = null,
        ThemeCatalog? themes = null)
    {
        _baseline = settings;
        _formatters = formatters;
        _themes = themes;
        _themes?.Refresh();
        _baselineClipsLocation = clipsLocation;
        _baselineSettingsLocation = settingsLocation;
        _baselineClipsPath = clipsPath;
        _baselineSettingsPath = settingsPath;

        // Kept as resolved roots as well as as choices, because "has anything moved" can only be answered by
        // comparing the roots - two different custom folders are the same choice.
        _baselineClipsRoot = AppPaths.RootFor(clipsLocation, clipsPath);
        _baselineSettingsRoot = AppPaths.RootFor(settingsLocation, settingsPath);

        InitializeComponent();
        Load();
        RefreshAdvanced();

        // Wired after Load, so populating the controls does not itself count as a change.
        //
        // Three class handlers on the window rather than an event per control: these are routed events, so
        // they reach here from any depth, and every editable control in this dialog is a TextBox, a
        // ToggleButton (check box or radio) or a Selector (combo or list). Subscribing individually would
        // mean ~30 subscriptions and a new setting silently not marking the dialog dirty - the same failure
        // mode the Advanced tab avoids by using reflection instead of a hand-written list.
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnAnyEdit));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnAnyEdit));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnAnyEdit));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnAnyEdit));

        // The excluded-apps list is mutated in code, not by the user typing into a control, so no routed
        // event fires for it.
        _excluded.CollectionChanged += (_, _) => RefreshApplyState();

        // Live theme preview. Subscribed here rather than in XAML for the same reason as the handlers above: this
        // runs after Load, so filling the combo does not itself fire a preview and repaint the application before
        // the dialog is even on screen.
        //
        // A theme is the one setting whose effect cannot be judged from the dialog that sets it - every other
        // control here describes something you go and look at afterwards, while this one changes the thing you are
        // looking at. Applying on Apply only meant choosing blind.
        ThemeCombo.SelectionChanged += OnThemeSelectionChanged;

        RefreshApplyState();

        // Focus lands in the search box, with the caret at the start. Typing is the fastest way to reach a
        // setting in a dialog with eight tabs, and a search box you have to click first is one most people never
        // notice at all.
        //
        // On Loaded rather than here: focus set in a constructor is discarded, because the window has no
        // presentation source yet and WPF assigns initial focus itself once it does. CaretIndex is set explicitly
        // rather than left alone - an empty box puts it at 0 anyway, but this dialog is reopened with whatever
        // text was there before once the box remembers it, and "focused, at the start" is the intent.
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.CaretIndex = 0;
        };
    }

    private void OnAnyEdit(object sender, RoutedEventArgs e) => RefreshApplyState();

    /// <summary>
    /// Applies the highlighted theme at once, so it can be seen rather than imagined.
    /// <para>
    /// Nothing is saved: this is a preview, and Cancel or closing the window puts the previous theme back - the host
    /// restores from the settings actually in force. Apply and OK make it permanent through the ordinary path, so
    /// there is no second way for a theme to be persisted.
    /// </para>
    /// <para>
    /// Cheap enough to do on every keyboard step through the combo: swapping the palette dictionary re-resolves the
    /// <c>DynamicResource</c> references without rebuilding a single control template, which is the reason the
    /// theming works this way at all.
    /// </para>
    /// </summary>
    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeCombo.SelectedIndex < 0 || ThemeCombo.SelectedIndex >= _themeNames.Count)
        {
            return;
        }

        ThemePreviewRequested?.Invoke(_themeNames[ThemeCombo.SelectedIndex]);
    }

    /// <summary>
    /// Asks the host to apply a theme without saving it. Raised rather than applied here because the palette belongs
    /// to the application, not to one window - the history window and the overlay have to follow it too.
    /// </summary>
    public event Action<string>? ThemePreviewRequested;

    /// <summary>
    /// Enables Apply only when something differs from what is currently in force.
    /// <para>
    /// Apply is the one button here whose whole meaning is "commit the pending change", so offering it when
    /// there is no pending change invites a click that appears to do nothing. It also gives the dialog a
    /// reliable signal after a successful Apply: the baseline moves to the applied values, so this goes
    /// straight back to disabled.
    /// </para>
    /// </summary>
    private void RefreshApplyState() => ApplyButton.IsEnabled = HasPendingChanges();

    private bool HasPendingChanges()
    {
        // Locations first: they live outside the settings object entirely, so no amount of comparing
        // PasteJumpSettings would notice them.
        // Compared as resolved roots rather than as choices: swapping one custom folder for another leaves the
        // choice identical and the destination different.
        if (!string.Equals(SelectedClipsRoot, _baselineClipsRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(SelectedSettingsRoot, _baselineSettingsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryBuild(out var candidate, out _))
        {
            // A value that cannot be parsed is still a change from what is in force, and leaving Apply
            // disabled here would mean a typo produced a dead button with no explanation. Enabled, so the
            // click surfaces the validation message.
            return true;
        }

        // Both sides are canonical, which is what makes comparing them meaningful: SettingsStore normalises
        // on load and TryBuild normalises what it builds. The one gap is a hand-edited excluded-apps list
        // that is not already normalised - Load canonicalises it into the list box, so it would differ from
        // the baseline and Apply would start enabled. Harmless and self-correcting: one Apply writes the
        // canonical form back.
        return !JsonSerializer.Serialize(candidate, DirtyCheckOptions)
            .Equals(JsonSerializer.Serialize(_baseline, DirtyCheckOptions), StringComparison.Ordinal);
    }

    /// <summary>
    /// Options for the dirty check only - never for persistence. Comparing the serialised form rather than
    /// field by field is deliberate: it covers every property automatically, so adding a setting cannot
    /// forget to mark the dialog dirty. <c>[JsonIgnore]</c> members are excluded for free, which is correct
    /// here since they are all computed from members that are compared.
    /// </summary>
    private static readonly JsonSerializerOptions DirtyCheckOptions = new() { WriteIndented = false };

    /// <summary>Raised with the new settings when the user accepts the dialog.</summary>
    public event Action<PasteJumpSettings>? SettingsApplied;

    /// <summary>
    /// Raised on accept only when either data location actually changed, with the new clips location and
    /// the new settings location in that order.
    /// <para>
    /// Separate from <see cref="SettingsApplied"/> because these are not settings: they live in their own
    /// file outside both data directories - one of them decides where <c>settings.json</c> is, so neither
    /// can be stored in it - and acting on them means moving files and restarting rather than applying a
    /// value in memory.
    /// </para>
    /// </summary>
    public event Action<DataLocationChoice, DataLocationChoice>? DataLocationChangeRequested;

    /// <summary>
    /// Raised when the user asks to import Clipjump's history.
    /// <para>
    /// Handled by the host rather than here because the importer needs the clip store, which this dialog has
    /// no business holding. Fires immediately rather than on OK: it is an action, not a setting, and nothing
    /// about it is pending until the dialog is accepted.
    /// </para>
    /// </summary>
    public event Action? LegacyImportRequested;

    /// <summary>
    /// Fills the combo boxes and then shows the values in force. Split from <see cref="ShowValues"/> because
    /// the item lists are populated with <c>Items.Add</c> and would duplicate if this ran twice, while showing
    /// values has to be repeatable - that is what Reset to Default relies on.
    /// </summary>
    private void Load()
    {
        foreach (var formatter in _formatters.All)
        {
            DefaultFormatterCombo.Items.Add(formatter.DisplayName);
        }

        foreach (var choice in PasteKeystrokeChoices)
        {
            PasteKeystrokeCombo.Items.Add(choice.Label);
        }

        foreach (var key in TriggerKey.Available)
        {
            TriggerKeyCombo.Items.Add(key.ToString());
        }

        BuildPasteKeyRows();

        // Bound once here rather than in XAML: the collection is a field, and a DataGrid whose ItemsSource is
        // reassigned loses any cell edit in progress.
        PasteDelayGrid.ItemsSource = _pasteDelays;

        // Built-ins first, then whatever the catalogue found, so a folder full of theme files cannot push
        // "Same as Windows" off the end of the list.
        foreach (var choice in BuiltInThemeChoices)
        {
            _themeNames.Add(choice.Name);
            ThemeCombo.Items.Add(choice.Label);
        }

        foreach (var theme in _themes?.Themes ?? [])
        {
            _themeNames.Add(theme.Name);
            ThemeCombo.Items.Add(theme.Name);
        }

        // The installed families, sorted, with "(default)" first for the built-in two-font look. Read from
        // Fonts.SystemFontFamilies rather than kept as a list: fonts get installed, and a stale list would be a
        // dialog that cannot offer the font the user just added. Source is the family's own name where it has one
        // for the current culture, so a Japanese system shows the names its users would recognise.
        OverlayFontFamilyCombo.Items.Add(DefaultFontLabel);

        foreach (var name in System.Windows.Media.Fonts.SystemFontFamilies
                     .Select(NameOf)
                     .Where(static n => n.Length > 0)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static n => n, StringComparer.CurrentCultureIgnoreCase))
        {
            OverlayFontFamilyCombo.Items.Add(name);
        }

        // The only place a skipped theme file can be reported. The catalogue deliberately does not fail at start-up
        // over one, so without this a file with a typo in it would simply never appear and nothing would say why.
        ShowThemeProblems();

        foreach (var choice in TrayLeftClickChoices)
        {
            TrayLeftClickCombo.Items.Add(choice.Label);
        }

        foreach (var choice in DensityChoices)
        {
            DensityCombo.Items.Add(choice.Label);
        }

        foreach (var choice in DataLocationChoices)
        {
            ClipsLocationCombo.Items.Add(choice.Label);
            SettingsLocationCombo.Items.Add(choice.Label);
        }

        ExcludedList.ItemsSource = _excluded;
        VersionText.Text = $"PasteJump {AppVersion.Current}";

        ShowValues(_baseline, _baselineClipsLocation, _baselineSettingsLocation);

        // Reflect the real state of the shortcut, not just what settings claim. The user may have deleted it
        // from the Startup folder by hand since the last run.
        RunAtLogonCheck.IsChecked = _baseline.RunAtLogon || StartupShortcut.Exists;
    }

    /// <summary>
    /// Puts a settings object into the controls. Called with the values in force when the dialog opens, and
    /// again with defaults - whole or in part - by Reset to Default on the Advanced page.
    /// <para>
    /// Every control is written, including ones whose value is unchanged. Writing only the differences would
    /// mean a reset silently leaving a control behind the first time someone added one and forgot, which is the
    /// same drift the Advanced page avoids by reflecting over the settings class.
    /// </para>
    /// </summary>
    private void ShowValues(PasteJumpSettings source, DataLocation clipsLocation, DataLocation settingsLocation)
    {
        MonitorClipboardCheck.IsChecked = source.MonitorClipboard;
        StoreImagesCheck.IsChecked = source.StoreImages;
        AllowDuplicatesCheck.IsChecked = source.AllowDuplicateClips;
        LimitMaxClipsCheck.IsChecked = source.LimitMaxClips;
        MaxClipsBox.Text = source.MaxClips.ToString(CultureInfo.CurrentCulture);
        RefreshMaxClipsEnabled();

        _excluded.Clear();

        foreach (var name in ExcludedApps.NormaliseAll(source.IgnoredProcesses))
        {
            _excluded.Add(name);
        }

        RefreshExcludedStatus();

        RecordHistoryCheck.IsChecked = source.RecordHistory;
        RetentionDaysBox.Text = source.HistoryRetentionDays.ToString(CultureInfo.CurrentCulture);
        PreviewMaxCharsBox.Text = source.PreviewMaxChars.ToString(CultureInfo.CurrentCulture);
        HistoryLoadLimitBox.Text = source.HistoryLoadLimit.ToString(CultureInfo.CurrentCulture);
        HistoryPreviewWidthBox.Text = source.HistoryPreviewMaxWidth.ToString(CultureInfo.CurrentCulture);
        ClipJoinSeparatorBox.Text = source.ClipJoinSeparator;

        // Inverted: the setting records that the offer was made, the box asks whether to make it.
        OfferLegacyImportCheck.IsChecked = !source.LegacyImportCompleted;

        PreservePositionCheck.IsChecked = source.PreserveClipPosition;
        OpenSearchCheck.IsChecked = source.OpenSearchImmediately;
        ResetFormatterCheck.IsChecked = source.ResetFormatterOnEntry;

        DefaultFormatterCombo.SelectedItem = _formatters.Resolve(source.DefaultFormatterId).DisplayName;

        PasteKeystrokeCombo.SelectedItem = PasteKeystrokeChoices
            .First(c => c.Keystroke == source.PasteKeystroke).Label;

        WarnAboutConflictCheck.IsChecked = source.WarnAboutClipboardManagerConflict;

        TriggerKeyCombo.SelectedItem = TriggerKey.Normalise(source.PasteModeTriggerKey).ToString();

        // The Keys tab shows the trigger too, read-only. Written here rather than left to the combo's own
        // SelectionChanged, so a Reset moves both.
        _triggerMirrorCombo.SelectedItem = TriggerKey.Normalise(source.PasteModeTriggerKey).ToString();

        // Every combo is written, without exception: a control ShowValues skips keeps its old value through a
        // Reset, which reads as Reset not working. See SettingsInspector's note on the same trap.
        var keyMap = PasteKeyMap.Parse(source.PasteModeKeys);

        foreach (var entry in PasteKeyMap.Entries)
        {
            _pasteKeyCombos[entry.Name].SelectedItem =
                keyMap.LetterFor(entry.Name) is { } letter ? letter.ToString() : KeyOffLabel;
        }

        UpdatePasteKeysHint(keyMap);

        HistoryHotkeyBox.Text = source.HistoryHotkey;

        TrayLeftClickCombo.SelectedItem = TrayLeftClickChoices
            .First(c => c.Action == source.TrayLeftClick).Label;

        // By index through _themeNames, so a theme whose name happens to look like a label cannot confuse it.
        // An unknown name - a theme file that has gone - selects "Same as Windows", which is what is actually in
        // force; the stored setting keeps the old name until the user presses OK, so a missing file does not
        // silently rewrite their choice.
        var themeIndex = _themeNames.FindIndex(n => string.Equals(n, source.Theme, StringComparison.OrdinalIgnoreCase));
        ThemeCombo.SelectedIndex = themeIndex >= 0
            ? themeIndex
            : _themeNames.FindIndex(n => string.Equals(n, ThemeNames.System, StringComparison.Ordinal));
        DensityCombo.SelectedItem = DensityChoices.First(c => c.Density == source.GridDensity).Label;

        ShowCopyNotificationCheck.IsChecked = source.ShowCopyNotification;
        CopyNotificationMsBox.Text = source.CopyNotificationMs.ToString(CultureInfo.CurrentCulture);
        PasteSettleDelayBox.Text = source.PasteSettleDelayMs.ToString(CultureInfo.CurrentCulture);

        // Rebuilt rather than merged, so a Reset genuinely clears the rows instead of leaving the old ones.
        _pasteDelays.Clear();

        foreach (var (process, milliseconds) in PerAppSettleDelays.Parse(source.PasteSettleDelayPerApp).Entries)
        {
            _pasteDelays.Add(new PasteDelayRow
            {
                Process = process,
                Milliseconds = milliseconds.ToString(CultureInfo.CurrentCulture),
            });
        }

        BeepOnCopyCheck.IsChecked = source.BeepOnCopy;
        BeepFrequencyBox.Text = source.BeepFrequencyHz.ToString(CultureInfo.CurrentCulture);
        BeepDurationBox.Text = source.BeepDurationMs.ToString(CultureInfo.CurrentCulture);

        PreviewWidthBox.Text = source.OverlayPreviewMaxWidth.ToString(CultureInfo.CurrentCulture);
        PreviewHeightBox.Text = source.OverlayPreviewMaxHeight.ToString(CultureInfo.CurrentCulture);
        OverlayPreviewCharsBox.Text = source.OverlayPreviewChars.ToString(CultureInfo.CurrentCulture);

        OverlayFontSizeBox.Text = source.OverlayFontSize.ToString(CultureInfo.CurrentCulture);

        // A saved font this machine does not have is added to the list rather than silently reset: the settings
        // file may have come from another machine, and quietly dropping the name would lose it on the next save.
        OverlayFontFamilyCombo.SelectedItem = SelectableFontName(source.OverlayFontFamily);

        // Empty rather than "0" for "not set". Zero is a legal screen coordinate, so using it as the sentinel
        // would make the top-left corner unreachable.
        OverlayXBox.Text = source.OverlayX?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        OverlayYBox.Text = source.OverlayY?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

        ShowKeyHintCheck.IsChecked = source.ShowOverlayKeyHint;

        ShowPositionCheck.IsChecked = source.ShowOverlayPosition;
        TextDetailsCheck.IsChecked = source.ShowOverlayTextDetails;
        TextSizeCheck.IsChecked = source.ShowOverlayTextSize;
        ImageDetailsCheck.IsChecked = source.ShowOverlayImageDetails;
        ImageSizeCheck.IsChecked = source.ShowOverlayImageSize;
        FileDetailsCheck.IsChecked = source.ShowOverlayFileDetails;
        FileSizeCheck.IsChecked = source.ShowOverlayFileSize;
        ShowFormatterCheck.IsChecked = source.ShowOverlayFormatter;
        ShowTagsCheck.IsChecked = source.ShowOverlayTags;
        ShowSourceCheck.IsChecked = source.ShowOverlaySource;
        ShowPinnedCheck.IsChecked = source.ShowOverlayPinned;

        // Deliberately just the setting. Load reconciles it with the Startup folder afterwards, which is right
        // for opening the dialog and wrong for a reset: resetting means "go back to not starting at logon", and
        // a box that stayed ticked because the shortcut is still there would read as the reset being ignored.
        RunAtLogonCheck.IsChecked = source.RunAtLogon;
        TextEditorBox.Text = source.TextEditor;
        ImageEditorBox.Text = source.ImageEditor;

        // Paths first, so the SelectionChanged this triggers finds the box already filled and computes the
        // right hint straight away rather than one showing the application folder for a frame.
        ClipsCustomPathBox.Text = _baselineClipsPath ?? string.Empty;
        SettingsCustomPathBox.Text = _baselineSettingsPath ?? string.Empty;

        ClipsLocationCombo.SelectedItem = DataLocationChoices
            .First(c => c.Location == clipsLocation).Label;

        SettingsLocationCombo.SelectedItem = DataLocationChoices
            .First(c => c.Location == settingsLocation).Label;

        // Explicitly, because Reset writes the same selection back and SelectionChanged does not fire when the
        // value has not changed - which would leave the path box visible after resetting to a non-custom choice.
        RefreshLocationHints();
    }

    /// <summary>Clips location currently picked, which may differ from the one in force.</summary>
    private DataLocation SelectedClipsLocation => LocationIn(ClipsLocationCombo);

    /// <summary>Settings location currently picked, which may differ from the one in force.</summary>
    private DataLocation SelectedSettingsLocation => LocationIn(SettingsLocationCombo);

    /// <summary>The typed folder, or null when this half is not using a custom one.</summary>
    private string? SelectedClipsPath => SelectedClipsLocation == DataLocation.CustomFolder
        ? ClipsCustomPathBox.Text
        : null;

    /// <inheritdoc cref="SelectedClipsPath"/>
    private string? SelectedSettingsPath => SelectedSettingsLocation == DataLocation.CustomFolder
        ? SettingsCustomPathBox.Text
        : null;

    /// <summary>
    /// The root each half would actually use. Compared against the roots in force to decide whether anything
    /// moved - which is the only test that catches one custom folder being swapped for another, where the
    /// location is unchanged and the path is not.
    /// </summary>
    private string SelectedClipsRoot => AppPaths.RootFor(SelectedClipsLocation, SelectedClipsPath);

    /// <inheritdoc cref="SelectedClipsRoot"/>
    private string SelectedSettingsRoot => AppPaths.RootFor(SelectedSettingsLocation, SelectedSettingsPath);

    private static DataLocation LocationIn(ComboBox combo) => DataLocationChoices
        .FirstOrDefault(c => string.Equals(c.Label, combo.SelectedItem as string, StringComparison.Ordinal))
        .Location;

    /// <summary>
    /// Both combos share this handler. It refreshes both labels rather than only the one that changed,
    /// which keeps it correct regardless of which control raised the event.
    /// </summary>
    private void OnDataLocationChanged(object sender, SelectionChangedEventArgs e) => RefreshLocationHints();

    /// <summary>Typing a path changes the resolved folder, so the hints have to follow it.</summary>
    private void OnDataLocationPathChanged(object sender, TextChangedEventArgs e) => RefreshLocationHints();

    private void OnBrowseClipsFolder(object sender, RoutedEventArgs e)
        => BrowseForDataFolder(ClipsCustomPathBox, "Choose where to store clips");

    private void OnBrowseSettingsFolder(object sender, RoutedEventArgs e)
        => BrowseForDataFolder(SettingsCustomPathBox, "Choose where to store settings");

    /// <summary>
    /// Picks a folder with the standard dialog, starting from whatever is already in the box.
    /// <para>
    /// <see cref="OpenFolderDialog"/> rather than the old shell folder browser: it is the modern picker, so it
    /// has a path bar, favourites and the ability to type a path - which matters here, because the folder
    /// someone wants for this is often one they can name faster than they can navigate to.
    /// </para>
    /// </summary>
    private void BrowseForDataFolder(TextBox target, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(target.Text) && Directory.Exists(target.Text))
        {
            dialog.InitialDirectory = target.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FolderName;
        }
    }

    /// <summary>What "switched off" reads as in the letter combos. Not a letter, so it cannot collide with one.</summary>
    private const string KeyOffLabel = "(off)";

    /// <summary>The letter combo for each configurable action, by <c>PasteKeyMap.Entry.Name</c>.</summary>
    private readonly Dictionary<string, ComboBox> _pasteKeyCombos = [];

    /// <summary>
    /// Builds the Keys tab from <see cref="PasteKeyMap.Entries"/>.
    /// <para>
    /// Generated rather than written in XAML so a new paste-mode action cannot be added without appearing here -
    /// the same reason the Advanced tab reflects over the settings class. Built once in the constructor, because
    /// <c>ShowValues</c> runs on every reset and reload and must only ever set values, not rebuild controls.
    /// </para>
    /// <para>
    /// One combo per action rather than a combo plus an "enabled" checkbox: two controls can express "enabled,
    /// but no letter" and "disabled, but a letter", neither of which means anything. <c>(off)</c> as an item
    /// makes the state impossible to contradict.
    /// </para>
    /// </summary>
    /// <summary>
    /// Shows the trigger letter on this tab as well, read-only.
    /// <para>
    /// Not part of <see cref="PasteKeyMap"/> and deliberately not editable here: the trigger is its own setting
    /// on the Paste Mode tab, because it is the one key that <em>opens</em> a session rather than acting inside
    /// one, and it is not adjustable in this release. But a tab listing every paste-mode key while omitting the
    /// most important one reads as an oversight, so it appears here showing what the gesture actually is - the
    /// same reasoning that keeps the Paste Mode combo visible-but-disabled rather than hidden.
    /// </para>
    /// </summary>
    private ComboBox _triggerMirrorCombo = null!;

    private void BuildPasteKeyRows()
    {
        // First, and read-only. The trigger is what the whole gesture is, so it heads the list even though this
        // tab cannot change it.
        _triggerMirrorCombo = AddPasteKeyRow(
            "Step to an older clip",
            "also Down / Right",
            offerOff: false,
            enabled: false,
            note: "Also the key that opens the gesture, so it has its own setting under Paste Mode.");

        foreach (var entry in PasteKeyMap.Entries)
        {
            _pasteKeyCombos[entry.Name] = AddPasteKeyRow(
                entry.Description,
                entry.FixedAlias is { } alias ? "also " + alias : null);
        }

        // And the actions that have no letter at all. Without these the tab documented only its own configurable
        // half - End and Delete appeared nowhere, and Home only as an aside on the newest-clip row - which reads
        // as an omission rather than as the deliberate exclusion it is.
        PasteKeyRows.Children.Add(new TextBlock
        {
            Text = "These cannot be changed",
            Style = (Style)FindResource("SettingHelp"),
            Margin = new Thickness(0, 14, 0, 4),
        });

        foreach (var (keys, description) in PasteKeyMap.FixedActions)
        {
            AddFixedKeyRow(keys, description);
        }
    }

    /// <summary>
    /// A read-only row for an action with no letter: the key on the left, what it does on the right.
    /// <para>
    /// Not a disabled combo like the trigger's row, because a combo of letters cannot show <c>End</c> or
    /// <c>1 - 9</c> - and a control that looks editable but never will be is worse than plain text.
    /// </para>
    /// </summary>
    private void AddFixedKeyRow(string keys, string description)
    {
        var row = new Grid { Style = (Style)FindResource("SettingRow") };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });

        var label = new TextBlock { Text = description };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var keyText = new TextBlock
        {
            Text = keys,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // SetResourceReference, not FindResource: the palette dictionary is swapped wholesale when the theme
        // changes, and a brush fetched once here would keep the colour it had when the row was built. This is
        // the code equivalent of DynamicResource - the styles above are safe because their own setters use it.
        keyText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

        Grid.SetColumn(keyText, 1);
        row.Children.Add(keyText);

        PasteKeyRows.Children.Add(row);
    }

    /// <summary>
    /// One row: a description, a letter combo, and the read-only note about whatever else fires the action.
    /// <para>
    /// Shared by the configurable rows and the trigger's read-only one so the two cannot drift apart visually -
    /// a row that looked different would suggest it behaved differently in some way other than being fixed.
    /// </para>
    /// </summary>
    private ComboBox AddPasteKeyRow(
        string description,
        string? alias,
        bool offerOff = true,
        bool enabled = true,
        string? note = null)
    {
        var row = new Grid { Style = (Style)FindResource("SettingRow") };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

        var label = new TextBlock { Text = description };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var combo = new ComboBox { IsEnabled = enabled };

        if (offerOff)
        {
            combo.Items.Add(KeyOffLabel);
        }

        // Every letter is offered, not only the free ones: a swap - moving tags from T to S and the clipboard
        // from S to T - is a perfectly reasonable thing to want, and it passes through an intermediate state
        // where two actions share a letter. Refusing at the combo would make that impossible to type, so the
        // clash is caught by PasteKeyMap.Validate on OK instead, where it can name both actions.
        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            combo.Items.Add(letter.ToString());
        }

        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);

        // What fires the action regardless of the letter. This is what makes switching a letter off safe rather
        // than lossy: turn pin off and Space still pins.
        if (alias is not null)
        {
            var aliasText = new TextBlock
            {
                Text = alias,
                Style = (Style)FindResource("SettingHelp"),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(aliasText, 2);
            row.Children.Add(aliasText);
        }

        PasteKeyRows.Children.Add(row);

        if (note is not null)
        {
            PasteKeyRows.Children.Add(new TextBlock
            {
                Text = note,
                Style = (Style)FindResource("SettingHelp"),
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        return combo;
    }

    // ------------------------------------------------------------- search

    /// <summary>
    /// Every searchable setting, indexed once from the dialog's own controls.
    /// <para>
    /// Built lazily on the first keystroke rather than in the constructor: the index is only wanted if someone
    /// searches, and the dialog's start-up cost is paid every time it opens.
    /// </para>
    /// </summary>
    private List<SettingsSearchHit>? _searchIndex;

    /// <summary>
    /// The search index, for the UI smoke harness.
    /// <para>
    /// Exposed because the assumption this feature rests on cannot be checked any other way: a
    /// <see cref="TabControl"/> only realises the selected tab, so if the logical-tree walk ever stopped reaching
    /// unselected ones the search would silently cover one tab in eight and still look like it worked. The harness
    /// asserts every tab contributes.
    /// </para>
    /// </summary>
    public IReadOnlyList<(string Label, string TabName)> SearchIndexForSmokeTest()
        => [.. SettingsSearch.Build(Tabs, (Style)FindResource("SettingRow")).Select(h => (h.Label, h.TabName))];

    /// <summary>
    /// Types into the search box, for the UI smoke harness.
    /// <para>
    /// Exists so a screenshot can show the box in use: the clear button and the match count only appear once there
    /// is a query, so an empty dialog proves nothing about either. The results popup opens as a side effect and is
    /// closed again here - it is a separate window, so it would not appear in a render anyway, and leaving one open
    /// behind a window the harness is about to close is asking for trouble.
    /// </para>
    /// </summary>
    public void TypeInSearchForSmokeTest(string text)
    {
        SearchBox.Text = text;
        SearchPopup.IsOpen = false;
    }

    /// <summary>Runs a query against the index, for the UI smoke harness. Results in the order the popup shows.</summary>
    public IReadOnlyList<(string Label, string TabName)> SearchForSmokeTest(string query)
    {
        _searchIndex ??= SettingsSearch.Build(Tabs, (Style)FindResource("SettingRow"));

        return [.. SettingsSearch.Filter(_searchIndex, query).Select(h => (h.Label, h.TabName))];
    }

    /// <summary>
    /// Moves focus to the search box and selects what is there, so typing replaces it.
    /// <para>
    /// Bound to Ctrl+K, Ctrl+E and Ctrl+F - the same three the history window uses, because a shortcut that works
    /// in one window of an application and not the other is worse than none. Always this box, never the Advanced
    /// tab's filter: one chord with two destinations depending on the selected tab is not a shortcut anyone can
    /// trust.
    /// </para>
    /// </summary>
    public ICommand FocusSearchCommand => _focusSearch ??= new RelayCommand(() =>
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    });

    private ICommand? _focusSearch;

    /// <summary>
    /// Clears the search and puts the caret back in the box.
    /// <para>
    /// Focus is returned deliberately: the button is <c>Focusable="False"</c>, so without this the caret would be
    /// left wherever it was and the obvious next action - typing a different query - would go somewhere else.
    /// </para>
    /// </summary>
    private void OnSearchClearClicked(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void OnAdvancedFilterClearClicked(object sender, RoutedEventArgs e)
    {
        AdvancedFilterBox.Clear();
        AdvancedFilterBox.Focus();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = SearchBox.Text.Length == 0;

        SearchCue.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        // Shown only when there is something to clear. A permanently visible cross invites a click that does
        // nothing, and it crowds an empty box that already carries its cue text.
        SearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        _searchIndex ??= SettingsSearch.Build(Tabs, (Style)FindResource("SettingRow"));

        var hits = SettingsSearch.Filter(_searchIndex, SearchBox.Text);

        SearchResults.ItemsSource = hits;
        SearchCount.Text = SearchBox.Text.Length == 0
            ? string.Empty
            : hits.Count switch
            {
                0 => "no matches",
                1 => "1 match",
                _ => $"{hits.Count} matches",
            };

        // Opened only when there is something to show, so an unmatched query leaves an empty box rather than an
        // empty list hanging under it.
        SearchPopup.IsOpen = hits.Count > 0;

        if (hits.Count > 0)
        {
            SearchResults.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// Down moves into the list, Enter takes the first match, Esc closes.
    /// <para>
    /// Handled on the box rather than the list, because the list never has focus while typing - moving focus to it
    /// on every keystroke would make the box unusable.
    /// </para>
    /// </summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when SearchPopup.IsOpen:
                SearchPopup.IsOpen = false;
                e.Handled = true;
                break;

            case Key.Down when SearchPopup.IsOpen:
                SearchResults.Focus();

                if (SearchResults.ItemContainerGenerator.ContainerFromIndex(SearchResults.SelectedIndex)
                    is ListBoxItem item)
                {
                    item.Focus();
                }

                e.Handled = true;
                break;

            case Key.Enter when SearchResults.SelectedItem is SettingsSearchHit hit:
                GoTo(hit);
                e.Handled = true;
                break;
        }
    }

    private void OnSearchResultKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when SearchResults.SelectedItem is SettingsSearchHit hit:
                GoTo(hit);
                e.Handled = true;
                break;

            case Key.Escape:
                SearchPopup.IsOpen = false;
                SearchBox.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnSearchResultClicked(object sender, MouseButtonEventArgs e)
    {
        if (SearchResults.SelectedItem is SettingsSearchHit hit)
        {
            GoTo(hit);
        }
    }

    /// <summary>
    /// Selects the setting's tab, scrolls its control into view and flashes it.
    /// <para>
    /// The scroll and the flash are deferred to a later dispatcher pass, and that is not optional: selecting a tab
    /// applies its template for the first time, so until a layout pass has run the control has no position to
    /// scroll to and <c>BringIntoView</c> does nothing at all.
    /// </para>
    /// </summary>
    private void GoTo(SettingsSearchHit hit)
    {
        SearchPopup.IsOpen = false;
        hit.Tab.IsSelected = true;

        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                hit.Target.BringIntoView();
                Flash(hit.Target);
                hit.Target.Focus();
            }));
    }

    /// <summary>
    /// A brief accent wash behind the control, so the eye lands on the right row.
    /// <para>
    /// Painted on the row rather than the control where possible - a highlight around a text box reads better than
    /// one inside it. The brush is created per flash and animated to transparent, then removed: animating a shared
    /// palette brush would tint every other use of it, and leaving the background set would make the row look
    /// permanently selected.
    /// </para>
    /// </summary>
    private static void Flash(FrameworkElement target)
    {
        // Only a SettingRow grid is washed. A check box's parent is the tab's StackPanel, and painting that would
        // wash the entire page - so for those the focus WPF has just given the control is the whole signal. The
        // grid is found by walking up rather than passed in, because "the row" is exactly "the grid the control
        // sits in".
        if (target.Parent is not Grid row)
        {
            return;
        }

        // Written as ARGB components rather than parsed from a string, so a typo is a compile error.
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(0x3F, 0x7F, 0xB4, 0xFF));

        row.Background = brush;

        // Held briefly, then faded. A wash that starts fading immediately is easy to miss if the tab was still
        // rendering when it began.
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1400))
        {
            BeginTime = TimeSpan.FromMilliseconds(400),
        };

        // Cleared afterwards, or the row keeps a transparent brush that reads as permanently selected the moment
        // anything re-evaluates it. The brush is created per flash rather than shared: animating a palette brush
        // would tint every other control using it.
        fade.Completed += (_, _) => row.Background = null;
        brush.BeginAnimation(System.Windows.Media.Brush.OpacityProperty, fade);
    }

    // ------------------------------------------------------------- per-application paste delays

    /// <summary>
    /// One editable row of the per-application delay grid.
    /// <para>
    /// A mutable class with <see cref="INotifyPropertyChanged"/> rather than a record: the grid edits these in
    /// place, and a record's <c>init</c> properties cannot be written back from a cell. The delay is a string so a
    /// half-typed value is held rather than rejected mid-keystroke - it is parsed and refused on OK, like every
    /// other number in this dialog.
    /// </para>
    /// </summary>
    public sealed class PasteDelayRow : System.ComponentModel.INotifyPropertyChanged
    {
        private string _process = string.Empty;
        private string _milliseconds = string.Empty;

        public string Process
        {
            get => _process;
            set { _process = value; Raise(nameof(Process)); }
        }

        public string Milliseconds
        {
            get => _milliseconds;
            set { _milliseconds = value; Raise(nameof(Milliseconds)); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    private readonly ObservableCollection<PasteDelayRow> _pasteDelays = [];

    private void OnAddPasteDelayClicked(object sender, RoutedEventArgs e)
    {
        // Seeded with the global delay rather than something arbitrary: the row is a starting point to edit, and
        // the current value is the only defensible starting point.
        var current = PasteSettleDelayBox.Text;

        var chosen = RunningAppPicker.Choose(this, _pasteDelays.Select(static row => row.Process));

        foreach (var process in chosen)
        {
            _pasteDelays.Add(new PasteDelayRow { Process = process, Milliseconds = current });
        }

        RefreshApplyState();
    }

    /// <summary>
    /// Fills the grid with the applications known to cache the clipboard.
    /// <para>
    /// Programs already listed are skipped rather than overwritten - a value someone has tuned for their machine is
    /// worth more than the suggestion, and it makes pressing the button twice harmless.
    /// </para>
    /// </summary>
    private void OnAddKnownSlowProgramsClicked(object sender, RoutedEventArgs e)
    {
        var added = KnownSlowPasteTargets.NotAlreadyListed(_pasteDelays.Select(static row => row.Process));

        foreach (var target in added)
        {
            _pasteDelays.Add(new PasteDelayRow
            {
                Process = target.Process,
                Milliseconds = target.Milliseconds.ToString(CultureInfo.CurrentCulture),
            });
        }

        // AppliedText, not ValidationText: that one is DangerBrush red, so a cheerful "added 13 programs" would
        // have read as an error. This is the dialog's neutral line for saying what just happened.
        //
        // Said out loud including the nothing-to-do case, because a button that appears to do nothing reads as
        // broken and "they are all already there" is the answer worth giving.
        AppliedText.Text = added.Count == 0
            ? "Every known slow program is already listed."
            : $"Added {added.Count} program{(added.Count == 1 ? string.Empty : "s")} with a starting delay — "
                + "edit the numbers to suit your machine. Nothing is saved until you press OK or Apply.";

        AppliedText.Visibility = Visibility.Visible;
        RefreshApplyState();
    }

    private void OnRemovePasteDelayClicked(object sender, RoutedEventArgs e)
    {
        foreach (var row in PasteDelayGrid.SelectedItems.OfType<PasteDelayRow>().ToList())
        {
            _pasteDelays.Remove(row);
        }

        RefreshApplyState();
    }

    /// <summary>
    /// Reads the grid, refusing anything unusable.
    /// <para>
    /// Blank rows are dropped rather than refused: the grid cannot add rows itself, but a row whose program was
    /// deleted by hand is plainly abandoned rather than a mistake worth stopping OK for.
    /// </para>
    /// </summary>
    private bool TryReadPasteDelays(out PerAppSettleDelays delays, out string error)
    {
        var entries = new List<(string Process, int Milliseconds)>();

        foreach (var row in _pasteDelays)
        {
            if (string.IsNullOrWhiteSpace(row.Process) && string.IsNullOrWhiteSpace(row.Milliseconds))
            {
                continue;
            }

            if (!int.TryParse(row.Milliseconds, NumberStyles.Integer, CultureInfo.CurrentCulture, out var ms))
            {
                delays = PerAppSettleDelays.Empty;
                error = $"\"{row.Milliseconds}\" is not a number of milliseconds for {row.Process}.";
                return false;
            }

            entries.Add((row.Process, ms));
        }

        if (PerAppSettleDelays.Validate(entries) is { } refusal)
        {
            delays = PerAppSettleDelays.Empty;
            error = refusal;
            return false;
        }

        delays = PerAppSettleDelays.FromEntries(entries);
        error = string.Empty;
        return true;
    }

    // ------------------------------------------------------------- export and import

    /// <summary>
    /// Writes the settings as the dialog currently has them to a file the user chooses.
    /// <para>
    /// The <em>pending</em> values, not the saved ones, because "export" plainly means "what I am looking at". That
    /// means it has to validate first, and a refusal is reported the same way OK reports it - exporting a value the
    /// dialog would not accept would produce a file that cannot be imported.
    /// </para>
    /// </summary>
    private void OnExportSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (!TryBuild(out var settings, out var error))
        {
            ValidationText.Text = error;
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export PasteJump settings",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
            FileName = SettingsTransfer.SuggestFileName(DateTimeOffset.Now),
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, SettingsTransfer.Export(settings));
            AdvancedStatusNote = $"Exported to {Path.GetFileName(dialog.FileName)}.";
            RefreshAdvanced();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageDialog.Warn(ex.Message, owner: this, headline: "Could not write that file");
        }
    }

    /// <summary>
    /// Loads settings from a file into the dialog's controls.
    /// <para>
    /// Into the controls, not into force: nothing is saved until OK or Apply, so Cancel still abandons an import -
    /// which is what makes trying one safe. That also means every imported value passes back through the same
    /// validation as a hand-typed one.
    /// </para>
    /// </summary>
    private void OnImportSettingsClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import PasteJump settings",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string json;

        try
        {
            json = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageDialog.Warn(ex.Message, owner: this, headline: "Could not read that file");
            return;
        }

        var imported = SettingsTransfer.TryImport(json, _baseline, out var error);

        if (imported is null)
        {
            MessageDialog.Warn(error, owner: this, headline: "Could not import those settings");
            return;
        }

        // Straight through ShowValues, which is the same path a Reset takes - so every control is written and none
        // keeps a stale value. The excluded-apps list is not a control, so it is replaced by hand.
        ShowValues(imported, SelectedClipsLocation, SelectedSettingsLocation);

        _excluded.Clear();

        foreach (var process in imported.IgnoredProcesses)
        {
            _excluded.Add(process);
        }

        AdvancedStatusNote =
            $"Imported from {Path.GetFileName(dialog.FileName)}. Nothing is saved until you press OK or Apply.";

        RefreshAdvanced();
        RefreshApplyState();
    }

    /// <summary>Reads the Keys tab into the shape <see cref="PasteKeyMap"/> validates and builds from.</summary>
    private Dictionary<string, char?> ReadPasteKeyChoices()
    {
        var choices = new Dictionary<string, char?>();

        foreach (var entry in PasteKeyMap.Entries)
        {
            var selected = _pasteKeyCombos[entry.Name].SelectedItem as string;

            choices[entry.Name] = selected is { Length: 1 } letter ? letter[0] : null;
        }

        return choices;
    }

    /// <summary>
    /// Says how many letters are left for the trigger, because moving an action off a letter is what frees one -
    /// and the connection between the two tabs is not otherwise visible.
    /// </summary>
    private void UpdatePasteKeysHint(PasteKeyMap map)
    {
        var free = TriggerKey.AvailableFor(map).Count;

        PasteKeysHintText.Text =
            $"{free} letters are free, and any of them could be the paste-mode trigger under Paste Mode.";
    }

    /// <summary>
    /// Brings the tab holding a control to the front, so a refusal is shown next to the control that caused it
    /// rather than leaving the user to find it.
    /// </summary>
    private void SelectTabContaining(DependencyObject control)
    {
        for (DependencyObject? node = control; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is TabItem tab)
            {
                tab.IsSelected = true;
                return;
            }
        }
    }

    /// <summary>
    /// Spells out the chord and warns when it is no longer the original's. Worth saying out loud: changing
    /// this changes the one gesture the whole application is built around, and muscle memory will not have
    /// been consulted.
    /// </summary>
    private void OnTriggerKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        var key = TriggerKey.Normalise(TriggerKeyCombo.SelectedItem as string);

        // Kept in step with the read-only copy on the Keys tab. Costs nothing today, since the trigger combo is
        // disabled in this release, and stops the two disagreeing the moment it is enabled.
        if (_triggerMirrorCombo is not null)
        {
            _triggerMirrorCombo.SelectedItem = key.ToString();
        }

        TriggerKeyHintText.Text = key == TriggerKey.Default
            ? $"{TriggerKey.Describe(key)} opens paste mode, and tapping {key} again steps further back."
            : $"{TriggerKey.Describe(key)} opens paste mode. Ctrl+V goes back to being an ordinary paste.";
    }

    /// <summary>
    /// Checks each half that asked for a custom folder, naming the half in the message so the user knows which
    /// box to fix when both are set.
    /// </summary>
    private bool TryValidateCustomFolders(out string error)
    {
        error = string.Empty;

        if (SelectedClipsLocation == DataLocation.CustomFolder)
        {
            var problem = CustomDataFolder.Validate(ClipsCustomPathBox.Text, out var resolved);

            if (problem != CustomFolderProblem.Ok)
            {
                error = "Clips folder: " + CustomDataFolder.Describe(problem, ClipsCustomPathBox.Text);
                return false;
            }

            // Written back in canonical form, so the pointer file records "D:\PasteJump" rather than
            // "d:/pastejump/" and the comparisons above stop depending on how it was typed.
            ClipsCustomPathBox.Text = resolved;
        }

        if (SelectedSettingsLocation == DataLocation.CustomFolder)
        {
            var problem = CustomDataFolder.Validate(SettingsCustomPathBox.Text, out var resolved);

            if (problem != CustomFolderProblem.Ok)
            {
                error = "Settings folder: " + CustomDataFolder.Describe(problem, SettingsCustomPathBox.Text);
                return false;
            }

            SettingsCustomPathBox.Text = resolved;
        }

        return true;
    }

    private void RefreshLocationHints()
    {
        // The path box only applies to the custom choice, so it appears with it and goes away again.
        ClipsCustomRow.Visibility = SelectedClipsLocation == DataLocation.CustomFolder
            ? Visibility.Visible
            : Visibility.Collapsed;

        SettingsCustomRow.Visibility = SelectedSettingsLocation == DataLocation.CustomFolder
            ? Visibility.Visible
            : Visibility.Collapsed;

        ClipsLocationPathText.Text = Describe(SelectedClipsRoot, _baselineClipsRoot);
        SettingsLocationPathText.Text = Describe(SelectedSettingsRoot, _baselineSettingsRoot);

        // Says so up front rather than only in the confirmation prompt, so the restart is not a surprise
        // discovered after clicking OK. Compared by resolved root rather than by location, so swapping one
        // custom folder for another is recognised as a move.
        static string Describe(string selectedRoot, string baselineRoot)
        {
            var path = Path.Combine(selectedRoot, "data");

            return string.Equals(selectedRoot, baselineRoot, StringComparison.OrdinalIgnoreCase)
                ? path
                : path + "   (restart required)";
        }
    }

    /// <summary>
    /// Validates and commits, without closing. Returns false when validation failed, in which case the
    /// error is already on screen and the caller must not close the window.
    /// </summary>
    private bool TryApply()
    {
        if (!TryBuild(out var updated, out var error))
        {
            ValidationText.Text = error;
            ValidationText.Visibility = Visibility.Visible;
            AppliedText.Visibility = Visibility.Collapsed;
            return false;
        }

        // Both custom folders are checked before anything is saved, and a bad one stops the whole Apply. The
        // failure being prevented is the worst this application has: accept a folder that cannot be written,
        // restart onto it, and the database cannot be opened - so it looks as though every clip has gone.
        if (!TryValidateCustomFolders(out var locationError))
        {
            ValidationText.Text = locationError;
            ValidationText.Visibility = Visibility.Visible;
            AppliedText.Visibility = Visibility.Collapsed;
            return false;
        }

        ValidationText.Visibility = Visibility.Collapsed;

        SettingsApplied?.Invoke(updated);

        var clips = new DataLocationChoice(SelectedClipsLocation, SelectedClipsPath);
        var settings = new DataLocationChoice(SelectedSettingsLocation, SelectedSettingsPath);

        // Raised after SettingsApplied, so the settings are saved to their current location before
        // anything starts moving them. The handler may restart the process, which ends this method.
        //
        // Compared by resolved root, so one custom folder swapped for another counts as a move even though
        // the choice did not change.
        if (!clips.SameRootAs(_baselineClipsRoot) || !settings.SameRootAs(_baselineSettingsRoot))
        {
            DataLocationChangeRequested?.Invoke(clips, settings);
        }

        // The baseline moves to what is now in force. Without this, a second Apply would re-raise the
        // location change and prompt to move data that has already been moved.
        _baseline = updated;
        _baselineClipsLocation = clips.Location;
        _baselineSettingsLocation = settings.Location;
        _baselineClipsPath = clips.Path;
        _baselineSettingsPath = settings.Path;
        _baselineClipsRoot = clips.Root;
        _baselineSettingsRoot = settings.Root;

        RefreshLocationHints();
        RefreshAdvanced();

        // The baseline has just moved to these values, so nothing is pending any more and Apply goes back to
        // disabled until the next edit. This is the whole reason the check is computed rather than a flag.
        RefreshApplyState();
        return true;
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        if (TryApply())
        {
            Close();
        }
    }

    /// <summary>
    /// Commits and stays open, so a setting can be nudged and its effect watched without reopening the
    /// dialog each time - which matters most for the paste and notification timings, where finding the
    /// right value means trying several.
    /// </summary>
    private void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        if (!TryApply())
        {
            return;
        }

        // Explicit acknowledgement. Apply produces no visible change in the dialog itself, so without
        // this there is no way to tell it from a click that missed.
        AppliedText.Text = $"Applied at {DateTime.Now:HH:mm:ss}";
        AppliedText.Visibility = Visibility.Visible;
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();

    private void OnImportClicked(object sender, RoutedEventArgs e) => LegacyImportRequested?.Invoke();

    /// <summary>
    /// Writes the palette currently in force as a theme file and opens the folder on it.
    /// <para>
    /// Named after the theme in the combo, with a number appended if that file already exists - rather than
    /// overwriting, which would destroy work, or refusing, which would leave the user to guess a free name. The file
    /// is a complete copy of the current palette, so it is immediately valid and immediately editable.
    /// </para>
    /// </summary>
    private void OnCreateThemeClicked(object sender, RoutedEventArgs e)
    {
        if (_themes is null || ThemeCreationRequested is null)
        {
            return;
        }

        // Named after what the combo is showing, since "from this one" means the theme on screen. " copy" is not
        // decoration: a built-in name would be refused by the parser, and a user theme of the same name would
        // replace the original in the catalogue - so the suggestion has to differ either way.
        var basedOnName = ThemeCombo.SelectedIndex >= 0 && ThemeCombo.SelectedIndex < _themeNames.Count
            ? _themeNames[ThemeCombo.SelectedIndex]
            : ThemeNames.System;

        ThemeCreationRequested.Invoke($"{basedOnName} copy");
    }

    /// <summary>
    /// Opens the selected theme for editing, writing it out first when there is no file to open.
    /// <para>
    /// Three cases, and they differ in what "edit this" can honestly mean. A user theme has a file, so it is simply
    /// opened. A shipped theme has none, so it is written out under <em>its own name</em> - the catalogue lets a user
    /// file replace a shipped theme of the same name, which is exactly what editing one should do. Light, Dark and
    /// "Same as Windows" cannot be edited in place at all: the parser refuses those names, and they are the bases
    /// every other theme inherits from, so they are copied to a new name instead.
    /// </para>
    /// </summary>
    private void OnEditThemeClicked(object sender, RoutedEventArgs e)
    {
        if (ThemeEditRequested is null || ThemeCombo.SelectedIndex < 0 || ThemeCombo.SelectedIndex >= _themeNames.Count)
        {
            return;
        }

        ThemeEditRequested.Invoke(_themeNames[ThemeCombo.SelectedIndex]);
    }

    private void OnReloadThemesClicked(object sender, RoutedEventArgs e) => ReloadThemes();

    /// <summary>
    /// Re-reads the themes folder from outside. For the host, after it has written a theme file on this dialog's
    /// behalf - the new file has to appear in the list, or a second Edit would overwrite what was just typed into it.
    /// </summary>
    public void ReloadThemesForHost() => ReloadThemes();

    /// <summary>
    /// Re-reads the themes folder and rebuilds the list, keeping the selection.
    /// <para>
    /// The manual half of a live preview: an edit happens in a text editor, and nothing tells this dialog when the
    /// file was saved. A <c>FileSystemWatcher</c> was the alternative and is worse - it fires while an editor is
    /// still writing, so a theme would be read half-saved and reported as broken.
    /// </para>
    /// <para>
    /// Selection is restored <em>by name</em> and the preview re-applied, so pressing Reload after an edit shows the
    /// new colours. A theme whose file has gone falls back to the stored setting rather than to whatever now happens
    /// to sit at the same index.
    /// </para>
    /// </summary>
    private void ReloadThemes()
    {
        var wanted = ThemeCombo.SelectedIndex >= 0 && ThemeCombo.SelectedIndex < _themeNames.Count
            ? _themeNames[ThemeCombo.SelectedIndex]
            : _baseline.Theme;

        _themes?.Refresh();

        // Detached while the list is rebuilt: clearing the items fires SelectionChanged with -1 and then again on
        // repopulation, which would preview twice and flash the window.
        ThemeCombo.SelectionChanged -= OnThemeSelectionChanged;

        _themeNames.Clear();
        ThemeCombo.Items.Clear();

        foreach (var choice in BuiltInThemeChoices)
        {
            _themeNames.Add(choice.Name);
            ThemeCombo.Items.Add(choice.Label);
        }

        foreach (var theme in _themes?.Themes ?? [])
        {
            _themeNames.Add(theme.Name);
            ThemeCombo.Items.Add(theme.Name);
        }

        var index = _themeNames.FindIndex(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));

        ThemeCombo.SelectedIndex = index >= 0
            ? index
            : _themeNames.FindIndex(n => string.Equals(n, ThemeNames.System, StringComparison.Ordinal));

        ThemeCombo.SelectionChanged += OnThemeSelectionChanged;

        ShowThemeProblems();

        // Explicitly, because the handler was detached over the rebuild - and re-applying is the whole point of
        // Reload: the file may now say something different under the same name.
        if (ThemeCombo.SelectedIndex >= 0 && ThemeCombo.SelectedIndex < _themeNames.Count)
        {
            ThemePreviewRequested?.Invoke(_themeNames[ThemeCombo.SelectedIndex]);
        }
    }

    /// <summary>
    /// Shows why any theme file was skipped, or hides the line when none was. The only place this can be reported -
    /// see <see cref="ThemeCatalog.Refresh"/>.
    /// </summary>
    private void ShowThemeProblems()
    {
        if (_themes is { Problems.Count: > 0 })
        {
            ThemeProblems.Text = "Some theme files were skipped - " + string.Join("; ", _themes.Problems);
            ThemeProblems.Visibility = Visibility.Visible;
        }
        else
        {
            ThemeProblems.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Asks the host to open a theme's file for editing, writing one out first if it has none.</summary>
    public event Action<string>? ThemeEditRequested;

    private void OnOpenThemesFolderClicked(object sender, RoutedEventArgs e) => ThemesFolderRequested?.Invoke();

    /// <summary>
    /// Asks the host to write a theme file. Raised rather than done here because it needs the live palette and the
    /// shell, neither of which a settings dialog should be reaching for - the same shape as
    /// <see cref="LegacyImportRequested"/>.
    /// </summary>
    public event Action<string>? ThemeCreationRequested;

    /// <summary>Asks the host to open the themes folder in Explorer.</summary>
    public event Action? ThemesFolderRequested;

    /// <summary>
    /// Puts a retention value changed behind the dialog's back into the box, and into the baseline.
    /// <para>
    /// Needed because the import can offer to switch retention off while this dialog is open. Without this the
    /// box still holds the old number and writes it straight back on OK, quietly undoing the choice the user
    /// just made in the import prompt.
    /// </para>
    /// </summary>
    public void ReloadRetention(int days)
    {
        RetentionDaysBox.Text = days.ToString(CultureInfo.CurrentCulture);
        _baseline.HistoryRetentionDays = days;
        RefreshAdvanced();
    }

    /// <summary>
    /// Puts a density chosen in the history window into this dialog, and into the baseline.
    /// <para>
    /// Same reasoning as <see cref="ReloadRetention"/>: without it the combo still holds the old value and
    /// writes it back on OK, undoing a choice the user made moments earlier in the other window. Updating the
    /// baseline first matters - setting the combo raises SelectionChanged, and the dirty check runs from there,
    /// so a stale baseline would light up Apply for a change that has already been saved.
    /// </para>
    /// </summary>
    public void ReloadDensity(GridDensity density)
    {
        _baseline.GridDensity = density;

        DensityCombo.SelectedItem = DensityChoices
            .First(c => c.Density == density).Label;

        RefreshAdvanced();
    }

    // ------------------------------------------------------------------ excluded apps

    private void OnExcludedSelectionChanged(object sender, SelectionChangedEventArgs e)
        => RemoveExcludedButton.IsEnabled = ExcludedList.SelectedItems.Count > 0;

    private void OnAddExcludedClicked(object sender, RoutedEventArgs e) => AddTypedExclusion();

    /// <summary>Enter in the entry box adds, which is what anyone typing a name will try first.</summary>
    private void OnExcludedEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        AddTypedExclusion();

        // Handled, or Enter also fires the dialog's default button and closes the window on the user
        // mid-edit - which is a genuinely infuriating way to lose a list you were part-way through building.
        e.Handled = true;
    }

    private void AddTypedExclusion()
    {
        var normalised = ExcludedApps.Normalise(ExcludedEntryBox.Text);

        if (normalised is null)
        {
            RefreshExcludedStatus("Type a program's file name, for example keepass.exe.");
            return;
        }

        if (!TryAddExclusion(normalised))
        {
            RefreshExcludedStatus($"{normalised} is already excluded.");
            return;
        }

        ExcludedEntryBox.Clear();
        ExcludedEntryBox.Focus();
        RefreshExcludedStatus($"Added {normalised}.");
    }

    private void OnAddFromRunningClicked(object sender, RoutedEventArgs e)
    {
        var chosen = RunningAppPicker.Choose(this, _excluded);

        if (chosen.Count == 0)
        {
            return;
        }

        var added = chosen.Count(TryAddExclusion);

        RefreshExcludedStatus(added == 1 ? $"Added {chosen[0]}." : $"Added {added} programs.");
    }

    private void OnOpenClipsFolder(object sender, MouseButtonEventArgs e)
        => OpenDataFolder(SelectedClipsRoot);

    private void OnOpenSettingsFolder(object sender, MouseButtonEventArgs e)
        => OpenDataFolder(SelectedSettingsRoot);

    /// <summary>
    /// Opens a data folder in Explorer.
    /// <para>
    /// The path is recomputed from the selected location rather than read off the label, because the label may
    /// carry a "(restart required)" suffix once the combo has been changed - handing that to Explorer would
    /// simply fail. For the same reason the folder may not exist yet: the move happens on the next start-up, so
    /// the pending destination is a directory that has never been created. Falling back to its parent shows the
    /// user where it is going to be rather than reporting an error about a folder they have just chosen.
    /// </para>
    /// </summary>
    private void OpenDataFolder(string root)
    {
        var path = Path.Combine(root, "data");

        var target = Directory.Exists(path)
            ? path
            : Directory.GetParent(path)?.FullName;

        if (target is null || !Directory.Exists(target))
        {
            ValidationText.Text = $"{path} does not exist yet. It is created when PasteJump next starts.";
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            // UseShellExecute, because explorer.exe is being asked to interpret a path rather than run as a
            // child process with inherited handles.
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ValidationText.Text = $"Could not open {target}: {ex.Message}";
            ValidationText.Visibility = Visibility.Visible;
        }
    }

    private void OnBrowseTextEditorClicked(object sender, RoutedEventArgs e)
        => BrowseForEditor(TextEditorBox, "Choose the program to open text clips with");

    private void OnBrowseImageEditorClicked(object sender, RoutedEventArgs e)
        => BrowseForEditor(ImageEditorBox, "Choose the program to open image clips with");

    /// <summary>
    /// Picks an executable into <paramref name="box"/>.
    /// <para>
    /// Unlike the excluded-apps picker, the full path is kept rather than the file name: this value is handed to
    /// <c>Process.Start</c>, so a bare name only works for something already on the PATH. Keeping the path is
    /// what makes an editor that is not on the PATH - most of them - work at all.
    /// </para>
    /// <para>
    /// Opens at whatever is currently in the box when that resolves to a real file, so browsing from
    /// <c>notepad.exe</c> starts in System32 rather than wherever the dialog last was.
    /// </para>
    /// </summary>
    private void BrowseForEditor(TextBox box, string title)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "Programs|*.exe;*.com;*.bat;*.cmd|All files|*.*",
            CheckFileExists = true,
        };

        var current = ResolveExecutable(box.Text);

        if (current is not null)
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
            dialog.FileName = Path.GetFileName(current);
        }

        if (dialog.ShowDialog(this) == true)
        {
            box.Text = dialog.FileName;
        }
    }

    /// <summary>
    /// The full path of an editor setting, whether it is already a path or a bare name on the PATH. Null when
    /// neither resolves, in which case the dialog simply opens wherever it last was.
    /// </summary>
    private static string? ResolveExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().Trim('"');

        try
        {
            if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
            {
                return trimmed;
            }

            // Bare names are the common case - notepad.exe and mspaint.exe both ship as defaults - and both
            // live in a PATH directory rather than anywhere guessable.
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim(), trimmed);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception)
        {
            // A malformed path from a hand-edited settings file. Not worth failing the browse over.
        }

        return null;
    }

    private void OnBrowseExcludedClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a program to exclude",
            Filter = "Programs|*.exe|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // Only the file name is kept. Capture resolves the foreground window to a process and gets a file
        // name, never a path, so storing the full path would produce an entry that can never match.
        var normalised = ExcludedApps.Normalise(dialog.FileName);

        if (normalised is null)
        {
            return;
        }

        RefreshExcludedStatus(TryAddExclusion(normalised)
            ? $"Added {normalised}."
            : $"{normalised} is already excluded.");
    }

    private void OnRemoveExcludedClicked(object sender, RoutedEventArgs e)
    {
        // Copied before removing: SelectedItems mutates as items leave the collection, so iterating it
        // directly removes every other entry.
        var doomed = ExcludedList.SelectedItems.OfType<string>().ToList();

        foreach (var name in doomed)
        {
            _excluded.Remove(name);
        }

        RefreshExcludedStatus(doomed.Count == 1 ? $"Removed {doomed[0]}." : $"Removed {doomed.Count} programs.");
        RefreshAdvanced();
    }

    private bool TryAddExclusion(string normalised)
    {
        if (ExcludedApps.Contains(_excluded, normalised))
        {
            return false;
        }

        _excluded.Add(normalised);
        RefreshAdvanced();
        return true;
    }

    private void RefreshExcludedStatus(string? message = null)
    {
        ExcludedStatusText.Text = message ?? (_excluded.Count == 0
            ? "Nothing is excluded. Everything you copy is recorded."
            : $"{_excluded.Count} program{(_excluded.Count == 1 ? string.Empty : "s")} excluded.");
    }

    private void OnLimitMaxClipsChanged(object sender, RoutedEventArgs e) => RefreshMaxClipsEnabled();

    /// <summary>
    /// Greys the count while the limit is off, so it is obvious the number is not in force.
    /// <para>
    /// The label is dimmed as well as the box. Disabling only the input leaves a full-strength label beside a
    /// greyed field, which reads as a rendering fault rather than as a deliberate state.
    /// </para>
    /// </summary>
    private void RefreshMaxClipsEnabled()
    {
        var limiting = LimitMaxClipsCheck.IsChecked == true;

        MaxClipsBox.IsEnabled = limiting;
        MaxClipsLabel.IsEnabled = limiting;
    }

    /// <summary>
    /// Rebuilds the Advanced list from the settings currently entered in the dialog, so it reflects
    /// pending edits rather than only what was on disk when the window opened.
    /// </summary>
    private void RefreshAdvanced()
    {
        // Falls back to the loaded settings when the form does not currently validate - the inventory
        // should still be readable while a text box holds a half-typed number.
        var source = TryBuild(out var pending, out _) ? pending : _baseline;

        var filter = AdvancedFilterBox.Text;

        // The Where column, filled per row. A child row carries no key of its own, so it inherits the tab of the
        // composite setting above it - which is the right answer anyway: its control lives there too.
        var lastTab = string.Empty;

        var rows = SettingsInspector.Describe(source, SelectedClipsLocation, SelectedSettingsLocation)
            .Select(r =>
            {
                var tab = r.CanReset ? TabFor(r.Key) : lastTab;
                lastTab = tab;

                return r with { Where = tab };
            })
            .Where(r => string.IsNullOrWhiteSpace(filter)
                || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Where.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        AdvancedGrid.ItemsSource = rows;

        // Settings and their detail rows counted separately, and only settings counted as "changed". A row for one
        // excluded program is always different from "(none)", so counting those would report nine changes where the
        // user has made seven - and that number is the one people check when behaviour surprises them.
        var settingCount = rows.Count(static r => r.CanReset);
        var detailCount = rows.Count - settingCount;
        var modified = rows.Count(static r => r.IsModified && r.CanReset);

        // The note wins the line when there is one, because it says something about what just happened; the
        // inventory count is always derivable from the grid itself.
        AdvancedStatus.Text = AdvancedStatusNote
            ?? $"{settingCount} setting{(settingCount == 1 ? string.Empty : "s")}, {modified} changed from default"
                + (detailCount == 0 ? ". " : $", and {detailCount} rows of detail beneath them. ")
                + $"Values are read-only here - change them on the tab named in Where, or in data\\{AppPaths.SettingsFileName} "
                + $"while PasteJump is closed. Rows marked {DataLocationPointer.FileName} live in that file.";

        // One-shot: cleared as it is read, so the next filter keystroke or edit shows the inventory line again.
        AdvancedStatusNote = null;

        AdvancedFilterCue.Visibility = string.IsNullOrEmpty(filter)
            ? Visibility.Visible
            : Visibility.Collapsed;

        AdvancedFilterClear.Visibility = string.IsNullOrEmpty(filter)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// Which tab holds the control for a setting, for the Advanced page's <b>Where</b> column.
    /// <para>
    /// The Advanced page lists every setting and can change none of them, so "edit it on the other tabs" left the
    /// reader to find <em>which</em> of eight tabs among 43 rows. This answers that, and it is also what the filter
    /// box searches - typing <c>appearance</c> lists everything that tab owns.
    /// </para>
    /// <para>
    /// A hand-written table, which is normally the thing this codebase avoids. It cannot be derived: the mapping
    /// from a property to its control is not a naming convention - <c>PasteSettleDelayMs</c> lives in
    /// <c>PasteSettleDelayBox</c> and <c>HistoryPreviewMaxWidth</c> in <c>HistoryPreviewWidthBox</c> - and matching
    /// on the prose labels would be worse. The drift a hand table invites is caught instead:
    /// <c>VerifyEverySettingHasAControl</c> fails when a row has no entry here, so a new setting cannot be added
    /// without one.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> SettingTabs = new(StringComparer.Ordinal)
    {
        // Capture
        [nameof(PasteJumpSettings.MonitorClipboard)] = "Capture",
        [nameof(PasteJumpSettings.StoreImages)] = "Capture",
        [nameof(PasteJumpSettings.AllowDuplicateClips)] = "Capture",
        [nameof(PasteJumpSettings.LimitMaxClips)] = "Capture",
        [nameof(PasteJumpSettings.MaxClips)] = "Capture",

        // History
        [nameof(PasteJumpSettings.RecordHistory)] = "History",
        [nameof(PasteJumpSettings.HistoryRetentionDays)] = "History",
        [nameof(PasteJumpSettings.PreviewMaxChars)] = "History",
        [nameof(PasteJumpSettings.HistoryLoadLimit)] = "History",
        [nameof(PasteJumpSettings.HistoryPreviewMaxWidth)] = "History",
        [nameof(PasteJumpSettings.ClipJoinSeparator)] = "History",
        [nameof(PasteJumpSettings.LegacyImportCompleted)] = "History",

        // Paste Mode
        [nameof(PasteJumpSettings.PreserveClipPosition)] = "Paste Mode",
        [nameof(PasteJumpSettings.OpenSearchImmediately)] = "Paste Mode",
        [nameof(PasteJumpSettings.ResetFormatterOnEntry)] = "Paste Mode",
        [nameof(PasteJumpSettings.DefaultFormatterId)] = "Paste Mode",
        [nameof(PasteJumpSettings.PasteKeystroke)] = "Paste Mode",
        [nameof(PasteJumpSettings.WarnAboutClipboardManagerConflict)] = "Paste Mode",

        // Keys
        [nameof(PasteJumpSettings.PasteModeTriggerKey)] = "Keys",
        [nameof(PasteJumpSettings.PasteModeKeys)] = "Keys",

        // Excluded Apps
        [nameof(PasteJumpSettings.IgnoredProcesses)] = "Excluded Apps",

        // Appearance
        [nameof(PasteJumpSettings.Theme)] = "Appearance",
        // History, not Appearance: it governs the history window's own list, which is also where its second
        // control lives. Moved 2026-08-14.
        [nameof(PasteJumpSettings.GridDensity)] = "History",

        // Advanced is where it is listed and the only place it appears - there is no control for it on any tab,
        // by request. The value is edited in PasteJump.json.
        [nameof(PasteJumpSettings.OverlayDeletedFlashMs)] = "Advanced",
        [nameof(PasteJumpSettings.OverlayPreviewMaxWidth)] = "Appearance",
        [nameof(PasteJumpSettings.OverlayPreviewMaxHeight)] = "Appearance",
        [nameof(PasteJumpSettings.OverlayPreviewChars)] = "Appearance",
        [nameof(PasteJumpSettings.OverlayFontFamily)] = "Appearance",
        [nameof(PasteJumpSettings.OverlayFontSize)] = "Appearance",
        [nameof(PasteJumpSettings.OverlayX)] = "Appearance",
        [nameof(PasteJumpSettings.OverlayY)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlayKeyHint)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlayPosition)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlayTextDetails)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlayTextSize)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlayImageDetails)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlayImageSize)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlayFileDetails)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlayFileSize)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlayFormatter)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlayTags)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlaySource)] = "Appearance",
        [nameof(PasteJumpSettings.ShowOverlayPinned)] = "Appearance",
        [nameof(PasteJumpSettings.ShowCopyNotification)] = "Appearance",
        [nameof(PasteJumpSettings.CopyNotificationMs)] = "Appearance",
        [nameof(PasteJumpSettings.BeepOnCopy)] = "Appearance",
        [nameof(PasteJumpSettings.BeepFrequencyHz)] = "Appearance",
        [nameof(PasteJumpSettings.BeepDurationMs)] = "Appearance",

        // System
        [nameof(PasteJumpSettings.RunAtLogon)] = "System",
        [nameof(PasteJumpSettings.HistoryHotkey)] = "System",
        [nameof(PasteJumpSettings.TrayLeftClick)] = "System",
        [nameof(PasteJumpSettings.PasteSettleDelayMs)] = "System",
        [nameof(PasteJumpSettings.PasteSettleDelayPerApp)] = "System",
        [nameof(PasteJumpSettings.TextEditor)] = "System",
        [nameof(PasteJumpSettings.ImageEditor)] = "System",

        // The two data locations, which are not settings and live in their own file. Named by the tab that holds
        // their controls all the same, since that is what the column is for.
        ["ClipsLocation"] = "System",
        ["SettingsLocation"] = "System",
    };

    /// <summary>
    /// The tab that owns a setting, or empty when nothing has recorded one. Empty rather than a guess, so the
    /// harness can tell the difference between "on the System tab" and "nobody said".
    /// </summary>
    private static string TabFor(string key)
        => SettingTabs.TryGetValue(key, out var tab) ? tab : string.Empty;

    /// <summary>Test hook: the tab recorded for each row, so the harness can insist every row has one.</summary>
    public static string TabForSmokeTest(string key) => TabFor(key);

    private void OnAdvancedFilterChanged(object sender, TextChangedEventArgs e) => RefreshAdvanced();

    /// <summary>
    /// Puts one setting back to its default, leaving every other pending edit alone.
    /// <para>
    /// Reflection over the property named by the row rather than a switch over control names. A hand-written
    /// map would be one more list to keep in step with <see cref="PasteJumpSettings"/>, and a setting missing
    /// from it would have a Reset button that silently did nothing - the same drift the Advanced page exists to
    /// avoid. Nothing is written here: this edits the pending values, so Cancel still abandons it.
    /// </para>
    /// </summary>
    private void OnResetSettingClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SettingRow row)
        {
            ResetSetting(row);
        }
    }

    private void ResetSetting(SettingRow row)
    {
        // The two data locations are not settings - they live in their own file - so they are reset by moving
        // their combo rather than by touching a settings object.
        if (string.Equals(row.Key, "ClipsLocation", StringComparison.Ordinal))
        {
            SelectLocation(ClipsLocationCombo, DataLocation.ApplicationFolder);
            return;
        }

        if (string.Equals(row.Key, "SettingsLocation", StringComparison.Ordinal))
        {
            SelectLocation(SettingsLocationCombo, DataLocation.ApplicationFolder);
            return;
        }

        // Resetting works on the pending values, so they have to be readable first. Refusing while a box holds
        // a half-typed number is deliberate: the alternative is silently discarding the user's other edits.
        if (!TryBuild(out var pending, out var error))
        {
            ValidationText.Text = $"{error} Fix that first, then reset {row.Name}.";
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        var property = typeof(PasteJumpSettings).GetProperty(row.Key);

        if (property?.SetMethod is null)
        {
            return;
        }

        property.SetValue(pending, property.GetValue(new PasteJumpSettings()));

        // Normalise after the write, because settings are not all independent: clearing one half of the overlay
        // position has to clear the other, and only Normalise knows that.
        pending.Normalise();

        ValidationText.Visibility = Visibility.Collapsed;
        ShowValues(pending, SelectedClipsLocation, SelectedSettingsLocation);

        AdvancedStatusNote = $"{row.Name} reset to {row.Default}. Nothing is saved until you press OK or Apply.";
        RefreshAdvanced();
    }

    /// <summary>
    /// Puts every setting back to its default, including the two data locations.
    /// <para>
    /// Confirmed even though it is reversible with Cancel: it discards a configuration that may have taken a
    /// while to arrive at, and the excluded-apps list is the kind of thing nobody remembers re-entering.
    /// </para>
    /// </summary>
    private void OnResetAllClicked(object sender, RoutedEventArgs e)
    {
        if (!MessageDialog.Confirm(
                "Every setting goes back to the value a fresh install would have, including the excluded "
                    + "applications list and both data locations.\n\nNothing is saved until you press OK or "
                    + "Apply, so Cancel still abandons it. Your clips and history are not touched.",
                headline: "Reset all settings to their defaults?",
                title: "PasteJump - settings",
                owner: this))
        {
            return;
        }

        ResetAll();
    }

    private void ResetAll()
    {
        ValidationText.Visibility = Visibility.Collapsed;

        ShowValues(new PasteJumpSettings(), DataLocation.ApplicationFolder, DataLocation.ApplicationFolder);
        RefreshLocationHints();

        AdvancedStatusNote = "All settings reset. Nothing is saved until you press OK or Apply.";
        RefreshAdvanced();
    }

    /// <summary>
    /// Test hook: runs both reset paths against the rows actually on screen.
    /// <para>
    /// It goes through <see cref="ResetSetting"/> and <see cref="ResetAll"/> rather than reimplementing them,
    /// which is the point - what can break here is the wiring between a grid row and a settings property, and
    /// only exercising the real path can catch that. The confirmation is skipped because it is modal and would
    /// block the harness for ever; nothing is written either way, since this dialog is never accepted.
    /// </para>
    /// </summary>
    public void ExerciseResetsForSmokeTest()
    {
        if (AdvancedGrid.ItemsSource is IEnumerable<SettingRow> rows)
        {
            // A modified row, so the branch that actually writes something is the one taken.
            foreach (var row in rows.Where(static r => r.IsModified))
            {
                ResetSetting(row);
            }
        }

        ResetAll();
    }

    /// <summary>
    /// Test hook: reads the controls back exactly as OK does, without saving anything.
    /// <para>
    /// This is what lets the harness prove that <em>every</em> setting has a working control. Loading a settings
    /// object with nothing at its default and then building one back out has to return what went in - and a setting
    /// missing from <c>ShowValues</c>, missing from <c>TryBuild</c>, or with no control at all each fails that
    /// round trip in a way nothing else notices. It is the "add it in three places" rule, checked rather than
    /// remembered.
    /// </para>
    /// </summary>
    public bool TryBuildForSmokeTest(out PasteJumpSettings? settings, out string? error)
        => TryBuild(out settings, out error);

    /// <summary>
    /// One-shot line shown above the Advanced grid after a reset, cleared by the next refresh. A transient note
    /// rather than a dialog: the grid itself already shows the new value, so this only has to say that nothing
    /// has been written yet.
    /// </summary>
    private string? AdvancedStatusNote { get; set; }

    private static void SelectLocation(ComboBox combo, DataLocation location) => combo.SelectedItem =
        DataLocationChoices.First(c => c.Location == location).Label;

    /// <summary>
    /// Parses a screen coordinate that is allowed to be blank. Blank yields null and succeeds; anything that is
    /// neither blank nor a coordinate fails, so a typo is reported rather than quietly meaning "not set".
    /// </summary>
    private static bool TryParseOptionalCoordinate(string? text, out int? value)
    {
        value = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var parsed)
            || parsed is < -32_768 or > 32_767)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private bool TryBuild(out PasteJumpSettings settings, out string error)
    {
        settings = new PasteJumpSettings();
        error = string.Empty;

        // CARRIED, not collected. TryBuild starts from a fresh defaults object and writes each field from its
        // control, so a setting with no control anywhere would silently revert to its default on every OK - which
        // is what VerifyEverySettingHasAControl exists to catch. OverlayDeletedFlashMs is deliberately Advanced-only
        // (it appears in the inventory, and is edited in PasteJump.json), so it is copied through by hand.
        settings.OverlayDeletedFlashMs = _baseline.OverlayDeletedFlashMs;

        if (!int.TryParse(MaxClipsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var maxClips)
            || maxClips < 1)
        {
            error = "Maximum clips must be a whole number of at least 1.";
            return false;
        }

        if (!int.TryParse(RetentionDaysBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var retention)
            || retention < 0)
        {
            error = "Days of history must be zero or a positive whole number.";
            return false;
        }

        if (!int.TryParse(CopyNotificationMsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var toastMs)
            || !SettingsBounds.CopyNotificationMs.Admits(toastMs))
        {
            error = SettingsBounds.CopyNotificationMs.Refuse("Notification duration", "milliseconds");
            return false;
        }

        if (!int.TryParse(PasteSettleDelayBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var settleMs)
            || !SettingsBounds.PasteSettleDelayMs.Admits(settleMs))
        {
            error = SettingsBounds.PasteSettleDelayMs.Refuse("Pause before pasting", "milliseconds");
            return false;
        }

        if (!int.TryParse(BeepFrequencyBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var beepHz)
            || !SettingsBounds.BeepFrequencyHz.Admits(beepHz))
        {
            error = SettingsBounds.BeepFrequencyHz.Refuse("Beep pitch", "hertz");
            return false;
        }

        // The bounds match PasteJumpSettings.Normalise. Rejected here rather than clamped there, because a
        // number silently changing to 1400 after OK reads as the box not having taken the value.
        if (!int.TryParse(PreviewWidthBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var previewWidth)
            || !SettingsBounds.OverlayPreviewMaxWidth.Admits(previewWidth))
        {
            error = SettingsBounds.OverlayPreviewMaxWidth.Refuse("Image preview width", "pixels");
            return false;
        }

        if (!int.TryParse(PreviewHeightBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var previewHeight)
            || !SettingsBounds.OverlayPreviewMaxHeight.Admits(previewHeight))
        {
            error = SettingsBounds.OverlayPreviewMaxHeight.Refuse("Image preview height", "pixels");
            return false;
        }

        if (!int.TryParse(OverlayFontSizeBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var overlayFontSize)
            || !SettingsBounds.OverlayFontSize.Admits(overlayFontSize))
        {
            error = SettingsBounds.OverlayFontSize.Refuse("Overlay text size");
            return false;
        }

        if (!int.TryParse(OverlayPreviewCharsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var overlayChars)
            || !SettingsBounds.OverlayPreviewChars.Admits(overlayChars))
        {
            error = SettingsBounds.OverlayPreviewChars.Refuse("Characters of text shown in the overlay");
            return false;
        }

        if (!int.TryParse(BeepDurationBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var beepMs)
            || !SettingsBounds.BeepDurationMs.Admits(beepMs))
        {
            error = SettingsBounds.BeepDurationMs.Refuse("Beep length", "milliseconds");
            return false;
        }

        if (!int.TryParse(PreviewMaxCharsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var previewChars)
            || !SettingsBounds.PreviewMaxChars.Admits(previewChars))
        {
            error = SettingsBounds.PreviewMaxChars.Refuse("Characters kept per history entry");
            return false;
        }

        if (!int.TryParse(HistoryLoadLimitBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var historyLimit)
            || !SettingsBounds.HistoryLoadLimit.Admits(historyLimit))
        {
            error = SettingsBounds.HistoryLoadLimit.Refuse("Rows the history window loads");
            return false;
        }

        if (!int.TryParse(HistoryPreviewWidthBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var historyPreviewWidth)
            || !SettingsBounds.HistoryPreviewMaxWidth.Admits(historyPreviewWidth))
        {
            error = SettingsBounds.HistoryPreviewMaxWidth.Refuse("History preview image width", "pixels");
            return false;
        }

        // Negative coordinates are legal: a monitor placed left of or above the primary one has them.
        if (!TryParseOptionalCoordinate(OverlayXBox.Text, out var overlayX)
            || !TryParseOptionalCoordinate(OverlayYBox.Text, out var overlayY))
        {
            error = "The overlay position must be whole numbers between -32768 and 32767, or empty.";
            return false;
        }

        if (overlayX is null != overlayY is null)
        {
            error = "Give the overlay position both an x and a y, or leave both empty to follow the caret.";
            return false;
        }

        // Rejected rather than silently blanked. A hotkey the user typed and that then vanished from the
        // box reads as the dialog losing input, whereas being told what is wrong is actionable.
        var hotkeyText = HistoryHotkeyBox.Text?.Trim() ?? string.Empty;

        if (hotkeyText.Length > 0 && !HotkeySpec.TryParse(hotkeyText, out _))
        {
            error = HotkeySpec.ParseOrNone(hotkeyText).IsSet
                ? "The history shortcut needs at least one of Ctrl, Alt, Shift or Win."
                : "The history shortcut is not one PasteJump can register. Try something like Ctrl+Shift+H.";

            return false;
        }

        var selectedName = DefaultFormatterCombo.SelectedItem as string;
        var formatter = _formatters.All.FirstOrDefault(f =>
            string.Equals(f.DisplayName, selectedName, StringComparison.Ordinal));

        settings.MonitorClipboard = MonitorClipboardCheck.IsChecked == true;
        settings.StoreImages = StoreImagesCheck.IsChecked == true;
        settings.AllowDuplicateClips = AllowDuplicatesCheck.IsChecked == true;
        settings.LimitMaxClips = LimitMaxClipsCheck.IsChecked == true;
        settings.MaxClips = maxClips;

        // Already normalised on the way into the list, but run again on the way out so a value hand-edited
        // into PasteJump.json and then loaded here cannot be written back in a shape capture never matches.
        settings.IgnoredProcesses = ExcludedApps.NormaliseAll(_excluded);

        settings.RecordHistory = RecordHistoryCheck.IsChecked == true;
        settings.HistoryRetentionDays = retention;

        settings.PreserveClipPosition = PreservePositionCheck.IsChecked == true;
        settings.OpenSearchImmediately = OpenSearchCheck.IsChecked == true;
        settings.ResetFormatterOnEntry = ResetFormatterCheck.IsChecked == true;
        // Falls back to the canonical default id rather than null, so a saved value always matches what
        // a fresh install would hold for the same choice.
        settings.DefaultFormatterId = formatter?.Id ?? FormatterRegistry.DefaultId;

        settings.Theme = ThemeCombo.SelectedIndex >= 0 && ThemeCombo.SelectedIndex < _themeNames.Count
            ? _themeNames[ThemeCombo.SelectedIndex]
            : ThemeNames.System;

        var densityLabel = DensityCombo.SelectedItem as string;
        settings.GridDensity = DensityChoices
            .FirstOrDefault(c => string.Equals(c.Label, densityLabel, StringComparison.Ordinal))
            .Density;

        settings.ShowCopyNotification = ShowCopyNotificationCheck.IsChecked == true;
        settings.CopyNotificationMs = toastMs;
        settings.PasteSettleDelayMs = settleMs;

        var keystrokeLabel = PasteKeystrokeCombo.SelectedItem as string;
        settings.PasteKeystroke = PasteKeystrokeChoices
            .FirstOrDefault(c => string.Equals(c.Label, keystrokeLabel, StringComparison.Ordinal))
            .Keystroke;

        settings.WarnAboutClipboardManagerConflict = WarnAboutConflictCheck.IsChecked == true;

        settings.PasteModeTriggerKey = TriggerKey
            .Normalise(TriggerKeyCombo.SelectedItem as string)
            .ToString();

        var keyChoices = ReadPasteKeyChoices();
        var keyError = PasteKeyMap.Validate(keyChoices, TriggerKey.Normalise(settings.PasteModeTriggerKey));

        if (keyError is not null)
        {
            // Refused rather than resolved. Two actions on one letter is not a preference half of which could be
            // honoured - whichever the lookup wrote last would win, silently - so the clash is named and the
            // dialog stays open on the tab that owns it.
            MessageDialog.Warn(keyError, owner: this, headline: "Those paste-mode keys clash");
            SelectTabContaining(PasteKeyRows);
            return false;
        }

        settings.PasteModeKeys = PasteKeyMap.FromChoices(keyChoices).ToSettingsString();

        // Canonicalised on the way in, so "control+shift+h" is stored the same way the combo would render
        // it and the Advanced tab does not report a spurious difference from the default.
        settings.HistoryHotkey = HotkeySpec.ParseOrNone(hotkeyText).ToString();

        settings.BeepOnCopy = BeepOnCopyCheck.IsChecked == true;
        settings.BeepFrequencyHz = beepHz;

        settings.OverlayPreviewMaxWidth = previewWidth;
        settings.OverlayPreviewMaxHeight = previewHeight;
        settings.OverlayPreviewChars = overlayChars;
        settings.OverlayFontSize = overlayFontSize;
        settings.OverlayFontFamily = OverlayFontFamilyCombo.SelectedItem is string font && font != DefaultFontLabel
            ? font
            : string.Empty;
        settings.ShowOverlayKeyHint = ShowKeyHintCheck.IsChecked == true;
        settings.ShowOverlayPosition = ShowPositionCheck.IsChecked == true;
        settings.ShowOverlayTextDetails = TextDetailsCheck.IsChecked == true;
        settings.ShowOverlayTextSize = TextSizeCheck.IsChecked == true;
        settings.ShowOverlayImageDetails = ImageDetailsCheck.IsChecked == true;
        settings.ShowOverlayImageSize = ImageSizeCheck.IsChecked == true;
        settings.ShowOverlayFileDetails = FileDetailsCheck.IsChecked == true;
        settings.ShowOverlayFileSize = FileSizeCheck.IsChecked == true;
        settings.ShowOverlayFormatter = ShowFormatterCheck.IsChecked == true;
        settings.ShowOverlayTags = ShowTagsCheck.IsChecked == true;
        settings.ShowOverlaySource = ShowSourceCheck.IsChecked == true;
        settings.ShowOverlayPinned = ShowPinnedCheck.IsChecked == true;
        settings.BeepDurationMs = beepMs;
        settings.PreviewMaxChars = previewChars;
        settings.HistoryLoadLimit = historyLimit;
        settings.HistoryPreviewMaxWidth = historyPreviewWidth;

        // Not validated, because there is nothing to validate it against - any text is a legal separator. An
        // empty box is corrected to the default by Normalise rather than refused here: emptying it is a plausible
        // accident, and the alternative reading, "join with nothing", produces one unreadable run of text.
        settings.ClipJoinSeparator = string.IsNullOrEmpty(ClipJoinSeparatorBox.Text)
            ? ClipJoiner.DefaultSeparator
            : ClipJoinSeparatorBox.Text;

        // Carried through explicitly. Before these had controls they were simply never assigned here, so a
        // position set by hand in PasteJump.json was silently discarded by opening this dialog and clicking OK.
        settings.OverlayX = overlayX;
        settings.OverlayY = overlayY;

        var trayLabel = TrayLeftClickCombo.SelectedItem as string;
        settings.TrayLeftClick = TrayLeftClickChoices
            .FirstOrDefault(c => string.Equals(c.Label, trayLabel, StringComparison.Ordinal))
            .Action;

        if (!TryReadPasteDelays(out var perAppDelays, out var delayError))
        {
            error = delayError;
            return false;
        }

        settings.PasteSettleDelayPerApp = perAppDelays.ToSettingsString();

        settings.RunAtLogon = RunAtLogonCheck.IsChecked == true;
        settings.TextEditor = TextEditorBox.Text;
        settings.ImageEditor = ImageEditorBox.Text;

        // Carried forward, not surfaced: re-offering the legacy import after every settings change
        // would be maddening.
        // From the control now rather than carried forward from the baseline. It was the only setting with no
        // control at all, which meant editing PasteJump.json by hand was the only way to be asked again.
        settings.LegacyImportCompleted = OfferLegacyImportCheck.IsChecked != true;

        settings.Normalise();
        return true;
    }
    /// <summary>What the font combo calls "no font chosen". Not a font name, so it cannot collide with one.</summary>
    private const string DefaultFontLabel = "(default)";

    /// <summary>
    /// A family's name in the user's own language where it has one, falling back to its invariant name.
    /// </summary>
    private static string NameOf(System.Windows.Media.FontFamily family)
    {
        var names = family.FamilyNames;

        foreach (var culture in new[] { System.Globalization.CultureInfo.CurrentUICulture, System.Globalization.CultureInfo.InvariantCulture })
        {
            var key = System.Windows.Markup.XmlLanguage.GetLanguage(culture.IetfLanguageTag);

            if (names.TryGetValue(key, out var localised) && localised.Length > 0)
            {
                return localised;
            }
        }

        // Source is the last resort and can carry a URI for a private font, so take the family segment only.
        var source = family.Source ?? string.Empty;
        var hash = source.LastIndexOf('#');

        return hash >= 0 ? source[(hash + 1)..] : source;
    }

    /// <summary>
    /// The combo item standing for a saved family name, adding it to the list when this machine lacks the font.
    /// </summary>
    private string SelectableFontName(string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved))
        {
            return DefaultFontLabel;
        }

        var name = saved.Trim();

        foreach (var item in OverlayFontFamilyCombo.Items)
        {
            if (item is string existing && string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }
        }

        OverlayFontFamilyCombo.Items.Add(name);

        return name;
    }

}
