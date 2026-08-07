using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Clipjog.App.Services;
using Clipjog.Core;
using Clipjog.Core.Formatting;
using Clipjog.Core.Settings;

namespace Clipjog.App.Views;

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

    /// <summary>Density options, labelled as Outlook and Explorer label them.</summary>
    private static readonly (GridDensity Density, string Label)[] DensityChoices =
    [
        (GridDensity.Roomy, "Roomy"),
        (GridDensity.Cozy, "Cozy"),
        (GridDensity.Compact, "Compact"),
    ];

    private readonly ClipjogSettings _original;
    private readonly FormatterRegistry _formatters;

    public SettingsWindow(ClipjogSettings settings, FormatterRegistry formatters)
    {
        _original = settings;
        _formatters = formatters;

        InitializeComponent();
        Load();
        RefreshAdvanced();
    }

    /// <summary>Raised with the new settings when the user accepts the dialog.</summary>
    public event Action<ClipjogSettings>? SettingsApplied;

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

        VersionText.Text = $"Clipjog {AppVersion.Current}";

        ShowCopyNotificationCheck.IsChecked = _original.ShowCopyNotification;
        CopyNotificationMsBox.Text = _original.CopyNotificationMs.ToString(CultureInfo.CurrentCulture);
        PasteSettleDelayBox.Text = _original.PasteSettleDelayMs.ToString(CultureInfo.CurrentCulture);

        // Reflect the real state of the shortcut, not just what settings claim. The user may have
        // deleted it from the Startup folder by hand since the last run.
        RunAtLogonCheck.IsChecked = _original.RunAtLogon || StartupShortcut.Exists;
        TextEditorBox.Text = _original.TextEditor;
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
            "Read-only here - edit on the other tabs, or in data\\settings.json while Clipjog is closed.";

        AdvancedFilterCue.Visibility = string.IsNullOrEmpty(filter)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnAdvancedFilterChanged(object sender, TextChangedEventArgs e) => RefreshAdvanced();

    private bool TryBuild(out ClipjogSettings settings, out string error)
    {
        settings = new ClipjogSettings();
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

        settings.RunAtLogon = RunAtLogonCheck.IsChecked == true;
        settings.TextEditor = TextEditorBox.Text;

        // Carried forward, not surfaced: re-offering the legacy import after every settings change
        // would be maddening.
        settings.LegacyImportCompleted = _original.LegacyImportCompleted;

        settings.Normalise();
        return true;
    }
}
