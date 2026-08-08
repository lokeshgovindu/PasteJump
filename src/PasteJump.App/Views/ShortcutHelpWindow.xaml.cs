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
    public ShortcutHelpWindow(char triggerKey = TriggerKey.Default)
    {
        InitializeComponent();

        var key = char.ToUpperInvariant(triggerKey);
        var chord = TriggerKey.Describe(key);

        Title = $"PasteJump - Paste-mode keys ({chord})";

        IntroText.Text =
            $"Press {chord} and keep Ctrl held. The overlay appears; tap keys to act on the stack. " +
            "Release Ctrl to commit.";

        TriggerKeyText.Text = key.ToString();
        SearchStepText.Text = $"{chord} / Ctrl+C";

        // SizeToContent is released once the initial height has been measured. Left on, it recomputes the
        // height from the content and undoes any vertical resize the user makes, so the window would appear
        // resizable and then silently snap back.
        Loaded += (_, _) => SizeToContent = SizeToContent.Manual;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
