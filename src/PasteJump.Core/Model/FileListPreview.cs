using System.Globalization;
using System.Text;

namespace PasteJump.Core.Model;

/// <summary>
/// Turns a <c>CF_HDROP</c> payload into something readable, for the history row, the paste overlay and the
/// copy notification.
/// <para>
/// Worth more than the cosmetics suggest: <c>history_fts</c> indexes the preview column, so while every file
/// copy was stored as the literal string <c>[files]</c>, searching history for a file name could never match
/// one. Naming the files is what makes them findable.
/// </para>
/// <para>
/// Every name is included rather than a first-few summary, for that reason - the abbreviation belongs to the
/// display, which truncates anyway, not to the stored record. The shared folder is stated once instead of
/// repeating it per file: a multiple selection almost always comes from one directory, and a column of
/// identical path prefixes is the least informative way to spend a narrow toast.
/// </para>
/// </summary>
public static class FileListPreview
{
    /// <summary><c>CF_HDROP</c>.</summary>
    public const uint CfHdrop = 15;

    /// <summary>
    /// The size of the <c>DROPFILES</c> header: a DWORD offset, a POINT, and two BOOLs. Identical on 32- and
    /// 64-bit, since every member is four bytes.
    /// </summary>
    private const int DropFilesHeaderSize = 20;

    /// <summary>
    /// Describes the file list in <paramref name="payloads"/>, or null when there is no usable
    /// <c>CF_HDROP</c> - in which case the caller should fall back to its own placeholder rather than
    /// inventing a description of nothing.
    /// </summary>
    /// <summary>
    /// How many paths are probed to see whether they are directories. Probing is a filesystem stat, and this
    /// runs on the capture path, so the count is bounded: a copy of several hundred files should not turn one
    /// clipboard notification into hundreds of disk touches. Anything past this is described as a file, which
    /// is what the overwhelming majority of a large selection is.
    /// </summary>
    private const int MaxDirectoryProbes = 64;

    public static string? TryDescribe(IReadOnlyList<ClipPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        var hdrop = payloads.FirstOrDefault(static p => p.FormatId == CfHdrop);

        if (hdrop is null)
        {
            return null;
        }

        var paths = TryReadPaths(hdrop.Data);

        if (paths.Count == 0)
        {
            return null;
        }

        var probes = 0;

        return Describe(paths, path => probes++ < MaxDirectoryProbes && LooksLikeDirectory(path));
    }

