using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace PasteJump.App.Views;

/// <summary>One running program, as offered in the picker.</summary>
/// <param name="FileName">Executable file name, which is what gets stored.</param>
/// <param name="WindowTitle">Title of its main window, shown only to identify it.</param>
public sealed record RunningApp(string FileName, string WindowTitle);

/// <summary>
/// Lets the user pick programs to exclude from the ones currently running, rather than typing an executable
/// name from memory.
/// </summary>
public partial class RunningAppPicker : Window
{
    private RunningAppPicker()
    {
        InitializeComponent();
    }

    /// <summary>File names the user chose. Empty when the dialog was cancelled.</summary>
    public IReadOnlyList<string> SelectedFileNames { get; private set; } = [];

    /// <summary>
    /// Shows the picker and returns the chosen file names.
    /// </summary>
    /// <param name="alreadyExcluded">
    /// Entries to leave out of the list. Offering something that is already excluded invites the user to add
    /// it twice and then wonder why only one appeared.
    /// </param>
    public static IReadOnlyList<string> Choose(Window? owner, IEnumerable<string> alreadyExcluded)
    {
        ArgumentNullException.ThrowIfNull(alreadyExcluded);

        var dialog = new RunningAppPicker();

        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }

        dialog.Populate(alreadyExcluded);

        return dialog.ShowDialog() == true ? dialog.SelectedFileNames : [];
    }

    /// <summary>
    /// Builds the picker without showing it, for the UI smoke harness. <see cref="Choose"/> is modal, which a
    /// harness cannot wait on.
    /// </summary>
    public static RunningAppPicker CreateForSmokeTest()
    {
        var dialog = new RunningAppPicker();
        dialog.Populate([]);

        return dialog;
    }

    private void Populate(IEnumerable<string> alreadyExcluded)
    {
        var excluded = new HashSet<string>(
            Core.Settings.ExcludedApps.NormaliseAll(alreadyExcluded),
            StringComparer.OrdinalIgnoreCase);

        var apps = new List<RunningApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Snapshot())
        {
            var fileName = Core.Settings.ExcludedApps.Normalise(process.ProcessName);

            // Deduplicated by file name, not by process: Chrome and Explorer run many processes with windows,
            // and the exclusion applies to the program rather than to one instance of it.
            if (fileName is null || excluded.Contains(fileName) || !seen.Add(fileName))
            {
                continue;
            }

            apps.Add(new RunningApp(fileName, process.MainWindowTitle));
        }

        AppsGrid.ItemsSource = apps.OrderBy(static a => a.FileName, StringComparer.CurrentCultureIgnoreCase).ToList();

        CountText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0} program{1} with a window",
            apps.Count,
            apps.Count == 1 ? string.Empty : "s");
    }

    /// <summary>
    /// Processes that own a visible window, with their titles.
    /// <para>
    /// Enumeration failures are swallowed per process rather than aborting the list. A process can exit
    /// between being enumerated and being asked for its title, and a protected one refuses outright - neither
    /// is a reason to show the user nothing.
    /// </para>
    /// </summary>
    private static List<Process> Snapshot()
    {
        var result = new List<Process>();

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero
                        && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    {
                        result.Add(process);
                    }
                }
                catch (Exception)
                {
                    // Exited or protected. Skip it.
                }
            }
        }
        catch (Exception)
        {
            // The whole enumeration failed. An empty picker still lets the user cancel and type a name.
        }

        return result;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void OnAddClicked(object sender, RoutedEventArgs e) => Accept();

    private void OnCancelClicked(object sender, RoutedEventArgs e) => Close();

    private void Accept()
    {
        SelectedFileNames = [.. AppsGrid.SelectedItems.OfType<RunningApp>().Select(static a => a.FileName)];

        if (SelectedFileNames.Count == 0)
        {
            // Nothing selected is not an error and not something to close on - the user pressed Add without
            // choosing, so the useful response is to leave the dialog up.
            CountText.Text = "Select at least one program.";
            return;
        }

        DialogResult = true;
        Close();
    }
}
