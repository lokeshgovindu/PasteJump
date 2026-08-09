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
    public static string? TryDescribe(IReadOnlyList<ClipPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        var hdrop = payloads.FirstOrDefault(static p => p.FormatId == CfHdrop);

        if (hdrop is null)
        {
            return null;
        }

        var paths = TryReadPaths(hdrop.Data);

        return paths.Count == 0 ? null : Describe(paths);
    }

    /// <summary>Formats an already-parsed list. Separate so the wording can be tested without a payload.</summary>
    public static string Describe(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            return string.Empty;
        }

        // A single file gets its full path: it fits, and the folder is as useful as the name.
        if (paths.Count == 1)
        {
            return paths[0];
        }

        var count = paths.Count.ToString(CultureInfo.CurrentCulture);
        var shared = SharedDirectory(paths);

        if (shared is null)
        {
            // Mixed folders, so a name alone would be ambiguous - two files called report.docx from
            // different directories must not read as the same file twice.
            return $"{count} files{Environment.NewLine}{string.Join(", ", paths)}";
        }

        var names = paths.Select(static p => Path.GetFileName(p) is { Length: > 0 } n ? n : p);

        return $"{count} files in {shared}{Environment.NewLine}{string.Join(", ", names)}";
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