    /// <summary>
    /// Whether a path is a directory, decided conservatively.
    /// <para>
    /// UNC paths are never probed. A stat against an offline server blocks for seconds, and this is reached
    /// from the clipboard notification - the one place in the app where a slow call is a hang rather than a
    /// pause. Being wrong about a network folder costs a trailing backslash; being slow costs the copy.
    /// </para>
    /// </summary>
    private static bool LooksLikeDirectory(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            // Any of the several exceptions a bad path can raise. Not a directory as far as we can tell.
            return false;
        }
    }

    /// <summary>
    /// Formats an already-parsed list. Separate from <see cref="TryDescribe"/> so the wording can be tested
    /// without a payload, and so the directory test can be supplied rather than hitting the disk.
    /// </summary>
    /// <param name="isDirectory">
    /// Decides whether a path names a folder. Folders are marked with a trailing separator, which is the
    /// shortest unambiguous marker there is and the one Explorer and every shell already use.
    /// </param>
    public static string Describe(IReadOnlyList<string> paths, Func<string, bool>? isDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return string.Empty;
        }

        isDirectory ??= static _ => false;

        // Evaluated once per path: the caller's test may touch the disk, and the answer is needed both for
        // the header counts and for the trailing marker.
        var directories = paths.Select(p => isDirectory(p)).ToArray();
        var folderCount = directories.Count(static d => d);
        var header = CountHeader(paths.Count - folderCount, folderCount);

        // Even one item gets the header. Without it a single folder copy reads as nothing but a path, which
        // is indistinguishable from a text clip that happens to contain one - the confusion this fixes.
        if (paths.Count == 1)
        {
            return $"{header}{Environment.NewLine}{Mark(paths[0], directories[0])}";
        }

        var shared = SharedDirectory(paths);

        // One per line rather than comma-separated: a file list is a list, and at four or more names a run of
        // commas is markedly harder to scan. The toast joins them back for its two lines of room.
        if (shared is null)
        {
            // Mixed folders, so a name alone would be ambiguous - two files called report.docx from
            // different directories must not read as the same file twice.
            var full = paths.Select((p, i) => Mark(p, directories[i]));
            return $"{header}{Environment.NewLine}{string.Join(Environment.NewLine, full)}";
        }

        var names = paths.Select((p, i) =>
            Mark(Path.GetFileName(p) is { Length: > 0 } n ? n : p, directories[i]));

        return $"{header} in {shared}{Environment.NewLine}{string.Join(Environment.NewLine, names)}";
    }

    /// <summary>
    /// Reads the paths back out of a description produced by <see cref="Describe"/>.
    /// <para>
    /// Needed because a history row keeps only the preview text - the <c>CF_HDROP</c> payload is not stored
    /// alongside it - and the history window wants the paths again to show a thumbnail of a copied image.
    /// Parsing our own output is a coupling worth naming rather than hiding: it lives here, beside the writer,
    /// and is covered by round-trip tests so the two cannot drift apart silently.
    /// </para>
    /// <para>
    /// Returns an empty list for anything that is not one of our descriptions, including plain text that
    /// merely happens to contain a path.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> TryReadPathsFromDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return [];
        }

        var lines = description.ReplaceLineEndings("\n").Split('\n');

        if (lines.Length < 2)
        {
            return [];
        }

        var header = lines[0];

        // Only our own headers. Deliberately structural rather than a substring search: "see the file" contains
        // " file" too, and a test caught exactly that - plain text sprouting a thumbnail and a resolution,
        // claiming to be something it is not.
        if (!IsCountHeader(header))
        {
            return [];
        }

        var marker = header.IndexOf(" in ", StringComparison.Ordinal);
        var sharedDirectory = marker >= 0 ? header[(marker + 4)..] : null;

        var paths = new List<string>(lines.Length - 1);

        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0)
            {
                continue;
            }

            // The trailing separator is the folder marker, not part of the name.
            var entry = line.TrimEnd(Path.DirectorySeparatorChar);

            paths.Add(sharedDirectory is null ? entry : Path.Combine(sharedDirectory, entry));
        }

        return paths;
    }

    /// <summary>
    /// Whether a line is one of <see cref="CountHeader"/>'s: a number, then the word file or folder. Checking
    /// the shape is what keeps ordinary prose - "see the file below" - from being read as a file list.
    /// </summary>
    private static bool IsCountHeader(string header)
    {
        var parts = header.Split(' ', 3);

        if (parts.Length < 2 || !int.TryParse(parts[0], CultureInfo.CurrentCulture, out var count) || count < 1)
        {
            return false;
        }

        // The trailing comma of "2 files, 1 folder" belongs to the separator, not the noun.
        var noun = parts[1].TrimEnd(',');

        return noun is "file" or "files" or "folder" or "folders";
    }

    private static string Mark(string path, bool isDirectory)
        => isDirectory && !path.EndsWith(Path.DirectorySeparatorChar) ? path + Path.DirectorySeparatorChar : path;

    /// <summary>"3 files", "1 folder", "2 files, 1 folder" - so the kind is stated, not inferred.</summary>
    private static string CountHeader(int files, int folders)
    {
        var parts = new List<string>(2);

        if (files > 0)
        {
            parts.Add($"{files.ToString(CultureInfo.CurrentCulture)} {(files == 1 ? "file" : "files")}");
        }

        if (folders > 0)
        {
            parts.Add($"{folders.ToString(CultureInfo.CurrentCulture)} {(folders == 1 ? "folder" : "folders")}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The directory every path shares, or null when they differ. Compared case-insensitively, because
    /// Windows paths are.
    /// </summary>
    private static string? SharedDirectory(IReadOnlyList<string> paths)
    {
        string? shared = null;

        foreach (var path in paths)
        {
            string? directory;

            try
            {
                directory = Path.GetDirectoryName(path);
            }
            catch (ArgumentException)
            {
                // A malformed path from a misbehaving source. Treated as "no shared folder" rather than
                // allowed to throw out of a capture.
                return null;
            }

            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            if (shared is null)
            {
                shared = directory;
            }
            else if (!string.Equals(shared, directory, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return shared;
    }

    /// <summary>
    /// Parses the paths out of a <c>CF_HDROP</c> payload: a <c>DROPFILES</c> header, then a
    /// double-null-terminated list of paths at the offset it names.
    /// <para>
    /// Every field is bounds-checked and a malformed payload yields an empty list rather than an exception.
    /// This runs on the capture path, which is reached from the clipboard hook, and a throw there would lose
    /// the copy outright.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> TryReadPaths(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < DropFilesHeaderSize)
        {
            return [];
        }

        var listOffset = BitConverter.ToUInt32(data, 0);

        // fWide, at offset 16. Wide is what Explorer publishes; the ANSI form is handled because a payload
        // replayed from an older application can still carry it.
        var wide = BitConverter.ToInt32(data, 16) != 0;

        if (listOffset < DropFilesHeaderSize || listOffset >= (uint)data.Length)
        {
            return [];
        }

        var paths = new List<string>();
        var position = (int)listOffset;

        while (position < data.Length)
        {
            var text = wide ? ReadWide(data, ref position) : ReadAnsi(data, ref position);

            // The list ends with an empty string - the second half of the double null.
            if (text.Length == 0)
            {
                break;
            }

            paths.Add(text);
        }

        return paths;
    }

    private static string ReadWide(byte[] data, ref int position)
    {
        var start = position;

        while (position + 1 < data.Length
            && !(data[position] == 0 && data[position + 1] == 0))
        {
            position += 2;
        }

        var text = Encoding.Unicode.GetString(data, start, position - start);
        position += 2;

        return text;
    }

    private static string ReadAnsi(byte[] data, ref int position)
    {
        var start = position;

        while (position < data.Length && data[position] != 0)
        {
            position++;
        }

        var text = Encoding.Default.GetString(data, start, position - start);
        position++;

        return text;
    }
}
