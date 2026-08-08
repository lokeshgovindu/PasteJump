using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PasteJump.App.Services;
using PasteJump.Core;
using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;

namespace PasteJump.App.Views;

/// <summary>
/// Settings editor. Reads a copy of the settings and raises <see cref="SettingsApplied"/> with a
/// fully-populated object, so a cancelled dialog cannot leave partial changes behind.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// Theme options as shown in the combo box. A label-to-enum table rather than binding the enum
    /// directly, so the wording can say what "System" actually means.
    /// </summary>
    private static readonly (AppTheme Theme, string Label)[] ThemeChoices =
    [
        (AppTheme.Light, "Light"),
        (AppTheme.Dark, "Dark"),
        (AppTheme.System, "Same as Windows"),
    ];

    /// <summary>
    /// Paste-chord options. Labelled as the user would name the keys, with the reason for the second one
    /// carried in the combo's tooltip rather than the label.
    /// </summary>
    private static readonly (PasteKeystroke Keystroke, string Label)[] PasteKeystrokeChoices =
    [
        (PasteKeystroke.CtrlV, "Ctrl+V"),
        (PasteKeystroke.ShiftInsert, "Shift+Insert"),
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

    /// <summary>
    /// The excluded-application list as the user is editing it.
    /// <para>
    /// An <see cref="ObservableCollection{T}"/> bound to the ListBox, so adding and removing shows up without
    /// rebuilding <c>ItemsSource</c> - which would lose the selection every time and make Remove feel broken
    /// on a multiple selection.
    /// </para>
    /// </summary>
    private readonly ObservableCollection<string> _excluded = [];

    public SettingsWindow(
        PasteJumpSettings settings,
        FormatterRegistry formatters,
        DataLocation clipsLocation = DataLocation.ApplicationFolder,
        DataLocation settingsLocation = DataLocation.ApplicationFolder)
    {
        _baseline = settings;
        _formatters = formatters;
        _baselineClipsLocation = clipsLocation;
        _baselineSettingsLocation = settingsLocation;

        InitializeComponent();
        Load();
        RefreshAdvanced();
    }

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
    public event Action<DataLocation, DataLocation>? DataLocationChangeRequested;

    /// <summary>
    /// Raised when the user asks to import Clipjump's history.
    /// <para>
    /// Handled by the host rather than here because the importer needs the clip store, which this dialog has
    /// no business holding. Fires immediately rather than on OK: it is an action, not a setting, and nothing
    /// about it is pending until the dialog is accepted.
    /// </para>
    /// </summary>
    public event Action? LegacyImportRequested;

    private void Load()
    {
        MonitorClipboardCheck.IsChecked = _baseline.MonitorClipboard;
        StoreImagesCheck.IsChecked = _baseline.StoreImages;
        AllowDuplicatesCheck.IsChecked = _baseline.AllowDuplicateClips;
        LimitMaxClipsCheck.IsChecked = _baseline.LimitMaxClips;
        MaxClipsBox.Text = _baseline.MaxClips.ToString(CultureInfo.CurrentCulture);
        RefreshMaxClipsEnabled();

        foreach (var name in ExcludedApps.NormaliseAll(_baseline.IgnoredProcesses))
        {
            _excluded.Add(name);
        }

        ExcludedList.ItemsSource = _excluded;
        RefreshExcludedStatus();

        RecordHistoryCheck.IsChecked = _baseline.RecordHistory;
        RetentionDaysBox.Text = _baseline.HistoryRetentionDays.ToString(CultureInfo.CurrentCulture);

        PreservePositionCheck.IsChecked = _baseline.PreserveClipPosition;
        OpenSearchCheck.IsChecked = _baseline.OpenSearchImmediately;
        ResetFormatterCheck.IsChecked = _baseline.ResetFormatterOnEntry;

        foreach (var formatter in _formatters.All)
        {
            DefaultFormatterCombo.Items.Add(formatter.DisplayName);
        }

        DefaultFormatterCombo.SelectedItem = _formatters.Resolve(_baseline.DefaultFormatterId).DisplayName;

        foreach (var choice in PasteKeystrokeChoices)
        {
            PasteKeystrokeCombo.Items.Add(choice.Label);
        }

        PasteKeystrokeCombo.SelectedItem = PasteKeystrokeChoices
            .First(c => c.Keystroke == _baseline.PasteKeystroke).Label;

        WarnAboutConflictCheck.IsChecked = _baseline.WarnAboutClipboardManagerConflict;

        foreach (var key in TriggerKey.Available)
        {
            TriggerKeyCombo.Items.Add(key.ToString());
        }

        TriggerKeyCombo.SelectedItem = TriggerKey.Normalise(_baseline.PasteModeTriggerKey).ToString();

        HistoryHotkeyBox.Text = _baseline.HistoryHotkey;

        foreach (var choice in ThemeChoices)
        {
            ThemeCombo.Items.Add(choice.Label);
        }

        ThemeCombo.SelectedItem = ThemeChoices.First(c => c.Theme == _baseline.Theme).Label;

        foreach (var choice in DensityChoices)
        {
            DensityCombo.Items.Add(choice.Label);
        }

        DensityCombo.SelectedItem = DensityChoices.First(c => c.Density == _baseline.GridDensity).Label;

        VersionText.Text = $"PasteJump {AppVersion.Current}";

        ShowCopyNotificationCheck.IsChecked = _baseline.ShowCopyNotification;
        CopyNotificationMsBox.Text = _baseline.CopyNotificationMs.ToString(CultureInfo.CurrentCulture);
        PasteSettleDelayBox.Text = _baseline.PasteSettleDelayMs.ToString(CultureInfo.CurrentCulture);

        BeepOnCopyCheck.IsChecked = _baseline.BeepOnCopy;
        BeepFrequencyBox.Text = _baseline.BeepFrequencyHz.ToString(CultureInfo.CurrentCulture);

        // Reflect the real state of the shortcut, not just what settings claim. The user may have
        // deleted it from the Startup folder by hand since the last run.
        RunAtLogonCheck.IsChecked = _baseline.RunAtLogon || StartupShortcut.Exists;
        TextEditorBox.Text = _baseline.TextEditor;
        ImageEditorBox.Text = _baseline.ImageEditor;

        foreach (var choice in DataLocationChoices)
        {
            ClipsLocationCombo.Items.Add(choice.Label);
            SettingsLocationCombo.Items.Add(choice.Label);
        }

        ClipsLocationCombo.SelectedItem = DataLocationChoices
            .First(c => c.Location == _baselineClipsLocation).Label;

        SettingsLocationCombo.SelectedItem = DataLocationChoices
            .First(c => c.Location == _baselineSettingsLocation).Label;
    }

    /// <summary>Clips location currently picked, which may differ from the one in force.</summary>
    private DataLocation SelectedClipsLocation => LocationIn(ClipsLocationCombo);

    /// <summary>Settings location currently picked, which may differ from the one in force.</summary>
    private DataLocation SelectedSettingsLocation => LocationIn(SettingsLocationCombo);

    private static DataLocation LocationIn(ComboBox combo) => DataLocationChoices
        .FirstOrDefault(c => string.Equals(c.Label, combo.SelectedItem as string, StringComparison.Ordinal))
        .Location;

    /// <summary>
    /// Both combos share this handler. It refreshes both labels rather than only the one that changed,
    /// which keeps it correct regardless of which control raised the event.
    /// </summary>
    private void OnDataLocationChanged(object sender, SelectionChangedEventArgs e) => RefreshLocationHints();

    /// <summary>
    /// Spells out the chord and warns when it is no longer the original's. Worth saying out loud: changing
    /// this changes the one gesture the whole application is built around, and muscle memory will not have
    /// been consulted.
    /// </summary>
    private void OnTriggerKeyChanged(object sender, SelectionChangedEventArgs e)
    {
        var key = TriggerKey.Normalise(TriggerKeyCombo.SelectedItem as string);

        TriggerKeyHintText.Text = key == TriggerKey.Default
            ? $"{TriggerKey.Describe(key)} opens paste mode, and tapping {key} again steps further back."
            : $"{TriggerKey.Describe(key)} opens paste mode. Ctrl+V goes back to being an ordinary paste.";
    }

    private void RefreshLocationHints()
    {
        ClipsLocationPathText.Text = Describe(SelectedClipsLocation, _baselineClipsLocation);
        SettingsLocationPathText.Text = Describe(SelectedSettingsLocation, _baselineSettingsLocation);

        // Says so up front rather than only in the confirmation prompt, so the restart is not a surprise
        // discovered after clicking OK.
        static string Describe(DataLocation selected, DataLocation baseline)
        {
            var path = Path.Combine(AppPaths.RootFor(selected), "data");

            return selected == baseline ? path : path + "   (restart required)";
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

        ValidationText.Visibility = Visibility.Collapsed;

        SettingsApplied?.Invoke(updated);

        var clips = SelectedClipsLocation;
        var settings = SelectedSettingsLocation;

        // Raised after SettingsApplied, so the settings are saved to their current location before
        // anything starts moving them. The handler may restart the process, which ends this method.
        if (clips != _baselineClipsLocation || settings != _baselineSettingsLocation)
        {
            DataLocationChangeRequested?.Invoke(clips, settings);
        }

        // The baseline moves to what is now in force. Without this, a second Apply would re-raise the
        // location change and prompt to move data that has already been moved.
        _baseline = updated;
        _baselineClipsLocation = clips;
        _baselineSettingsLocation = settings;

        RefreshLocationHints();
        RefreshAdvanced();
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

        var rows = SettingsInspector.Describe(source, SelectedClipsLocation, SelectedSettingsLocation)
            .Where(r => string.IsNullOrWhiteSpace(filter)
                || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        AdvancedGrid.ItemsSource = rows;

        var modified = rows.Count(static r => r.IsModified);

        AdvancedStatus.Text =
            $"{rows.Count} setting{(rows.Count == 1 ? string.Empty : "s")}, {modified} changed from default. " +
            $"Read-only here - edit on the other tabs, or in data\\{AppPaths.SettingsFileName} while " +
            "PasteJump is closed. " +
            $"Rows marked {DataLocationPointer.FileName} live in that file instead, beside PasteJump.exe.";

        AdvancedFilterCue.Visibility = string.IsNullOrEmpty(filter)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnAdvancedFilterChanged(object sender, TextChangedEventArgs e) => RefreshAdvanced();

    private bool TryBuild(out PasteJumpSettings settings, out string error)
    {
        settings = new PasteJumpSettings();
        error = string.Empty;

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
            || toastMs is < 250 or > 10_000)
        {
            error = "Notification duration must be between 250 and 10000 milliseconds.";
            return false;
        }

        if (!int.TryParse(PasteSettleDelayBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var settleMs)
            || settleMs is < 0 or > 500)
        {
            error = "Pause before pasting must be between 0 and 500 milliseconds.";
            return false;
        }

        if (!int.TryParse(BeepFrequencyBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var beepHz)
            || beepHz is < 37 or > 32_767)
        {
            error = "Beep pitch must be between 37 and 32767 hertz.";
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

        var themeLabel = ThemeCombo.SelectedItem as string;
        settings.Theme = ThemeChoices
            .FirstOrDefault(c => string.Equals(c.Label, themeLabel, StringComparison.Ordinal))
            .Theme;

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

        // Canonicalised on the way in, so "control+shift+h" is stored the same way the combo would render
        // it and the Advanced tab does not report a spurious difference from the default.
        settings.HistoryHotkey = HotkeySpec.ParseOrNone(hotkeyText).ToString();

        settings.BeepOnCopy = BeepOnCopyCheck.IsChecked == true;
        settings.BeepFrequencyHz = beepHz;

        settings.RunAtLogon = RunAtLogonCheck.IsChecked == true;
        settings.TextEditor = TextEditorBox.Text;
        settings.ImageEditor = ImageEditorBox.Text;

        // Carried forward, not surfaced: re-offering the legacy import after every settings change
        // would be maddening.
        settings.LegacyImportCompleted = _baseline.LegacyImportCompleted;

        settings.Normalise();
        return true;
    }
}
