using System.Windows;
using System.Windows.Controls;
using PasteJump.Import;

namespace PasteJump.App.Views;

/// <summary>
/// Asks which Clipjump folder to import from, with auto-detection as a starting point rather than a verdict.
/// </summary>
public partial class ImportDialog : Window
{
    /// <param name="detectedFolder">
    /// What the locator found, or null when it found nothing. Null is a normal case, not an error: the dialog
    /// simply opens empty and waits for the user to browse, which is the whole reason it exists.
    /// </param>
    public ImportDialog(string? detectedFolder)
    {
        InitializeComponent();

        FolderBox.Text = detectedFolder ?? string.Empty;

        // Caret at the end and focus in the box, so a wrong guess can be replaced by typing without first
        // having to select the text.
        FolderBox.CaretIndex = FolderBox.Text.Length;
        Loaded += (_, _) => FolderBox.Focus();

        Validate();
    }

    /// <summary>The folder to import from. Only meaningful once the dialog has returned true.</summary>
    public string SelectedFolder => FolderBox.Text.Trim();

    private void OnFolderChanged(object sender, TextChangedEventArgs e) => Validate();

    /// <summary>
    /// Enables Import only for a folder that actually holds a Clipjump history database, and says which of
    /// the failure cases applies.
    /// <para>
    /// Validated against the database rather than against the folder's name, because the name proves nothing:
    /// a folder called Clipjump with no <c>cache\data.db</c> has nothing to import, and one called anything
    /// else with a database has everything.
    /// </para>
    /// </summary>
    private void Validate()
    {
        var folder = SelectedFolder;

        if (folder.Length == 0)
        {
            SetStatus("Choose the folder containing Clipjump.exe.", ok: false);
            return;
        }

        if (!Directory.Exists(folder))
        {
            SetStatus("That folder does not exist.", ok: false);
            return;
        }

        if (!LegacyClipjumpLocator.IsClipjumpFolder(folder))
        {
            SetStatus(
                $@"No history database found. Expected {LegacyClipjumpLocator.DatabaseRelativePath} inside "
                + "this folder.",
                ok: false);

            return;
        }

        SetStatus("Ready to import.", ok: true);
    }

    private void SetStatus(string message, bool ok)
    {
        StatusText.Text = message;
        StatusText.SetResourceReference(ForegroundProperty, ok ? "MutedTextBrush" : "DangerBrush");
        ImportButton.IsEnabled = ok;
    }

    /// <summary>
    /// Folder picker. <c>OpenFolderDialog</c> is WPF's own since .NET 8, so this needs no WinForms reference
    /// and no shell interop.
    /// </summary>
    private void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the Clipjump folder",
            Multiselect = false,
        };

        // Starts where the box currently points when that is a real folder, so browsing from a near-miss does
        // not begin again at the top of the tree.
        if (Directory.Exists(SelectedFolder))
        {
            dialog.InitialDirectory = SelectedFolder;
        }

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            FolderBox.CaretIndex = FolderBox.Text.Length;
        }
    }

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        // Re-checked rather than trusted to the button's enabled state: the folder could have been renamed or
        // removed between the last keystroke and the click.
        Validate();

        if (!ImportButton.IsEnabled)
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();
}
