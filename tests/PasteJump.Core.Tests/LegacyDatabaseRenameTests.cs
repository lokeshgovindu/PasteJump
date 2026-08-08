using PasteJump.Core;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The one-time database rename left over from the Clipjog to PasteJump rename.
/// <para>
/// Worth testing rather than eyeballing, because the failure is silent: an existing install would open
/// an empty database, show no clips, and give no indication that the data was still on disk under a
/// name nothing looks for.
/// </para>
/// </summary>
public sealed class LegacyDatabaseRenameTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public LegacyDatabaseRenameTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pastejump-rename-tests", Guid.NewGuid().ToString("n"));
        _paths = AppPaths.At(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string LegacyPath => Path.Combine(_paths.ClipsDirectory, "clipjog.db");

    [Fact]
    public void A_legacy_database_is_renamed()
    {
        File.WriteAllText(LegacyPath, "clip data");

        Assert.True(_paths.TryMigrateLegacyDatabase());

        Assert.False(File.Exists(LegacyPath));
        Assert.True(File.Exists(_paths.DatabaseFile));
        Assert.Equal("clip data", File.ReadAllText(_paths.DatabaseFile));
    }

    [Fact]
    public void The_write_ahead_log_sidecars_move_with_it()
    {
        // SQLite derives -wal and -shm names from the database filename, so leaving them behind would
        // discard anything the log had not yet checkpointed.
        File.WriteAllText(LegacyPath, "db");
        File.WriteAllText(LegacyPath + "-wal", "wal");
        File.WriteAllText(LegacyPath + "-shm", "shm");

        Assert.True(_paths.TryMigrateLegacyDatabase());

        Assert.Equal("wal", File.ReadAllText(_paths.DatabaseFile + "-wal"));
        Assert.Equal("shm", File.ReadAllText(_paths.DatabaseFile + "-shm"));
        Assert.False(File.Exists(LegacyPath + "-wal"));
    }

    [Fact]
    public void An_existing_database_is_never_overwritten()
    {
        // Both present means the app has already run under the new name. Overwriting live data to tidy
        // up a filename would be far worse than leaving a stray file behind.
        File.WriteAllText(LegacyPath, "old");
        File.WriteAllText(_paths.DatabaseFile, "current");

        Assert.False(_paths.TryMigrateLegacyDatabase());

        Assert.Equal("current", File.ReadAllText(_paths.DatabaseFile));
        Assert.True(File.Exists(LegacyPath));
    }

    [Fact]
    public void A_fresh_install_does_nothing_and_reports_nothing()
    {
        Assert.False(_paths.TryMigrateLegacyDatabase());
        Assert.False(File.Exists(_paths.DatabaseFile));
    }

    [Fact]
    public void Running_it_twice_is_harmless()
    {
        File.WriteAllText(LegacyPath, "db");

        Assert.True(_paths.TryMigrateLegacyDatabase());
        Assert.False(_paths.TryMigrateLegacyDatabase());

        Assert.Equal("db", File.ReadAllText(_paths.DatabaseFile));
    }
}
