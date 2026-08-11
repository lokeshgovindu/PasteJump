using System.Windows;
using PasteJump.Core.PasteMode;

namespace PasteJump.App.Views;

/// <summary>Reference card for the paste-mode keys, shown by F1 or from the tray menu.</summary>
public partial class ShortcutHelpWindow : Window
{
    /// <param name="triggerKey">
    /// The configured paste-mode trigger letter. Substituted into the text rather than hard-coded, because
    /// a help window confidently telling the user to press Ctrl+V when the trigger is Ctrl+B is worse than
    /// no help window at all.
    /// </param>
    /// <param name="onOpenManual">
    /// What the manual button does, or null to hide the button entirely.
    /// <para>
    /// Injected rather than resolved here, and the window deliberately knows nothing about where the .chm lives
    /// or whether there is one: the caller decides whether a manual is reachable and passes null when it is
    /// not. That is what lets the UI smoke harness render the button for the help screenshots, where the point
    /// is to show the window as a release build shows it rather than as an uninstalled build does.
    /// </para>
    /// </param>
    public ShortcutHelpWindow(char triggerKey = TriggerKey.Default, Action? onOpenManual = null)
    {
        InitializeComponent();

        var key = char.ToUpperInvariant(triggerKey);
        var chord = TriggerKey.Describe(key);

        Title = $"PasteJump - Paste-mode keys ({chord})";

        IntroText.Text =
            $"Press {chord} and keep Ctrl held. The overlay appears; tap keys to act on the stack. " +
            "Release Ctrl to commit.";

        // The arrows are listed beside the letters rather than instead of them: the letters are what a hand
        // coming from Clipjump already knows, and they keep working.
        TriggerKeyText.Text = $"{key}  ↓  →";
        SearchStepText.Text = $"{chord} / Ctrl+C";

        _onOpenManual = onOpenManual;

        // Collapsed rather than disabled when there is nothing to open. A greyed button invites a click and
        // then explains itself; an absent one asks nothing of the reader.
        if (onOpenManual is null)
        {
            ManualButton.Visibility = Visibility.Collapsed;
        }

        // SizeToContent is released once the initial height has been measured. Left on, it recomputes the
        // height from the content and undoes any vertical resize the user makes, so the window would appear
        // resizable and then silently snap back.
        Loaded += (_, _) => SizeToContent = SizeToContent.Manual;
    }

    private readonly Action? _onOpenManual;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnManualClicked(object sender, RoutedEventArgs e) => _onOpenManual?.Invoke();
}
