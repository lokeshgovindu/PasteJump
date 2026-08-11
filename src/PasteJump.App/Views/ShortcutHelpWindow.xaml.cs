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
    /// <param name="keyMap">
    /// The letter bindings in force, or null for the defaults. Read rather than assumed, because the letters are
    /// the user's to move and a reference card confidently naming <c>Z</c> when the user has moved the format
    /// cycle elsewhere is worse than no card - the same reasoning as the trigger letter above.
    /// </param>
    public ShortcutHelpWindow(
        char triggerKey = TriggerKey.Default,
        Action? onOpenManual = null,
        PasteKeyMap? keyMap = null)
    {
        InitializeComponent();

        var key = char.ToUpperInvariant(triggerKey);
        var chord = TriggerKey.Describe(key);

        var map = keyMap ?? PasteKeyMap.Default;

        KeyBack.Text = Keys(map, "back");
        KeyNewest.Text = Keys(map, "newest");
        KeySearch.Text = Keys(map, "search");
        KeyPin.Text = Keys(map, "pin");
        KeyFront.Text = Keys(map, "front");
        KeyFormat.Text = Keys(map, "format");
        KeyTags.Text = Keys(map, "tags");
        KeyClipboard.Text = Keys(map, "clipboard");
        KeyEditor.Text = Keys(map, "editor");
        KeyHistory.Text = Keys(map, "history");
        KeyExport.Text = Keys(map, "export");
        KeyCommit.Text = Keys(map, "commit");

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

    /// <summary>
    /// How one action's keys read in the card: the configured letter, then any key that fires it regardless.
    /// <para>
    /// A switched-off action is shown as "off" beside whatever still reaches it, rather than being hidden. The
    /// row is what tells the reader the action exists at all, and one they have turned off is exactly the one
    /// they may later wonder about.
    /// </para>
    /// </summary>
    private static string Keys(PasteKeyMap map, string name)
    {
        var entry = PasteKeyMap.Entries.First(e => e.Name == name);
        var letter = map.LetterFor(name) is { } value ? value.ToString() : "off";

        return entry.FixedAlias is { } alias ? $"{letter}  {alias}" : letter;
    }

    private readonly Action? _onOpenManual;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnManualClicked(object sender, RoutedEventArgs e) => _onOpenManual?.Invoke();
}
