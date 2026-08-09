using System.Globalization;
using PasteJump.Core.Model;
using PasteJump.Core.Storage;
using Microsoft.Data.Sqlite;

namespace PasteJump.Import;

/// <summary>Outcome of an import run.</summary>
public sealed class ImportReport
{
    public int Imported { get; set; }

    public int Skipped { get; set; }

    /// <summary>
    /// History rows already present, so not imported again.
    /// <para>
    /// Counted separately from <see cref="Skipped"/>, which means "could not be imported". A duplicate is a
    /// success - it is the import being idempotent - and reporting the two as one number would make a second
    /// run of a clean import look like thousands of failures.
    /// </para>
    /// </summary>
    public int Duplicates { get; set; }

    /// <summary>
    /// Set when the user stopped the run part-way. Whatever had already been imported is kept: the import is
    /// idempotent, so resuming later simply skips those rows rather than duplicating them.
    /// </summary>
    public bool Cancelled { get; set; }

    /// <summary>
    /// Timestamp of the oldest entry actually imported, or null when nothing was.
    /// <para>
    /// Reported because it is the only way the caller can notice a conflict the user would otherwise discover
    /// as silent data loss: history retention prunes anything older than its cutoff, and it runs at start-up.
    /// Importing three years of Clipjump history under a 180-day retention setting therefore deletes most of
    /// what was just imported, at the next launch, without a word.
    /// </para>
    /// </summary>
    public DateTimeOffset? OldestImported { get; set; }

    /// <summary>Clip files replayed into the paste stack, so the gesture can reach them.</summary>
    public int ClipsImported { get; set; }

    /// <summary>
    /// Clip files that held nothing replayable — only formats whose ids are session-scoped, so their meaning
    /// cannot be recovered. Reported rather than hidden, because "995 of 1004" is a materially different
    /// outcome from "all of them" and the user is entitled to know which they got.
    /// </summary>
    public int ClipsSkipped { get; set; }

    public List<string> Errors { get; } = [];
}

/// <summary>How far an import has got, for a progress display.</summary>
/// <param name="Processed">Rows read so far.</param>
/// <param name="Total">Rows in the source, or 0 when it could not be counted.</param>
public readonly record struct ImportProgress(int Processed, int Total);

/// <summary>
/// Imports Clipjump 12.x history into a PasteJump store.
/// <para>
/// History only. The <c>.avc</c> clip files are not touched: they hold AutoHotkey's own
/// <c>ClipboardAll</c> serialisation - a sequence of <c>{format, size, bytes}</c> records - which
/// is reverse-engineerable but only worth doing for data that turns over in days. History is plain
/// text with timestamps, so it imports cleanly and is the part with lasting value.
/// </para>
/// <para>
/// The source database is opened read-only and nothing in the Clipjump folder is modified, so a
/// failed import leaves the user's existing installation exactly as it was.
/// </para>
/// </summary>
public static class LegacyClipjumpImporter
{
    /// <summary>Marker written to imported rows so they can be identified or rolled back later.</summary>
    public const string ProvenanceTag = "clipjump-12.5";

    /// <param name="progress">Reported after each row, for a progress display. Optional.</param>
    /// <param name="cancellationToken">
    /// Stops the run at the next row boundary, and interrupts the initial database copy between chunks.
    /// <para>
    /// Cancellation is not a nicety here. A Clipjump folder can live in OneDrive, where every file is a cloud
    /// placeholder until something opens it - so the database copy and each image read can each block on a
    /// download. Without a token the only way out of a slow import is killing the process.
    /// </para>
    /// </param>
    /// <param name="maxClips">
    /// How many <c>.avc</c> clip files to replay into the paste stack, newest first. Zero imports history only.
    /// Should be the store's own clip limit: importing more than the stack keeps would evict the excess
    /// immediately, and take the user's own recent clips with it.
    /// </param>
    public static ImportReport ImportHistory(
        string clipjumpFolder,
        ClipStore target,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int maxClips = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipjumpFolder);
        ArgumentNullException.ThrowIfNull(target);

        var report = new ImportReport();
        var databasePath = Path.Combine(clipjumpFolder, LegacyClipjumpLocator.DatabaseRelativePath);

        if (!File.Exists(databasePath))
        {
            report.Errors.Add($"No history database at {databasePath}");
            return report;
        }

        // Copy before reading. Opening the live file read-only would still be blocked if Clipjump
        // is running and holding it, and a copy removes any possibility of disturbing it.
        var tempCopy = Path.Combine(Path.GetTempPath(), $"pastejump-import-{Guid.NewGuid():n}.db");

