using System.Windows;
using System.Windows.Controls;

namespace PasteJump.App.Views;

/// <summary>Which glyph and colour a dialog leads with.</summary>
public enum DialogKind
{
    Information,
    Question,
    Warning,
    Error,
}

/// <summary>Which buttons a dialog offers.</summary>
public enum DialogButtons
{
    Ok,
    OkCancel,
    YesNo,
}

/// <summary>What the user chose. <see cref="DialogResultKind.Cancelled"/> also covers closing the window.</summary>
public enum DialogResultKind
{
    Cancelled,
    Accepted,
}

/// <summary>
/// Themed replacement for <see cref="MessageBox"/>, for prompts that are PasteJump's own.
/// <para>
/// Shown with <c>ShowDialog</c>, so <c>IsDefault</c> and <c>IsCancel</c> behave as they are meant to and Esc
/// cancels - unlike the non-modal windows in this app, where both are inert and need explicit handlers.
/// </para>
/// </summary>
public partial class MessageDialog : Window
{
    private MessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows a modal themed dialog and returns what the user chose.
    /// </summary>
    /// <param name="owner">
    /// Centres the dialog on its owner when there is one. A tray app usually has no window up, which is why
    /// this is optional and the fallback is centring on screen.
    /// </param>
    public static DialogResultKind Show(
        string message,
        string? headline = null,
        string title = "PasteJump",
        DialogKind kind = DialogKind.Information,
        DialogButtons buttons = DialogButtons.Ok,
        Window? owner = null)
    {
        var dialog = new MessageDialog { Title = title };

        dialog.BodyText.Text = message;

        if (!string.IsNullOrWhiteSpace(headline))
        {
            dialog.HeadlineText.Text = headline;
            dialog.HeadlineText.Visibility = Visibility.Visible;
            dialog.BodyText.Margin = new Thickness(0, 8, 0, 0);
        }

        dialog.ApplyKind(kind);
        dialog.ApplyButtons(buttons);

        if (owner is not null && owner.IsLoaded)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog() == true ? DialogResultKind.Accepted : DialogResultKind.Cancelled;
    }

    /// <summary>
    /// Builds a dialog without showing it, for the UI smoke harness.
    /// <para>
    /// Exists because <see cref="Show"/> is modal and would block the harness indefinitely. This is the only
    /// window whose content is assembled entirely in code, so without a hook the harness would never
    /// instantiate it and a broken template would go unnoticed until the app first had something to say.
    /// </para>
    /// </summary>
    public static MessageDialog CreateForSmokeTest(
        string message,
        string? headline = null,
        DialogKind kind = DialogKind.Warning,
        DialogButtons buttons = DialogButtons.YesNo)
    {
        var dialog = new MessageDialog();

        dialog.BodyText.Text = message;

        if (!string.IsNullOrWhiteSpace(headline))
        {
            dialog.HeadlineText.Text = headline;
            dialog.HeadlineText.Visibility = Visibility.Visible;
            dialog.BodyText.Margin = new Thickness(0, 8, 0, 0);
        }

        dialog.ApplyKind(kind);
        dialog.ApplyButtons(buttons);

        return dialog;
    }

    /// <summary>Convenience for the common "tell the user something went wrong" case.</summary>
    public static void Warn(string message, string? headline = null, Window? owner = null)
        => Show(message, headline, "PasteJump", DialogKind.Warning, DialogButtons.Ok, owner);

    /// <summary>Convenience for a yes/no question. True when the user said yes.</summary>
    public static bool Confirm(
        string message,
        string? headline = null,
        string title = "PasteJump",
        Window? owner = null)
        => Show(message, headline, title, DialogKind.Question, DialogButtons.YesNo, owner)
            == DialogResultKind.Accepted;

    private void ApplyKind(DialogKind kind)
    {
        // Characters rather than an icon font. Segoe Fluent Icons is not present on every supported build, and
        // a missing glyph renders as a box - a worse failure than a plain punctuation mark.
        (GlyphText.Text, var brush) = kind switch
        {
            DialogKind.Question => ("?", "AccentBrush"),
            DialogKind.Warning => ("!", "WarnBrush"),
            DialogKind.Error => ("!", "DangerBrush"),
            _ => ("i", "AccentBrush"),
        };

        // DynamicResource, not a resolved brush: ThemeManager swaps the palette dictionary wholesale, and a
        // brush looked up once here would not follow a theme change while the dialog is open.
        GlyphText.SetResourceReference(ForegroundProperty, brush);
    }

    private void ApplyButtons(DialogButtons buttons)
    {
        switch (buttons)
        {
            case DialogButtons.YesNo:
                Add("_Yes", accept: true, isDefault: true);
                Add("_No", accept: false, isCancel: true);
                break;

            case DialogButtons.OkCancel:
                Add("_OK", accept: true, isDefault: true);
                Add("_Cancel", accept: false, isCancel: true);
                break;

            default:
                Add("_OK", accept: true, isDefault: true, isCancel: true);
                break;
        }

        void Add(string content, bool accept, bool isDefault = false, bool isCancel = false)
        {
            var button = new Button
            {
                Content = content,
                IsDefault = isDefault,
                IsCancel = isCancel,
            };

            button.Click += (_, _) =>
            {
                DialogResult = accept;
                Close();
            };

            Buttons.Children.Add(button);
        }
    }
}
