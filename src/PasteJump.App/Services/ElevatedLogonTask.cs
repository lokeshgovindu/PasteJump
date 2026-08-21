using System.Diagnostics;

namespace PasteJump.App.Services;

/// <summary>
/// Manages the scheduled task that starts PasteJump elevated at logon.
/// </summary>
/// <remarks>
/// <para>
/// A scheduled task rather than the ordinary startup shortcut, because <b>a shortcut cannot ask for
/// elevation</b> - Windows offers no way to mark a <c>.lnk</c> "run as administrator" without a UAC prompt
/// every single time, which is no way to start something at logon. A task registered with the highest
/// privileges starts elevated silently, which is exactly what is wanted and is how other resident tools on
/// this kind of machine do it.
/// </para>
/// <para>
/// <b>Why anybody would want this.</b> Where endpoint security routes one application's keyboard input
/// through a component of higher integrity than PasteJump, Windows excludes PasteJump's low-level hook from
/// seeing that input at all - UIPI, working as designed - and the gesture silently stops working in that one
/// application. Running elevated is the only thing that restores it. Measured 2026-08-21 and reproduced by
/// three unrelated programs including Clipjump, so it is a property of the machine rather than of this
/// application.
/// </para>
/// <para>
/// <c>schtasks.exe</c> rather than the Task Scheduler COM API: the command line is what
/// <c>tools/install-elevated-task.ps1</c> already uses, so there is one description of the task rather than
/// two that can disagree, and no COM interop to carry for four operations.
/// </para>
/// </remarks>
internal static class ElevatedLogonTask
{
    /// <summary>
    /// Name of the task. Shared with <c>tools/install-elevated-task.ps1</c> - a mismatch would leave the
    /// script's task invisible to the tray toggle and the two fighting over logon.
    /// </summary>
    public const string TaskName = "PasteJump (elevated)";

    /// <summary>Whether the task is registered. False on any failure: absent is the safe reading.</summary>
    public static bool Exists => Run("/Query", "/TN", TaskName).Success;

    /// <summary>
    /// Registers the task, pointing at the running executable.
    /// </summary>
    /// <remarks>
    /// <b>Requires elevation</b>, and says so rather than failing opaquely: <c>schtasks /RL HIGHEST</c> from an
    /// ordinary process returns "Access is denied". The caller elevates first - see
    /// <c>App.SetAlwaysRunAsAdministrator</c>, which relaunches under UAC and lets the elevated copy register
    /// it, so one prompt buys both the elevation and the task.
    /// </remarks>
    public static (bool Success, string Message) TryRegister()
    {
        var exePath = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exePath))
        {
            return (false, "PasteJump could not work out its own path.");
        }

        // /IT - an interactive token, so it runs in the user's session and can draw windows. Without it the
        // task starts in session 0 and PasteJump would be running where nobody can see it.
        // /RL HIGHEST - the entire point.
        // /F - replace an existing registration rather than failing, so re-enabling is idempotent.
        var result = Run(
            "/Create",
            "/TN", TaskName,
            "/TR", "\"" + exePath + "\"",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/IT",
            "/F");

        return result.Success
            ? (true, string.Empty)
            : (false, Describe(result));
    }

    /// <summary>Removes the task. Succeeds when it was not there in the first place.</summary>
    public static (bool Success, string Message) TryRemove()
    {
        if (!Exists)
        {
            return (true, string.Empty);
        }

        var result = Run("/Delete", "/TN", TaskName, "/F");

        return result.Success
            ? (true, string.Empty)
            : (false, Describe(result));
    }

    /// <summary>Starts PasteJump through the task, which is what makes it start elevated.</summary>
    public static bool TryRun() => Run("/Run", "/TN", TaskName).Success;

    private static string Describe((bool Success, string Output) result)
    {
        var text = result.Output.Trim();

        if (text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows refused: registering a task that runs with the highest privileges needs "
                + "administrator rights.";
        }

        return string.IsNullOrEmpty(text) ? "schtasks reported an error." : text;
    }

    private static (bool Success, string Output) Run(params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);

            if (process is null)
            {
                return (false, "schtasks.exe could not be started.");
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            // Bounded, because a hung schtasks must not hang the UI thread that called this.
            if (!process.WaitForExit(10_000))
            {
                return (false, "schtasks.exe did not finish.");
            }

            return (process.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
