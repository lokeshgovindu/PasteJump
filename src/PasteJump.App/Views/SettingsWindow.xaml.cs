using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PasteJump.App.Services;
using PasteJump.Core;
using PasteJump.Core.Formatting;
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

    private readonly PasteJumpSettings _original;
    private readonly FormatterRegistry _formatters;
    private readonly DataLocation _originalLocation;

    public SettingsWindow(
        PasteJumpSettings settings,
        FormatterRegistry formatters,
        DataLocation dataLocation = DataLocation.ApplicationFolder)
    {
        _original = settings;
        _formatters = formatters;
        _originalLocation = dataLocation;

        InitializeComponent();
        Load();
        RefreshAdvanced();
    }

    /// <summary>Raised with the new settings when the user accepts the dialog.</summary>
    public event Action<PasteJumpSettings>? SettingsApplied;

    /// <summary>
    /// Raised on accept only when the data location actually changed.
    /// <para>
    /// Separate from <see cref="SettingsApplied"/> because it is not one of the settings: it lives in its
    /// own file outside the data directory, and acting on it means moving the database and restarting
    /// rather than applying a value in memory.
    /// </para>
    /// </summary>
    public event Action<DataLocation>? DataLocationChangeRequested;

    private void Load()
    {
        MonitorClipboardCheck.IsChecked = _original.MonitorClipboard;
        StoreImagesCheck.IsChecked = _original.StoreImages;
        AllowDuplicatesCheck.IsChecked = _original.AllowDuplicateClips;
        MaxClipsBox.Text = _original.MaxClips.ToString(CultureInfo.CurrentCulture);
        IgnoredProcessesBox.Text = string.Join(Environment.NewLine, _original.IgnoredProcesses);

        RecordHistoryCheck.IsChecked = _original.RecordHistory;
        RetentionDaysBox.Text = _original.HistoryRetentionDays.ToString(CultureInfo.CurrentCulture);

        PreservePositionCheck.IsChecked = _original.PreserveClipPosition;
        OpenSearchCheck.IsChecked = _original.OpenSearchImmediately;
        ResetFormatterCheck.IsChecked = _original.ResetFormatterOnEntry;

        foreach (var formatter in _formatters.All)
        {
            DefaultFormatterCombo.Items.Add(formatter.DisplayName);
        }

        DefaultFormatterCombo.SelectedItem = _formatters.Resolve(_original.DefaultFormatterId).DisplayName;

        foreach (var choice in PasteKeystrokeChoices)
        {
            PasteKeystrokeCombo.Items.Add(choice.Label);
        }

        PasteKeystrokeCombo.SelectedItem = PasteKeystrokeChoices
            .First(c => c.Keystroke == _original.PasteKeystroke).Label;

        WarnAboutConflictCheck.IsChecked = _original.WarnAboutClipboardManagerConflict;

        foreach (var choice in ThemeChoices)
        {
            ThemeCombo.Items.Add(choice.Label);
        }

        ThemeCombo.SelectedItem = ThemeChoices.First(c => c.Theme == _original.Theme).Label;

        foreach (var choice in DensityChoices)
        {
            DensityCombo.Items.Add(choice.Label);
        }

        DensityCombo.SelectedItem = DensityChoices.First(c => c.Density == _original.GridDensity).Label;

        VersionText.Text = $"PasteJump {AppVersion.Current}";

        ShowCopyNotificationCheck.IsChecked = _original.ShowCopyNotification;
        CopyNotificationMsBox.Text = _original.CopyNotificationMs.ToString(CultureInfo.CurrentCulture);
        PasteSettleDelayBox.Text = _original.PasteSettleDelayMs.ToString(CultureInfo.CurrentCulture);

        // Reflect the real state of the shortcut, not just what settings claim. The user may have
        // deleted it from the Startup folder by hand since the last run.
        RunAtLogonCheck.IsChecked = _original.RunAtLogon || StartupShortcut.Exists;
        TextEditorBox.Text = _original.TextEditor;

        foreach (var choice in DataLocationChoices)
        {
            DataLocationCombo.Items.Add(choice.Label);
        }

        DataLocationCombo.SelectedItem = DataLocationChoices.First(c => c.Location == _originalLocation).Label;
    }

    /// <summary>The location currently picked in the combo, which may differ from the one in force.</summary>
    private DataLocation SelectedDataLocation => DataLocationChoices
        .FirstOrDefault(c => string.Equals(c.Label, DataLocationCombo.SelectedItem as string, StringComparison.Ordinal))
        .Location;

    private void OnDataLocationChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedDataLocation;

        DataLocationPathText.Text = Path.Combine(AppPaths.RootFor(selected), "data");

        // Says so up front rather than only in the confirmation prompt, so the restart is not a surprise
        // discovered after clicking OK.
        if (selected != _originalLocation)
        {
            DataLocationPathText.Text += "   (restart required)";
        }
    }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        if (!TryBuild(out var updated, out var error))
        {
            ValidationText.Text = error;
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        SettingsApplied?.Invoke(updated);

        // After SettingsApplied, so the settings are already saved to the old location before anything
        // starts moving it. The handler may restart the process, which ends this method.
        if (SelectedDataLocation != _originalLocation)
        {
            DataLocationChangeRequested?.Invoke(SelectedDataLocation);
        }

        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Rebuilds the Advanced list from the settings currently entered in the dialog, so it reflects
    /// pending edits rather than only what was on disk when the window opened.
    /// </summary>
    private void RefreshAdvanced()
    {
        // Falls back to the loaded settings when the form does not currently validate - the inventory
        // should still be readable while a text box holds a half-typed number.
        var source = TryBuild(out var pending, out _) ? pending : _original;

        var filter = AdvancedFilterBox.Text;

        var rows = SettingsInspector.Describe(source)
            .Where(r => string.IsNullOrWhiteSpace(filter)
                || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        AdvancedGrid.ItemsSource = rows;

        var modified = rows.Count(static r => r.IsModified);

        AdvancedStatus.Text =
            $"{rows.Count} setting{(rows.Count == 1 ? string.Empty : "s")}, {modified} changed from default. " +
            "Read-only here - edit on the other tabs, or in data\\settings.json while PasteJump is closed.";

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

        var selectedName = DefaultFormatterCombo.SelectedItem as string;
        var formatter = _formatters.All.FirstOrDefault(f =>
            string.Equals(f.DisplayName, selectedName, StringComparison.Ordinal));

        settings.MonitorClipboard = MonitorClipboardCheck.IsChecked == true;
        settings.StoreImages = StoreImagesCheck.IsChecked == true;
        settings.AllowDuplicateClips = AllowDuplicatesCheck.IsChecked == true;
        settings.MaxClips = maxClips;

        settings.IgnoredProcesses = IgnoredProcessesBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

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

        settings.RunAtLogon = RunAtLogonCheck.IsChecked == true;
        settings.TextEditor = TextEditorBox.Text;

        // Carried forward, not surfaced: re-offering the legacy import after every settings change
        // would be maddening.
        settings.LegacyImportCompleted = _original.LegacyImportCompleted;

        settings.Normalise();
        return true;
    }
}
