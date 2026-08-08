namespace PasteJump.Core.Settings;

/// <summary>
/// Tidies the excluded-application list: the rules for turning what a user typed, browsed to or picked from
/// a process list into the one thing capture actually compares against.
/// <para>
/// In <c>Core</c> rather than in the settings dialog because it is where the list's meaning lives. Capture
/// compares against <c>Process.ProcessName</c>-style file names, and every route into the list has to arrive
/// at the same shape or an entry silently never matches - which is the worst possible failure for a setting
/// whose whole job is keeping a password manager out of the clipboard history.
/// </para>
/// </summary>
public static class ExcludedApps
{
    /// <summary>
    /// Normalises one entry, or returns null when there is nothing usable in it.
    /// <para>
    /// A full path is reduced to its file name, because that is what capture can see: it resolves the
    /// foreground window's process and gets a file name, never a path. Storing
    /// <c>C:\Program Files\KeePass\KeePass.exe</c> would therefore never match anything - and the Browse
    /// button hands over exactly that.
    /// </para>
    /// <para>
    /// A missing extension is filled in. Someone typing "keepass" means the program, and rejecting it for the
    /// want of four characters would be pedantry; someone typing "keepass.exe" gets the same result.
    /// </para>
    /// </summary>
    public static string? Normalise(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return null;
        }

        var trimmed = entry.Trim().Trim('"');

        if (trimmed.Length == 0)
        {
            return null;
        }

        string name;

        try
        {
            name = Path.GetFileName(trimmed);
        }
        catch (ArgumentException)
        {
            // Invalid path characters. Treated as a bare name rather than rejected, since the user may simply
            // have typed something odd that still identifies a process.
            name = trimmed;
        }

        if (name.Length == 0)
        {
            return null;
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    /// <summary>
    /// Normalises a whole list, dropping blanks and case-insensitive duplicates while keeping the order the
    /// user built it in.
    /// <para>
    /// Order is preserved rather than sorted because the list is the user's own record of decisions, and
    /// re-sorting it under them on every save makes it hard to see what was just added. Duplicates go because
    /// Windows file names are case-insensitive, so <c>KeePass.exe</c> and <c>keepass.exe</c> are one entry and
    /// showing both invites the belief that they differ.
    /// </para>
    /// </summary>
    public static List<string> NormaliseAll(IEnumerable<string?>? entries)
    {
        var result = new List<string>();

        if (entries is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (Normalise(entry) is { } name && seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="entry"/> is already present, compared the way Windows compares file names.
    /// </summary>
    public static bool Contains(IEnumerable<string> existing, string? entry)
    {
        ArgumentNullException.ThrowIfNull(existing);

        return Normalise(entry) is { } name
            && existing.Any(e => string.Equals(Normalise(e), name, StringComparison.OrdinalIgnoreCase));
    }
}
