namespace PasteJump.Import;

/// <summary>
/// Finds an existing Clipjump 12.x installation to import from.
/// <para>
/// Clipjump was distributed as a portable folder with no installer and no registry footprint, so
/// there is nothing authoritative to query - this has to be a search of plausible locations,
/// validated by the presence of the files we actually need.
/// </para>
/// </summary>
public static class LegacyClipjumpLocator
{
    /// <summary>Relative path of the history database inside a Clipjump folder.</summary>
    public const string DatabaseRelativePath = @"cache\data.db";

    /// <summary>Relative path of the settings file inside a Clipjump folder.</summary>
    public const string SettingsRelativePath = "settings.ini";

    /// <summary>
    /// Returns the most likely Clipjump folder, or null if none is found. A folder only qualifies
    /// if it actually contains a history database - a bare Clipjump.exe with no data is not worth
    /// prompting the user about.
    /// </summary>
    public static string? FindLikelyInstallation()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (IsClipjumpFolder(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsClipjumpFolder(string folder)
        => !string.IsNullOrWhiteSpace(folder)
            && File.Exists(Path.Combine(folder, DatabaseRelativePath));

    private static IEnumerable<string> EnumerateCandidates()
    {
        var roots = new List<string>();

        foreach (var special in new[]
        {
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.Desktop,
        })
        {
            var path = Environment.GetFolderPath(special);

            if (!string.IsNullOrEmpty(path))
            {
                roots.Add(path);
            }
        }

        // Fixed drive roots too: Clipjump's portable nature means people commonly keep it in a
        // hand-made folder such as D:\Tools or D:\Portable rather than anywhere standard.
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive is { IsReady: true, DriveType: DriveType.Fixed })
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch (IOException)
            {
                // A drive that disappears mid-enumeration is not interesting.
            }
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var found in SafeEnumerate(root, depth: 3))
            {
                yield return found;
            }
        }
    }

    /// <summary>
    /// Depth-limited search for directories whose name starts with "Clipjump".
    /// <para>
    /// Bounded on purpose. An unbounded recursive scan of every fixed drive at startup would take
    /// tens of seconds and hammer the disk while the user waits, to find a folder that in practice
    /// is never more than a few levels down.
    /// </para>
    /// </summary>
    private static IEnumerable<string> SafeEnumerate(string root, int depth)
    {
        if (depth < 0)
        {
            yield break;
        }

        string[] directories;

        try
        {
            directories = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);

            if (name.StartsWith("Clipjump", StringComparison.OrdinalIgnoreCase))
            {
                yield return directory;
            }
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);

            // Reparse points would let a junction loop the walk back on itself.
            if (name.StartsWith('$') || IsReparsePoint(directory))
            {
                continue;
            }

            foreach (var found in SafeEnumerate(directory, depth - 1))
            {
                yield return found;
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
