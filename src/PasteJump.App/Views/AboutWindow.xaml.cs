using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using PasteJump.Core;

namespace PasteJump.App.Views;

/// <summary>
/// Product identity: name, version, attribution and the repository link.
/// <para>
/// A real window rather than a <see cref="MessageBox"/> because a message box is drawn by the OS with
/// its own chrome and ignores the palette entirely - in dark mode it would be the one light-on-light
/// surface in the app.
/// </para>
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // Current, not Display: this is the one place the full four-part number belongs, because it is
        // the number someone reads back in a bug report.
        VersionText.Text = $"Version {AppVersion.Current}";

        ShowBuildTimestamp();
        ShowCopyright();
    }

    /// <summary>
    /// Shows when this build was produced, in local time. Local rather than UTC because the reader is placing
    /// it against "when did I install this", and the row is hidden entirely rather than showing "unknown" when
    /// there is no stamp - an empty fact is not worth a line.
    /// </summary>
    private void ShowBuildTimestamp()
    {
        if (AppVersion.BuildTimestamp is not { } built)
        {
            BuildText.Visibility = Visibility.Collapsed;
            return;
        }

        BuildText.Text = "Built " + built.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Renders the copyright line with the author's name as a link to their profile.
    /// <para>
    /// Inlines rather than a single string, because only part of the line is a link. Both halves come from
    /// assembly attributes: writing the name here as well would be a second copy of it, and the copy on screen
    /// is the one that goes stale.
    /// </para>
    /// </summary>
    private void ShowCopyright()
    {
        var credit = CreditLineSplitter.Split(AppVersion.Copyright, AppVersion.Author);

        CopyrightText.Inlines.Clear();
        CopyrightText.Inlines.Add(new Run(credit.Prefix));

        if (!credit.HasAuthor || !Uri.TryCreate(AppVersion.AuthorUrl, UriKind.Absolute, out var profile))
        {
            // No name found, or no profile to point at. The line still reads correctly, just without a link.
            CopyrightText.Inlines.Add(new Run(credit.Author + credit.Suffix));
            return;
        }

        var link = new Hyperlink(new Run(credit.Author))
        {
            NavigateUri = profile,
            ToolTip = profile.Host + profile.AbsolutePath,
        };

        link.RequestNavigate += OnRequestNavigate;

        CopyrightText.Inlines.Add(link);
        CopyrightText.Inlines.Add(new Run(credit.Suffix));
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Opens the repository in the default browser.
    /// <para>
    /// <c>UseShellExecute</c> is required. Without it .NET treats the URI as an executable path and
    /// throws <c>Win32Exception</c>, which is the standard trap when moving this pattern from .NET
    /// Framework - where the default was the other way round.
    /// </para>
    /// </summary>
    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageDialog.Warn(ex.Message, headline: "Could not open the link", owner: this);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Puts the version and environment on the clipboard, for pasting into a bug report.
    /// <para>
    /// Deliberately a plain clipboard write with no <c>SelfWriteGuard</c> registration: the user pressed
    /// a button labelled Copy, so this becoming a new clip is the correct outcome rather than something
    /// to suppress.
    /// </para>
    /// </summary>
    private void OnCopyDetailsClicked(object sender, RoutedEventArgs e)
    {
        // The build stamp goes in too. Two people on the same version number can be on different builds, and
        // this is the line that tells them apart in a bug report.
        var built = AppVersion.BuildTimestamp is { } time
            ? $"Built {time.ToLocalTime():yyyy-MM-dd HH:mm}{Environment.NewLine}"
            : string.Empty;

        var details =
            $"PasteJump {AppVersion.Current}{Environment.NewLine}" +
            built +
            $"Windows {Environment.OSVersion.Version}{Environment.NewLine}" +
            $".NET {Environment.Version}{Environment.NewLine}" +
            $"{(Environment.Is64BitProcess ? "x64" : "x86")} process";

        try
        {
            Clipboard.SetText(details);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // The clipboard is a machine-wide lock and another process can be holding it. Nothing here
            // is worth a modal dialog over - the details are on screen anyway.
            MessageDialog.Warn("The clipboard is busy. Nothing was copied.", owner: this);
        }
    }
}
