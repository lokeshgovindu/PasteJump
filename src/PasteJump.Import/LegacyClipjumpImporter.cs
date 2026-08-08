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
    /// Set when the user stopped the run part-way. Whatever had already been imported is kept: the import is
    /// idempotent, so resuming later simply skips those rows rather than duplicating them.
    /// </summary>
    public bool Cancelled { get; set; }

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
    public static ImportReport ImportHistory(
        string clipjumpFolder,
        ClipStore target,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
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

        return report;
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

                if (type == 1)
                {
                    var blob = TryReadLegacyImage(clipjumpFolder, fileId);

                    target.AddHistory(
                        captured,
                        ClipKind.Image,
                        string.IsNullOrWhiteSpace(data) ? "[image]" : data,
                        blob,
                        size,
                        ProvenanceTag);
                }
                else
                {
                    if (string.IsNullOrEmpty(data))
                    {
                        report.Skipped++;
                        continue;
                    }

                    target.AddHistory(captured, ClipKind.Text, data, null, size, ProvenanceTag);
                }

                report.Imported++;
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