        try
        {
            CopyCancellable(databasePath, tempCopy, cancellationToken);
            ImportFrom(tempCopy, clipjumpFolder, target, report, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            report.Cancelled = true;
        }
        catch (Exception ex)
        {
            report.Errors.Add(ex.Message);
        }
        finally
        {
            TryDelete(tempCopy);
        }

        // After history, and outside its try, so a history failure still leaves the clips importable and a clip
        // failure cannot lose the history that already succeeded.
        if (maxClips > 0 && !report.Cancelled)
        {
            try
            {
                ImportClips(clipjumpFolder, target, report, maxClips, progress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                report.Cancelled = true;
            }
            catch (Exception ex)
            {
                report.Errors.Add($"Clips: {ex.Message}");
            }
        }

        return report;
    }

    /// <summary>
    /// Replays Clipjump's <c>.avc</c> clip files into the paste stack, newest first, up to
    /// <paramref name="maxClips"/>.
    /// <para>
    /// This is what makes importing worth doing rather than merely searchable: history is a flattened archive
    /// the gesture cannot paste from, so without this an imported installation of thousands of entries left
    /// <c>Ctrl+V</c> with nothing new to offer.
    /// </para>
    /// <para>
    /// Newest first and capped, because the stack is bounded by a clip count. Importing a thousand clips into a
    /// store that keeps two hundred would spend the whole budget on the oldest ones read and then evict them,
    /// which is worse than useless - it would also push out the clips the user copied today.
    /// </para>
    /// </summary>
    private static void ImportClips(
        string clipjumpFolder,
        ClipStore target,
        ImportReport report,
        int maxClips,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(clipjumpFolder, "cache", "clips");

        if (!Directory.Exists(directory))
        {
            return;
        }

        // Ordered by write time, which is what "newest" means to the user, with the numeric file name as the
        // tie-break since Clipjump allocates those in sequence.
        var files = new DirectoryInfo(directory)
            .EnumerateFiles("*.avc")
            .OrderByDescending(static f => f.LastWriteTimeUtc)
            .ThenByDescending(static f => int.TryParse(Path.GetFileNameWithoutExtension(f.Name), out var n) ? n : 0)
            .Take(maxClips)
            .ToList();

        // Reported from zero again for this phase. A clip pass can run a minute on a large installation - image
        // clips are multi-megabyte uncompressed DIBs - and a dialog whose bar sat still for that long after
        // finishing the history would look like a hang, which is how a slow import gets killed half-done.
        var processed = 0;
        progress?.Report(new ImportProgress(0, files.Count));

        // Oldest of the selected set first, so the newest Clipjump clip ends up newest in the stack rather than
        // buried under the others.
        foreach (var file in Enumerable.Reverse(files))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var payloads = ClipjumpClipFile.TryReadPayloads(File.ReadAllBytes(file.FullName));

                if (payloads.Count == 0)
                {
                    report.ClipsSkipped++;
                    continue;
                }

                var text = Win32TextOf(payloads);
                var snapshot = new ClipboardSnapshot(payloads, text, KindOf(payloads, text), ProvenanceTag);

                // Duplicates NOT allowed, which is a reversal: it was true here on the reasoning that two
                // Clipjump clips holding the same text are two clips the user kept. That reasoning ignored the
                // second run - importing twice made a second copy of every clip, the same way the history import
                // did. Add promotes the existing clip instead, so a re-import refreshes the order rather than
                // doubling the stack, and the report counts it as a duplicate rather than as an import.
                target.Add(snapshot, allowDuplicates: false, out var wasNewClip);

                if (wasNewClip)
                {
                    report.ClipsImported++;
                }
                else
                {
                    report.Duplicates++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.ClipsSkipped++;

                if (report.Errors.Count < 20)
                {
                    report.Errors.Add($"{file.Name}: {ex.Message}");
                }
            }

            // Outside the try, as in the history pass, so a file that failed still advances the bar.
            progress?.Report(new ImportProgress(++processed, files.Count));
        }
    }

    /// <summary>
    /// The text of an imported payload set, decoded from <c>CF_UNICODETEXT</c> only.
    /// <para>
    /// No <c>CF_TEXT</c> fallback, for the reason recorded in <c>Win32ClipboardAccess.ExtractText</c>: that
    /// format is in the system ANSI codepage while .NET's <c>Encoding.Default</c> is UTF-8, so decoding it
    /// that way would mangle every non-ASCII character.
    /// </para>
    /// </summary>
    private static string? Win32TextOf(IReadOnlyList<ClipPayload> payloads)
    {
        var unicode = payloads.FirstOrDefault(static p => p.FormatId == 13);

        if (unicode is null)
        {
            return null;
        }

        var text = System.Text.Encoding.Unicode.GetString(unicode.Data);
        var nul = text.IndexOf('\0', StringComparison.Ordinal);

        return nul >= 0 ? text[..nul] : text;
    }

    private static ClipKind KindOf(IReadOnlyList<ClipPayload> payloads, string? text)
    {
        if (payloads.Any(static p => p.FormatId == 15))
        {
            return ClipKind.Files;
        }

        if (payloads.Any(static p => p.FormatId is 8 or 17))
        {
            return ClipKind.Image;
        }

        return string.IsNullOrEmpty(text) ? ClipKind.Other : ClipKind.Text;
    }

