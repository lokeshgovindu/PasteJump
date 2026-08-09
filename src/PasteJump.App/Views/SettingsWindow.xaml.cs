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

        RefreshApplyState();
    }

    private void OnAnyEdit(object sender, RoutedEventArgs e) => RefreshApplyState();

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
        if (SelectedClipsLocation != _baselineClipsLocation
            || SelectedSettingsLocation != _baselineSettingsLocation)
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

        foreach (var choice in ThemeChoices)
        {
            ThemeCombo.Items.Add(choice.Label);
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

        PreservePositionCheck.IsChecked = source.PreserveClipPosition;
        OpenSearchCheck.IsChecked = source.OpenSearchImmediately;
        ResetFormatterCheck.IsChecked = source.ResetFormatterOnEntry;

        DefaultFormatterCombo.SelectedItem = _formatters.Resolve(source.DefaultFormatterId).DisplayName;

        PasteKeystrokeCombo.SelectedItem = PasteKeystrokeChoices
            .First(c => c.Keystroke == source.PasteKeystroke).Label;

        WarnAboutConflictCheck.IsChecked = source.WarnAboutClipboardManagerConflict;

        TriggerKeyCombo.SelectedItem = TriggerKey.Normalise(source.PasteModeTriggerKey).ToString();

        HistoryHotkeyBox.Text = source.HistoryHotkey;

        ThemeCombo.SelectedItem = ThemeChoices.First(c => c.Theme == source.Theme).Label;
        DensityCombo.SelectedItem = DensityChoices.First(c => c.Density == source.GridDensity).Label;

        ShowCopyNotificationCheck.IsChecked = source.ShowCopyNotification;
        CopyNotificationMsBox.Text = source.CopyNotificationMs.ToString(CultureInfo.CurrentCulture);
        PasteSettleDelayBox.Text = source.PasteSettleDelayMs.ToString(CultureInfo.CurrentCulture);

        BeepOnCopyCheck.IsChecked = source.BeepOnCopy;
        BeepFrequencyBox.Text = source.BeepFrequencyHz.ToString(CultureInfo.CurrentCulture);
        BeepDurationBox.Text = source.BeepDurationMs.ToString(CultureInfo.CurrentCulture);

        PreviewWidthBox.Text = source.OverlayPreviewMaxWidth.ToString(CultureInfo.CurrentCulture);
        PreviewHeightBox.Text = source.OverlayPreviewMaxHeight.ToString(CultureInfo.CurrentCulture);
        OverlayPreviewCharsBox.Text = source.OverlayPreviewChars.ToString(CultureInfo.CurrentCulture);

        // Empty rather than "0" for "not set". Zero is a legal screen coordinate, so using it as the sentinel
        // would make the top-left corner unreachable.
        OverlayXBox.Text = source.OverlayX?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
        OverlayYBox.Text = source.OverlayY?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

        // Deliberately just the setting. Load reconciles it with the Startup folder afterwards, which is right
        // for opening the dialog and wrong for a reset: resetting means "go back to not starting at logon", and
        // a box that stayed ticked because the shortcut is still there would read as the reset being ignored.
        RunAtLogonCheck.IsChecked = source.RunAtLogon;
        TextEditorBox.Text = source.TextEditor;
        ImageEditorBox.Text = source.ImageEditor;

        ClipsLocationCombo.SelectedItem = DataLocationChoices
            .First(c => c.Location == clipsLocation).Label;

        SettingsLocationCombo.SelectedItem = DataLocationChoices
            .First(c => c.Location == settingsLocation).Label;
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
        => OpenDataFolder(SelectedClipsLocation);

    private void OnOpenSettingsFolder(object sender, MouseButtonEventArgs e)
        => OpenDataFolder(SelectedSettingsLocation);

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
    private void OpenDataFolder(DataLocation location)
    {
        var path = Path.Combine(AppPaths.RootFor(location), "data");

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

        var rows = SettingsInspector.Describe(source, SelectedClipsLocation, SelectedSettingsLocation)
            .Where(r => string.IsNullOrWhiteSpace(filter)
                || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        AdvancedGrid.ItemsSource = rows;

        var modified = rows.Count(static r => r.IsModified);

        // The note wins the line when there is one, because it says something about what just happened; the
        // inventory count is always derivable from the grid itself.
        AdvancedStatus.Text = AdvancedStatusNote
            ?? $"{rows.Count} setting{(rows.Count == 1 ? string.Empty : "s")}, {modified} changed from default. "
                + $"Values are read-only here - edit them on the other tabs, or in data\\{AppPaths.SettingsFileName} "
                + $"while PasteJump is closed. Rows marked {DataLocationPointer.FileName} live in that file.";

        // One-shot: cleared as it is read, so the next filter keystroke or edit shows the inventory line again.
        AdvancedStatusNote = null;

        AdvancedFilterCue.Visibility = string.IsNullOrEmpty(filter)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

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

        // The bounds match PasteJumpSettings.Normalise. Rejected here rather than clamped there, because a
        // number silently changing to 1400 after OK reads as the box not having taken the value.
        if (!int.TryParse(PreviewWidthBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var previewWidth)
            || previewWidth is < 120 or > 1400)
        {
            error = "Image preview width must be between 120 and 1400 pixels.";
            return false;
        }

        if (!int.TryParse(PreviewHeightBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var previewHeight)
            || previewHeight is < 80 or > 900)
        {
            error = "Image preview height must be between 80 and 900 pixels.";
            return false;
        }

        if (!int.TryParse(OverlayPreviewCharsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var overlayChars)
            || overlayChars is < 40 or > 4_000)
        {
            error = "Characters of text shown in the overlay must be between 40 and 4000.";
            return false;
        }

        if (!int.TryParse(BeepDurationBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var beepMs)
            || beepMs is < 20 or > 2_000)
        {
            error = "Beep length must be between 20 and 2000 milliseconds.";
            return false;
        }

        if (!int.TryParse(PreviewMaxCharsBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var previewChars)
            || previewChars is < 256 or > 65_536)
        {
            error = "Characters kept per history entry must be between 256 and 65536.";
            return false;
        }

        if (!int.TryParse(HistoryLoadLimitBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var historyLimit)
            || historyLimit is < 100 or > 1_000_000)
        {
            error = "Rows the history window loads must be between 100 and 1000000.";
            return false;
        }

        if (!int.TryParse(HistoryPreviewWidthBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var historyPreviewWidth)
            || historyPreviewWidth is < 120 or > 4_096)
        {
            error = "History preview image width must be between 120 and 4096 pixels.";
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

        settings.OverlayPreviewMaxWidth = previewWidth;
        settings.OverlayPreviewMaxHeight = previewHeight;
        settings.OverlayPreviewChars = overlayChars;
        settings.BeepDurationMs = beepMs;
        settings.PreviewMaxChars = previewChars;
        settings.HistoryLoadLimit = historyLimit;
        settings.HistoryPreviewMaxWidth = historyPreviewWidth;

        // Carried through explicitly. Before these had controls they were simply never assigned here, so a
        // position set by hand in PasteJump.json was silently discarded by opening this dialog and clicking OK.
        settings.OverlayX = overlayX;
        settings.OverlayY = overlayY;

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
