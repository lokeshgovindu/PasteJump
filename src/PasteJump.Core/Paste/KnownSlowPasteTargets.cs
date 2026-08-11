namespace PasteJump.Core.Paste;

/// <summary>
/// Applications known to hold a cached copy of the clipboard, and a starting delay for each.
/// <para>
/// Offered rather than applied. A prefilled table nobody asked for would change how every paste into these programs
/// behaves, invisibly, on the strength of a guess about someone else's machine - and a wrong entry here costs real
/// milliseconds on every paste. So the settings dialog has a button that fills the grid with these, which the user
/// can then edit or remove; nothing is written until they press OK.
/// </para>
/// <para>
/// <b>These are starting points, not measurements.</b> They come from the behaviour the help already describes -
/// Office, Electron shells and remote-desktop clients serving a paste from a stale cache - not from timing runs on
/// any particular machine. The right value depends on the machine, and the honest instruction is "raise it until the
/// wrong clip stops appearing".
/// </para>
/// </summary>
public static class KnownSlowPasteTargets
{
    /// <param name="Process">Executable file name, matched the way the ignore list matches.</param>
    /// <param name="Milliseconds">A conservative starting delay.</param>
    /// <param name="Why">Shown in the help, so the list is a claim that can be judged rather than a magic table.</param>
    public sealed record Target(string Process, int Milliseconds, string Why);

    /// <summary>
    /// One value per family rather than a distinct number each, deliberately: inventing 85 for Word and 90 for Excel
    /// would imply a precision nobody measured. What the list asserts is "more than the default", not an exact figure.
    /// </summary>
    private const int OfficeAndElectron = 80;

    private const int RemoteDesktop = 120;

    public static IReadOnlyList<Target> All { get; } =
    [
        new("WINWORD.EXE", OfficeAndElectron, "Word caches the clipboard briefly after it is written."),
        new("EXCEL.EXE", OfficeAndElectron, "Excel is the worst of the Office family for this."),
        new("POWERPNT.EXE", OfficeAndElectron, "PowerPoint, same as the rest of Office."),
        new("OUTLOOK.EXE", OfficeAndElectron, "Classic Outlook."),
        new("olk.exe", OfficeAndElectron, "The new Outlook, which is an Electron shell as well as Office."),
        new("ONENOTE.EXE", OfficeAndElectron, "OneNote, same as the rest of Office."),
        new("ms-teams.exe", OfficeAndElectron, "Teams - an Electron shell, and slow to release the clipboard."),
        new("Teams.exe", OfficeAndElectron, "The older Teams build, still installed on many machines."),
        new("slack.exe", OfficeAndElectron, "Slack, Electron."),
        new("Discord.exe", OfficeAndElectron, "Discord, Electron."),
        new("Code.exe", OfficeAndElectron, "VS Code, Electron."),
        new("mstsc.exe", RemoteDesktop, "Remote Desktop has to carry the clipboard over the network."),
        new("wfica32.exe", RemoteDesktop, "Citrix, for the same reason as Remote Desktop."),
    ];

    /// <summary>
    /// The suggestions not already listed by the user, so pressing the button twice adds nothing the second time and
    /// a value someone has already tuned is never overwritten.
    /// </summary>
    public static IReadOnlyList<Target> NotAlreadyListed(IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        var already = new HashSet<string>(
            existing.Select(Settings.ExcludedApps.Normalise).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        return [.. All.Where(target => !already.Contains(target.Process))];
    }
}
