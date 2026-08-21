namespace PasteJump.Core;

/// <summary>
/// The command line PasteJump passes to a copy of itself that is replacing the running one.
/// </summary>
/// <remarks>
/// <para>
/// Restarting normally needs none of this: the old instance starts the replacement from its own
/// <c>Exit</c> handler, so the single-instance mutex is already released by the time the new process looks for
/// it. **Restarting elevated cannot work that way.** Elevation goes through <c>ShellExecute</c> with the
/// <c>runas</c> verb, which shows a UAC prompt the user may refuse - and a refusal after we had already shut
/// down would leave them with no PasteJump at all. So the elevated copy is launched <em>first</em>, while the
/// old one is still running and still holding the mutex, and told which process to wait for.
/// </para>
/// <para>
/// Without the wait the new instance would find the mutex held, conclude it was a second launch, surface the
/// first instance and exit - which looks exactly like the elevated restart silently doing nothing.
/// </para>
/// <para>
/// Parsing lives here, in <c>Core</c>, because it is the one part of this that can be tested: the waiting
/// itself needs a real process.
/// </para>
/// </remarks>
public static class RelaunchRequest
{
    /// <summary>The switch, followed by the process id to wait for.</summary>
    public const string ReplaceSwitch = "--replace";

    /// <summary>
    /// How long a replacement waits for its predecessor before giving up and starting anyway.
    /// </summary>
    /// <remarks>
    /// Bounded, and starting anyway is the right failure: the predecessor may already be gone, or may be
    /// wedged. Waiting for ever would turn a restart into a process that never appears, which is worse than
    /// the mutex collision this avoids - and that collision is itself handled, by surfacing the first copy.
    /// </remarks>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The process id this copy should wait for, or null when it was not started as a replacement.
    /// </summary>
    /// <remarks>
    /// Deliberately forgiving about everything except the number: an unrecognised argument yields null rather
    /// than an error, because this runs before there is any window to report an error in, and starting normally
    /// is a sane outcome for a command line nobody understands. A missing or unparseable value is the same
    /// case - see the tests, which pin each one.
    /// </remarks>
    public static int? TryParseReplacedProcessId(IReadOnlyList<string>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (!string.Equals(arguments[i], ReplaceSwitch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Positive only. Zero and negatives are not process ids, and passing our own id would be a request
            // to wait for ourselves - the caller checks for that, since only it knows what "ourselves" is.
            if (int.TryParse(arguments[i + 1], out var processId) && processId > 0)
            {
                return processId;
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// Asks the elevated copy to register the logon task once it is up.
    /// </summary>
    /// <remarks>
    /// Registering a task that runs with the highest privileges needs those privileges, so it cannot be done
    /// by the copy asking for it. Passing the request through the relaunch means <b>one</b> UAC prompt buys
    /// both the elevation and the registration - asking twice for the same decision is how a switch comes to
    /// feel broken.
    /// </remarks>
    public const string EnableElevatedLogonSwitch = "--enable-elevated-logon";

    /// <summary>Whether this copy was asked to register the elevated logon task.</summary>
    public static bool WantsElevatedLogonTask(IReadOnlyList<string>? arguments) =>
        arguments is not null
        && arguments.Any(a => string.Equals(a, EnableElevatedLogonSwitch, StringComparison.OrdinalIgnoreCase));

    /// <summary>The arguments to launch a replacement with, waiting for the given process to exit.</summary>
    public static string Arguments(int processIdToReplace, bool enableElevatedLogon = false) =>
        enableElevatedLogon
            ? $"{ReplaceSwitch} {processIdToReplace} {EnableElevatedLogonSwitch}"
            : $"{ReplaceSwitch} {processIdToReplace}";
}