    /// <summary>
    /// Copies in chunks, checking the token between them.
    /// <para>
    /// <see cref="File.Copy(string, string)"/> cannot be interrupted, and on a cloud-backed folder it is the
    /// single longest blocking call in the whole import - the database has to be downloaded in full before the
    /// first row can be read. Chunking is the only way to make that stretch abandonable.
    /// </para>
    /// </summary>
    private static void CopyCancellable(string source, string destination, CancellationToken cancellationToken)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[64 * 1024];
        int read;

        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
    }

    private static void ImportFrom(
        string databasePath,
        string clipjumpFolder,
        ClipStore target,
        ImportReport report,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,

            // Pooling off, deliberately. Microsoft.Data.Sqlite keeps the native handle alive in a
            // pool after Dispose, so with pooling on the temp copy below stays locked and the
            // cleanup delete silently fails - leaking a full copy of the user's history database
            // into %TEMP% on every import.
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var total = CountRows(connection);
        var processed = 0;

        using var command = connection.CreateCommand();

        // Column set from Clipjump's createHisTable (History GUI Plug.ahk:638):
        //   id, data TEXT, type INTEGER, fileid TEXT, time TEXT, size INTEGER
        // type 0 = text with content in `data`; type 1 = image with a relative path in `fileid`.
        command.CommandText = "SELECT id, data, type, fileid, time, size FROM history ORDER BY id;";

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            // Checked per row rather than per batch. On a cloud-backed folder a single image row can block for
            // seconds on its download, so the gap between checks is already as long as it should be.
            if (cancellationToken.IsCancellationRequested)
            {
                report.Cancelled = true;
                break;
            }

            try
            {
                var type = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                var data = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var fileId = reader.IsDBNull(3) ? null : reader.GetString(3);
                var timeText = reader.IsDBNull(4) ? null : reader.GetString(4);
                var size = reader.IsDBNull(5) ? 0L : reader.GetInt64(5);

                var captured = ParseLegacyTimestamp(timeText);

                if (type != 1 && string.IsNullOrEmpty(data))
                {
                    // Nothing to import and nothing to report: an empty text row carried no content in
                    // Clipjump either.
                    report.Skipped++;
                }
                else
                {
                    // AddHistoryIfAbsent rather than AddHistory, which is what makes running this twice
                    // harmless. The dialog claimed exactly that for months while nothing checked, so a history
                    // imported four times held four copies of everything - 28,488 rows where 7,122 were meant.
                    var inserted = type == 1
                        ? target.AddHistoryIfAbsent(
                            captured,
                            ClipKind.Image,
                            string.IsNullOrWhiteSpace(data) ? "[image]" : data,
                            TryReadLegacyImage(clipjumpFolder, fileId),
                            size,
                            ProvenanceTag)
                        : target.AddHistoryIfAbsent(captured, ClipKind.Text, data, null, size, ProvenanceTag);

                    if (inserted is null)
                    {
                        report.Duplicates++;
                    }
                    else
                    {
                        report.Imported++;

                        if (report.OldestImported is null || captured < report.OldestImported)
                        {
                            report.OldestImported = captured;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                report.Skipped++;

                if (report.Errors.Count < 20)
                {
                    report.Errors.Add(ex.Message);
                }
            }

            // Outside the try, so a row that threw still advances the count and the bar cannot stall on a run
            // that is making progress through failures.
            progress?.Report(new ImportProgress(++processed, total));
        }
    }

    /// <summary>Row count for the progress display, or 0 when the source will not give one.</summary>
    private static int CountRows(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM history;";

            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            // A determinate bar is a nicety; failing the whole import for the want of one is not.
            return 0;
        }
    }

    /// <summary>
    /// Parses Clipjump's <c>YYYY-MM-DD HH:MM:SS</c> timestamps (written by its
    /// <c>convertTimeSql</c> helper).
    /// <para>
    /// These carry no timezone and were produced from AutoHotkey's <c>A_Now</c>, which is local
    /// time. They are therefore interpreted as local and converted, not read as UTC - treating
    /// them as UTC would shift every imported entry by the user's offset.
    /// </para>
    /// </summary>
    internal static DateTimeOffset ParseLegacyTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTimeOffset.UnixEpoch;
        }

        if (DateTime.TryParseExact(
                value.Trim(),
                ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyyMMddHHmmss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local)).ToUniversalTime();
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var loose)
            ? loose.ToUniversalTime()
            : DateTimeOffset.UnixEpoch;
    }

    private static byte[]? TryReadLegacyImage(string clipjumpFolder, string? fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return null;
        }

        // fileId is stored relative to the Clipjump folder, e.g. "cache\history\ab12cd.jpg".
        var candidate = Path.Combine(clipjumpFolder, fileId);

        // Guard against a malformed or hostile path escaping the source folder.
        var full = Path.GetFullPath(candidate);
        var root = Path.GetFullPath(clipjumpFolder);

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
