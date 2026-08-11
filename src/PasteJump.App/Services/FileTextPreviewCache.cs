using System.Text;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;

namespace PasteJump.App.Services;

/// <summary>
/// Reads the first few lines of a copied text file, so the overlay can show its contents the way it already shows
/// a thumbnail for a copied image.
/// <para>
/// Deliberately the same shape as <see cref="FileThumbnailCache"/>, including its guards, because it runs in the
/// same place for the same reason: this is the gesture's redraw path, reached on every tap of the trigger key.
/// Anything slow here is felt as the overlay stuttering.
/// </para>
/// </summary>
internal static class FileTextPreviewCache
{
    /// <summary>
    /// How much of the file to read. Enough to fill the overlay several times over and small enough that reading
    /// it is not a decision - a 2 GB log must cost the same as a 2 KB one.
    /// </summary>
    private const int MaxBytes = 16 * 1024;

    /// <summary>
    /// Lines kept. The overlay can draw about sixteen; the rest is slack so a change to its height does not need
    /// this retuned.
    /// </summary>
    private const int MaxLines = 40;

    /// <summary>As small as the thumbnail cache, and for the same reason: the gesture walks a handful of clips.</summary>
    private const int Capacity = 8;

    private static readonly Dictionary<string, TextPreview> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Order = [];

    /// <summary>
    /// Extensions worth reading. An allow-list rather than a sniff, matching the thumbnail cache: guessing whether
    /// an arbitrary file is text means reading it first, which is the cost being avoided.
    /// <para>
    /// Deliberately excludes the office formats. A <c>.docx</c> is a zip archive - opening one as text produces
    /// binary noise, and the check below would reject it anyway, having read 16 KB to find out.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".markdown", ".json", ".xml", ".yml", ".yaml", ".csv", ".tsv", ".ini", ".cfg",
        ".conf", ".sql", ".html", ".htm", ".css", ".js", ".ts", ".cs", ".c", ".h", ".cpp", ".hpp", ".py", ".rb",
        ".go", ".rs", ".java", ".kt", ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".ahk", ".props", ".targets",
        ".csproj", ".sln", ".slnx", ".gitignore", ".editorconfig", ".toml",
    };

    /// <param name="Text">The lines read, joined - what the overlay draws.</param>
    /// <param name="Facts">Line count and whether it is partial, already rendered for the facts row.</param>
    /// <param name="FileBytes">The file's real size, not the size of what was read.</param>
    internal sealed record TextPreview(string Text, string Facts, long FileBytes);

    /// <summary>
    /// The preview for the first text file named in a <see cref="FileListPreview"/> description, or null when
    /// there is not one. Only the first: the overlay shows one thing, and a copy of forty files must not read
    /// forty of them to draw it.
    /// </summary>
    internal static TextPreview? TryGet(string? description)
    {
        foreach (var path in FileListPreview.TryReadPathsFromDescription(description))
        {
            if (!TextExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            // Never a network path. This is the gesture's redraw path, and a stat or read against an offline
            // server stalls for seconds - the same reason the folder probe and the thumbnail cache skip UNC.
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                continue;
            }

            var preview = Load(path);

            if (preview is not null)
            {
                return preview;
            }
        }

        return null;
    }

    /// <summary>Drops everything, for when the overlay's size changes and the line budget with it.</summary>
    internal static void Clear()
    {
        Entries.Clear();
        Order.Clear();
    }

    private static TextPreview? Load(string path)
    {
        if (Entries.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            var info = new FileInfo(path);

            if (!info.Exists || info.Length == 0)
            {
                return null;
            }

            var buffer = new byte[(int)Math.Min(MaxBytes, info.Length)];
            int read;

            using (var stream = File.OpenRead(path))
            {
                read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            }

            // A null byte in the first 16 KB means this is not text, whatever the extension claimed - a .csv that
            // is really a database, or a file someone renamed. Showing the mojibake would look like corruption.
            if (Array.IndexOf(buffer, (byte)0, 0, read) >= 0)
            {
                return null;
            }

            // Encoding detected from the preamble where there is one, UTF-8 otherwise. UTF-8 is also the right
            // fallback for plain ASCII, and a mis-detected legacy codepage costs a few wrong accents in a preview
            // rather than anything that matters.
            var text = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true)
                .ReadToEnd();

            var lines = text.Split('\n');
            var kept = Math.Min(lines.Length, MaxLines);

            // Truncated if we stopped short of the file, or dropped lines. The last line of a partial read is
            // usually cut mid-word, so it is dropped rather than shown broken.
            var partial = read < info.Length;
            var body = string.Join('\n', lines.Take(partial && kept > 1 ? kept - 1 : kept)).TrimEnd('\r', '\n');

            var preview = new TextPreview(
                body,
                TextMetrics.Describe(body, partial || lines.Length > MaxLines),
                info.Length);

            Remember(path, preview);
            return preview;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or System.Security.SecurityException)
        {
            // A file that cannot be read is not a preview and not an error worth showing: the path is already on
            // screen, which is what the clip actually holds.
            return null;
        }
    }

    private static void Remember(string path, TextPreview preview)
    {
        Entries[path] = preview;
        Order.Remove(path);
        Order.Add(path);

        while (Order.Count > Capacity)
        {
            Entries.Remove(Order[0]);
            Order.RemoveAt(0);
        }
    }
}
