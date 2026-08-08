using System.Globalization;
using System.Windows;
using PasteJump.Import;

namespace PasteJump.App.Views;

/// <summary>
/// Progress and a working Cancel button for a running import.
/// <para>
/// The import itself runs on a worker thread. That is the whole point: run inline it blocked the UI thread,
/// and because a OneDrive-backed Clipjump folder makes every file read a download, the freeze could last
/// minutes and looked like PasteJump having hung.
/// </para>
/// </summary>
public partial class ImportProgressDialog : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private bool _finished;

    private ImportProgressDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Runs an import to completion behind a modal progress window and returns its report.
    /// </summary>
    /// <param name="run">
    /// Does the work. Given a progress sink and the cancellation token, and called on a worker thread.
    /// </param>
    public static ImportReport Run(
        Func<IProgress<ImportProgress>, CancellationToken, ImportReport> run,
        Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        var dialog = new ImportProgressDialog();

        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        // Progress<T> captures the current SynchronizationContext, and this is constructed on the UI thread -
        // so reports arrive back on the dispatcher and the handler can touch controls directly.
        var progress = new Progress<ImportProgress>(dialog.Update);

        ImportReport? report = null;

        dialog.Loaded += async (_, _) =>
        {
            try
            {
                report = await Task.Run(
                    () => run(progress, dialog._cancellation.Token),
                    dialog._cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                report = new ImportReport { Cancelled = true };
            }
            catch (Exception ex)
            {
                report = new ImportReport();
                report.Errors.Add(ex.Message);
            }
            finally
            {
                // Set before Close, so the closing handler knows this was completion rather than the user
                // dismissing the window with Esc or the title-bar X.
                dialog._finished = true;
                dialog.Close();
            }
        };

        dialog.ShowDialog();

        // Never null in practice - the Loaded handler always assigns before closing - but a window closed
        // before Loaded ran would leave it unset, and reporting nothing imported is the honest answer there.
        return report ?? new ImportReport { Cancelled = true };
    }

    /// <summary>
    /// Builds the dialog part-way through a run, for the UI smoke harness.
    /// <para>
    /// <see cref="Run"/> is modal and drives a worker to completion, so the harness cannot use it. Without a
    /// hook this window would never be instantiated and a broken template would go unnoticed until an import
    /// was actually slow enough to show it.
    /// </para>
    /// </summary>
    public static ImportProgressDialog CreateForSmokeTest(int processed, int total)
    {
        var dialog = new ImportProgressDialog();

        // After layout, so the bar has a track width to measure against - set before that, the fill would
        // compute to zero and the screenshot would show an empty bar.
        dialog.Loaded += (_, _) => dialog.Update(new ImportProgress(processed, total));

        return dialog;
    }

    private void Update(ImportProgress progress)
    {
        if (progress.Total > 0)
        {
            var fraction = Math.Clamp((double)progress.Processed / progress.Total, 0, 1);

            // Measured against the track's actual width rather than a fixed number, so the bar stays correct
            // if the window is ever resized or shown at a different DPI.
            if (ProgressFill.Parent is FrameworkElement track && track.ActualWidth > 0)
            {
                ProgressFill.Width = track.ActualWidth * fraction;
            }

            DetailText.Text = string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} of {1:N0} entries",
                progress.Processed,
                progress.Total);
        }
        else
        {
            DetailText.Text = string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} entries",
                progress.Processed);
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _cancellation.Cancel();

        // Left open, with the button disabled and the wording changed. The worker stops at the next row
        // boundary, and on a cloud folder that can be a few seconds away - closing immediately would leave the
        // import running invisibly and the report arriving with no window to show it.
        CancelButton.IsEnabled = false;
        HeadlineText.Text = "Stopping…";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Esc or the title-bar X mean cancel, not "abandon the worker". The window stays up until the work
        // actually stops, so the store is never left being written to by a task nobody is waiting for.
        if (!_finished)
        {
            e.Cancel = true;
            OnCancelClicked(this, new RoutedEventArgs());
            return;
        }

        _cancellation.Dispose();
        base.OnClosing(e);
    }
}
