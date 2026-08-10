namespace PasteJump.Core.Settings;

/// <summary>Why a custom folder cannot be used, or <see cref="Ok"/> when it can.</summary>
public enum CustomFolderProblem
{
    /// <summary>Usable.</summary>
    Ok,

    /// <summary>Nothing was entered.</summary>
    Empty,

    /// <summary>Not a full path, or not a legal one.</summary>
    NotAFullPath,

    /// <summary>A file of that name already exists.</summary>
    IsAFile,

    /// <summary>The folder does not exist and could not be created.</summary>
    CannotCreate,

    /// <summary>The folder exists but nothing can be written into it.</summary>
    NotWritable,
}

/// <summary>
/// Validating a folder the user typed or browsed to, before anything is stored in it.
/// <para>
/// Checked up front rather than discovered later, because the failure this prevents is the worst one this
/// application has: accept a path that cannot be written, restart onto it, and the database cannot be opened -
/// so the app appears to have lost every clip. The old data is still where it was, but nothing on screen says
/// so.
/// </para>
/// <para>
/// In <c>Core</c> and therefore testable, which matters: the interesting cases are the ones a developer never
/// types by hand - a path that is a file, a relative path, a folder inside <c>C:\Program Files</c>.
/// </para>
/// </summary>
public static class CustomDataFolder
{
    /// <summary>
    /// Whether this path can hold a data folder, and why not when it cannot.
    /// <para>
    /// Deliberately <em>creates</em> the folder when it is missing rather than only inspecting it. "Can I write
    /// here" cannot be answered from the path alone on Windows - permissions, redirection and read-only volumes
    /// all lie to inspection - so the check that counts is doing it. A folder created here and then not used is
    /// an empty directory, which is a far smaller cost than the failure above.
    /// </para>
    /// </summary>
    /// <param name="path">The folder to hold <c>data</c>, not the <c>data</c> folder itself.</param>
    /// <param name="resolved">The path in full, canonical form when the result is <see cref="CustomFolderProblem.Ok"/>.</param>
    public static CustomFolderProblem Validate(string? path, out string resolved)
    {
        resolved = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return CustomFolderProblem.Empty;
        }

        if (!TryCanonicalise(path, out var full))
        {
            return CustomFolderProblem.NotAFullPath;
        }

        if (File.Exists(full))
        {
            return CustomFolderProblem.IsAFile;
        }

        try
        {
            if (!Directory.Exists(full))
            {
                Directory.CreateDirectory(full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return CustomFolderProblem.CannotCreate;
        }

        // The write test. A GUID name so it cannot collide with anything the user keeps there, and deleted
        // immediately - the point is only whether the attempt succeeds.
        var probe = Path.Combine(full, $".pastejump-write-test-{Guid.NewGuid():n}");

        try
        {
            using (var stream = File.Create(probe))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CustomFolderProblem.NotWritable;
        }

        resolved = full;
        return CustomFolderProblem.Ok;
    }

    /// <summary>
    /// Turns a typed folder into one canonical form, or fails when it is not a usable full path.
    /// <para>
    /// The trailing separator is trimmed, and that is not tidiness. These paths get compared to decide whether
    /// the data has to be moved, and <see cref="Path.GetFullPath(string)"/> keeps a trailing slash - so
    /// <c>D:\Clips\</c> and <c>D:\Clips</c> would compare as different folders and offer to copy a database onto
    /// itself. <see cref="Path.TrimEndingDirectorySeparator(string)"/> is used rather than a manual trim because
    /// it leaves a root alone: <c>D:\</c> must not become <c>D:</c>, which means "the current directory on D:".
    /// </para>
    /// <para>
    /// Fully qualified is required. A relative path would resolve against the working directory, which for a
    /// process launched from the Startup folder is neither the program folder nor predictable.
    /// </para>
    /// </summary>
    public static bool TryCanonicalise(string? path, out string full)
    {
        full = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var trimmed = path.Trim();

            if (!Path.IsPathFullyQualified(trimmed))
            {
                return false;
            }

            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>A sentence for the user. Kept with the rule rather than restated by whoever draws the dialog.</summary>
    public static string Describe(CustomFolderProblem problem, string? path) => problem switch
    {
        CustomFolderProblem.Ok => string.Empty,

        CustomFolderProblem.Empty =>
            "Choose a folder to store in, or pick one of the other two locations.",

        CustomFolderProblem.NotAFullPath =>
            $"\"{path}\" is not a full path. Give a complete one, such as D:\\PasteJump.",

        CustomFolderProblem.IsAFile =>
            $"\"{path}\" is a file, not a folder.",

        CustomFolderProblem.CannotCreate =>
            $"\"{path}\" does not exist and could not be created. Check the drive and your permissions.",

        CustomFolderProblem.NotWritable =>
            $"Nothing can be written to \"{path}\". A folder under C:\\Program Files usually needs "
                + "administrator rights, which PasteJump does not run with.",

        _ => "That folder cannot be used.",
    };
}
