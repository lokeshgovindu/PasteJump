using PasteJump.Core;
using PasteJump.Core.Model;
using PasteJump.Core.Storage;
using PasteJump.Import;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Import from a synthetic Clipjump 12.x folder. The legacy schema is recreated here exactly as
/// <c>createHisTable</c> declares it in History GUI Plug.ahk:638.
/// </summary>
public sealed class LegacyImportTests : IDisposable
{
    private readonly string _root;
    private readonly string _legacyFolder;
    private readonly ClipStore _store;

    public LegacyImportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pastejump-import-tests", Guid.NewGuid().ToString("n"));
        _legacyFolder = Path.Combine(_root, "Clipjump_x64");

        Directory.CreateDirectory(Path.Combine(_legacyFolder, "cache", "history"));

        _store = new ClipStore(AppPaths.At(Path.Combine(_root, "pastejump")));
    }

    public void Dispose()
    {
        _store.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string CreateLegacyDatabase(Action<SqliteConnection> seed)
    {
        var path = Path.Combine(_legacyFolder, "cache", "data.db");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // Without this the seeding connection's handle lingers in the pool after Dispose and
            // the tests that check file-level behaviour cannot open the file themselves.
            Pooling = false,
        }.ToString());

        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE history (
                    id     INTEGER PRIMARY KEY AUTOINCREMENT,
                    data   TEXT,
                    type   INTEGER,
                    fileid TEXT,
                    time   TEXT,
                    size   INTEGER
                );
                """;
            create.ExecuteNonQuery();
        }

        seed(connection);
        return path;
    }

    private static void InsertLegacyRow(
        SqliteConnection connection,
        string? data,
        int type,
        string? fileId,
        string? time,
        long size)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (data, type, fileid, time, size)
            VALUES ($data, $type, $fileid, $time, $size);
            """;
        cmd.Parameters.AddWithValue("$data", (object?)data ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$fileid", (object?)fileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$time", (object?)time ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$size", size);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- locator

    [Fact]
    public void Locator_RecognisesAFolderWithAHistoryDatabase()
    {
        CreateLegacyDatabase(_ => { });

        Assert.True(LegacyClipjumpLocator.IsClipjumpFolder(_legacyFolder));
    }

    [Fact]
    public void Locator_RejectsAFolderWithoutOne()
    {
        var empty = Path.Combine(_root, "NotClipjump");
        Directory.CreateDirectory(empty);

        Assert.False(LegacyClipjumpLocator.IsClipjumpFolder(empty));
        Assert.False(LegacyClipjumpLocator.IsClipjumpFolder(string.Empty));
    }

    // ---------------------------------------------------------------- timestamps

    [Fact]
    public void Timestamps_AreTreatedAsLocalNotUtc()
    {
        // Clipjump wrote these from A_Now, which is local time, via its convertTimeSql helper.
        // Reading them as UTC would shift every imported row by the user's offset.
        var parsed = LegacyClipjumpImporter.ParseLegacyTimestamp("2024-03-15 14:30:00");

        var expected = new DateTimeOffset(
            DateTime.SpecifyKind(new DateTime(2024, 3, 15, 14, 30, 0), DateTimeKind.Local))
            .ToUniversalTime();

        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("20240315143000")]
    [InlineData("2024-03-15T14:30:00")]
    public void Timestamps_AcceptTheOtherFormatsSeenInTheWild(string value)
    {
        var parsed = LegacyClipjumpImporter.ParseLegacyTimestamp(value);

        Assert.Equal(2024, parsed.ToLocalTime().Year);
        Assert.Equal(3, parsed.ToLocalTime().Month);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date at all")]
    public void Timestamps_FallBackToEpochRatherThanThrowing(string? value)
    {
        // A single malformed row must not abort an import of thousands.
        Assert.Equal(DateTimeOffset.UnixEpoch, LegacyClipjumpImporter.ParseLegacyTimestamp(value));
    }

    // ---------------------------------------------------------------- import

    [Fact]
    public void ImportsTextEntries()
    {
        CreateLegacyDatabase(connection =>
        {
            InsertLegacyRow(connection, "first legacy clip", 0, null, "2024-01-01 09:00:00", 20);
            InsertLegacyRow(connection, "second legacy clip", 0, null, "2024-01-02 09:00:00", 21);
        });

        var report = RunImport(_legacyFolder);

        Assert.Equal(2, report.Imported);
        Assert.Empty(report.Errors);
        Assert.Equal(2, _store.HistoryCount);
    }

    [Fact]
    public void ImportedEntriesAreSearchable()
    {
        CreateLegacyDatabase(connection =>
            InsertLegacyRow(connection, "connection string for production", 0, null, "2024-01-01 09:00:00", 32));

        RunImport(_legacyFolder);

        var hits = _store.SearchHistory("connection");

        Assert.Single(hits);
        Assert.Equal(LegacyClipjumpImporter.ProvenanceTag, hits[0].ImportedFrom);
    }

    [Fact]
    public void ImportedRowsCarryProvenanceSoTheyCanBeIdentified()
    {
        CreateLegacyDatabase(connection =>
            InsertLegacyRow(connection, "legacy", 0, null, "2024-01-01 09:00:00", 6));

        _store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Text, "native entry", null, 12);
        RunImport(_legacyFolder);

        var all = _store.SearchHistory(null);

        Assert.Equal(2, all.Count);
        Assert.Single(all, e => e.ImportedFrom == LegacyClipjumpImporter.ProvenanceTag);
        Assert.Single(all, e => e.ImportedFrom is null);
    }

    [Fact]
    public void ImportsImageEntriesWithTheirBlob()
    {
        var imageBytes = new byte[512];
        Random.Shared.NextBytes(imageBytes);

        var relativePath = Path.Combine("cache", "history", "abc123.jpg");
        File.WriteAllBytes(Path.Combine(_legacyFolder, relativePath), imageBytes);

        CreateLegacyDatabase(connection =>
            InsertLegacyRow(connection, "[IMAGE]", 1, relativePath, "2024-01-01 09:00:00", imageBytes.Length));

        var report = RunImport(_legacyFolder);

        Assert.Equal(1, report.Imported);

        var entry = _store.SearchHistory(null).Single();
        Assert.Equal(ClipKind.Image, entry.Kind);
        Assert.NotNull(entry.BlobHash);
        Assert.Equal(imageBytes, _store.Blobs.TryRead(entry.BlobHash!));
    }

    [Fact]
    public void ImageEntryWithMissingFileStillImportsAsARecord()
    {
        CreateLegacyDatabase(connection =>
            InsertLegacyRow(connection, "[IMAGE]", 1, @"cache\history\gone.jpg", "2024-01-01 09:00:00", 100));

        var report = RunImport(_legacyFolder);

        // The file is gone but the record is still history worth keeping.
        Assert.Equal(1, report.Imported);
        Assert.Null(_store.SearchHistory(null).Single().BlobHash);
    }

    [Fact]
    public void RejectsAnImagePathEscapingTheSourceFolder()
    {
        CreateLegacyDatabase(connection =>
            InsertLegacyRow(connection, "[IMAGE]", 1, @"..\..\..\Windows\System32\config\SAM", "2024-01-01 09:00:00", 1));

        var report = RunImport(_legacyFolder);

        // Imported as a record, but the traversal must not have read anything.
        Assert.Equal(1, report.Imported);
        Assert.Null(_store.SearchHistory(null).Single().BlobHash);
    }

    [Fact]
    public void SkipsEmptyTextRows()
    {
        CreateLegacyDatabase(connection =>
        {
            InsertLegacyRow(connection, string.Empty, 0, null, "2024-01-01 09:00:00", 0);
            InsertLegacyRow(connection, null, 0, null, "2024-01-01 09:00:00", 0);
            InsertLegacyRow(connection, "real content", 0, null, "2024-01-01 09:00:00", 12);
        });

        var report = RunImport(_legacyFolder);

        Assert.Equal(1, report.Imported);
        Assert.Equal(2, report.Skipped);
    }

    [Fact]
    public void MissingDatabase_ReportsAnErrorRatherThanThrowing()
    {
        var report = RunImport(Path.Combine(_root, "nowhere"));

        Assert.Equal(0, report.Imported);
        Assert.NotEmpty(report.Errors);
    }

    [Fact]
    public void SourceFolderIsLeftUnmodified()
    {
        var databasePath = CreateLegacyDatabase(connection =>
            InsertLegacyRow(connection, "legacy", 0, null, "2024-01-01 09:00:00", 6));

        var before = File.GetLastWriteTimeUtc(databasePath);
        var sizeBefore = new FileInfo(databasePath).Length;

        RunImport(_legacyFolder);

        // The importer copies the database before reading precisely so an existing Clipjump
        // installation cannot be disturbed by a failed or partial import.
        Assert.Equal(before, File.GetLastWriteTimeUtc(databasePath));
        Assert.Equal(sizeBefore, new FileInfo(databasePath).Length);
    }

    [Fact]
    public void ImportIsNotBlockedByTheSourceFileBeingOpen()
    {
        var databasePath = CreateLegacyDatabase(connection =>
            InsertLegacyRow(connection, "legacy while running", 0, null, "2024-01-01 09:00:00", 20));

        // Simulates Clipjump still running and holding its database.
        using var holder = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var report = RunImport(_legacyFolder);

        Assert.Equal(1, report.Imported);
    }

    /// <summary>
    /// Runs an import, threading xUnit's own cancellation token through.
    /// <para>
    /// A helper rather than the raw call at ten sites: the importer takes a <c>CancellationToken</c>, and
    /// xUnit's analyser rightly wants the test's token passed so a cancelled run stops the work too.
    /// </para>
    /// </summary>
    private ImportReport RunImport(string folder, IProgress<ImportProgress>? progress = null)
        => LegacyClipjumpImporter.ImportHistory(
            folder,
            _store,
            progress,
            TestContext.Current.CancellationToken);

    // ---------------------------------------------------------------- cancellation and progress

    /// <summary>
    /// Synchronous progress sink, deliberately not <see cref="Progress{T}"/>.
    /// <para>
    /// <c>Progress&lt;T&gt;</c> marshals through the captured synchronisation context, and a test has none - so
    /// callbacks land on pool threads, arriving after the import has returned and racing each other into
    /// whatever collection they append to. That is right for the UI and useless for asserting on.
    /// </para>
    /// </summary>
    private sealed class RecordingProgress : IProgress<ImportProgress>
    {
        public List<ImportProgress> Reports { get; } = [];

        public Action<ImportProgress>? OnReport { get; init; }

        public void Report(ImportProgress value)
        {
            Reports.Add(value);
            OnReport?.Invoke(value);
        }
    }

    [Fact]
    public void An_import_reports_progress_for_every_row()
    {
        CreateLegacyDatabase(connection =>
        {
            for (var i = 0; i < 5; i++)
            {
                InsertLegacyRow(connection, $"row {i}", 0, null, "2024-01-01 09:00:00", 10);
            }
        });

        var progress = new RecordingProgress();

        var report = RunImport(_legacyFolder, progress);

        Assert.Equal(5, report.Imported);
        Assert.Equal([1, 2, 3, 4, 5], progress.Reports.Select(static p => p.Processed));
        Assert.All(progress.Reports, p => Assert.Equal(5, p.Total));
    }

    [Fact]
    public void A_cancelled_import_stops_and_says_so()
    {
        CreateLegacyDatabase(connection =>
        {
            for (var i = 0; i < 50; i++)
            {
                InsertLegacyRow(connection, $"row {i}", 0, null, "2024-01-01 09:00:00", 10);
            }
        });

        using var cancellation = new CancellationTokenSource();

        // Cancelled from inside the progress callback, which is the closest a test gets to the user pressing
        // Cancel part-way through - and being synchronous, it happens at a known row.
        var progress = new RecordingProgress
        {
            OnReport = p =>
            {
                if (p.Processed == 3)
                {
                    cancellation.Cancel();
                }
            },
        };

        var report = LegacyClipjumpImporter.ImportHistory(
            _legacyFolder, _store, progress, cancellation.Token);

        Assert.True(report.Cancelled);

        // Stopped at the row after the cancel, not at the end. Whatever was imported is kept, because the
        // import is idempotent and re-running resumes rather than duplicating - discarding the partial work
        // would make a slow import unresumable.
        Assert.Equal(3, report.Imported);
        Assert.Equal(3, progress.Reports.Count);
    }

    [Fact]
    public void An_import_cancelled_before_it_starts_imports_nothing()
    {
        CreateLegacyDatabase(connection =>
            InsertLegacyRow(connection, "never read", 0, null, "2024-01-01 09:00:00", 10));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var report = LegacyClipjumpImporter.ImportHistory(_legacyFolder, _store, null, cancellation.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(0, report.Imported);

        // Cancellation is not an error: the report carries the flag, not a message the UI would show as a
        // failure.
        Assert.Empty(report.Errors);
    }

    // ---------------------------------------------------------------- retention conflict

    [Fact]
    public void The_report_names_the_oldest_entry_it_imported()
    {
        // The only signal the caller has that retention is about to delete part of what was just imported.
        // Retention prunes anything older than its cutoff and runs at start-up, so importing three years of
        // Clipjump history under a 180-day setting silently loses most of it by the next launch. That was
        // reported as an import that appeared not to have worked.
        CreateLegacyDatabase(connection =>
        {
            InsertLegacyRow(connection, "recent", 0, null, "2026-08-01 09:00:00", 10);
            InsertLegacyRow(connection, "ancient", 0, null, "2023-08-20 09:46:44", 10);
            InsertLegacyRow(connection, "middle", 0, null, "2025-01-15 12:00:00", 10);
        });

        var report = RunImport(_legacyFolder);

        Assert.Equal(3, report.Imported);

        // Local time in, UTC out - the legacy timestamps carry no zone and came from AutoHotkey's A_Now, which
        // is local, so the date is what matters here rather than the exact instant.
        Assert.Equal(
            new DateTime(2023, 8, 20).Date,
            report.OldestImported!.Value.ToLocalTime().Date);
    }

    [Fact]
    public void Nothing_imported_means_no_oldest_entry()
    {
        // Null rather than DateTimeOffset.MinValue, so the caller cannot mistake "nothing imported" for
        // "imported something impossibly old" and prompt about a retention conflict that does not exist.
        CreateLegacyDatabase(connection =>
            InsertLegacyRow(connection, string.Empty, 0, null, "2026-08-01 09:00:00", 0));

        var report = RunImport(_legacyFolder);

        Assert.Equal(0, report.Imported);
        Assert.Null(report.OldestImported);
    }

    // ---------------------------------------------------------------- locating an installation

    [Fact]
    public void The_locator_never_offers_a_folder_under_temp()
    {
        // It used to. LocalApplicationData is one of the search roots and %LOCALAPPDATA%\Temp sits inside the
        // depth limit, so the locator offered this very test class's leftover fixtures - a folder named
        // Clipjump_x64 under clipjog-import-tests - in preference to the user's real installation. Temp is
        // transient by definition: copies of Clipjump land there, but nobody runs the one they use from there.
        var found = LegacyClipjumpLocator.FindLikelyInstallation();

        if (found is null)
        {
            return;
        }

        Assert.DoesNotContain(
            Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            found,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_folder_qualifies_on_its_database_not_its_name()
    {
        // The name proves nothing: a folder called Clipjump with no cache\data.db has nothing to import, and
        // one called anything else with a database has everything. This is what the import dialog validates
        // a browsed folder against.
        //
        // The fixture folder starts without a database - the constructor only makes the cache directories -
        // so it is unqualified until one exists, which is the negative case for free.
        Assert.False(LegacyClipjumpLocator.IsClipjumpFolder(_legacyFolder));

        CreateLegacyDatabase(connection =>
            InsertLegacyRow(connection, "anything", 0, null, "2024-01-01 09:00:00", 8));

        Assert.True(LegacyClipjumpFolderQualifies());

        var namedButEmpty = Path.Combine(_root, "Clipjump-but-empty");
        Directory.CreateDirectory(namedButEmpty);

        Assert.False(LegacyClipjumpLocator.IsClipjumpFolder(namedButEmpty));

        bool LegacyClipjumpFolderQualifies() => LegacyClipjumpLocator.IsClipjumpFolder(_legacyFolder);
    }
}
