using System.Windows;

namespace Clipjog.App.Views;

/// <summary>Small modal for editing a clip's tags.</summary>
public partial class TagEditorWindow : Window
{
    public TagEditorWindow(IReadOnlyList<string> existingTags)
    {
        InitializeComponent();

        TagsBox.Text = string.Join(' ', existingTags);
        Tags = existingTags;

        Loaded += (_, _) =>
        {
            TagsBox.Focus();
            TagsBox.SelectAll();
        };
    }

    /// <summary>The tags as accepted. Unchanged from the input if the dialog was cancelled.</summary>
    public IReadOnlyList<string> Tags { get; private set; }

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        // Accept either separator: people type both, and silently keeping a trailing comma as part
        // of a tag name would be a confusing near-miss when searching later.
        Tags = TagsBox.Text
            .Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static t => t.TrimStart('#'))
            .Where(static t => t.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
