using PasteJump.Core;
using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The data-location pointer and the move it drives.
/// <para>
/// Both halves are worth testing because both failures are silent and both lose a clipboard history.
/// A pointer that fails to round-trip sends the app to the wrong directory, where it finds no database
/// and looks like it has forgotten everything. A move that overwrites instead of declining destroys the
/// history that was already at the destination.
/// </para>
/// </summary>
public sealed class DataLocationTests : IDisposable
{
    private readonly string _root;

    public DataLocationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pastejump-datalocation-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_root);
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

    private string AppDir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ------------------------------------------------------------------ pointer

    [Fact]
    public void No_pointer_file_means_the_application_folder()
    {
        var pointer = DataLocationPointer.Read(AppDir("fresh"));

        Assert.Equal(DataLocation.ApplicationFolder, pointer.Location);
        Assert.Null(pointer.MigrateFrom);
    }

    [Fact]
    public void A_pointer_round_trips()
    {
        var dir = AppDir("roundtrip");

        var written = new DataLocationPointer
        {
            Location = DataLocation.UserProfile,
            MigrateFrom = @"C:\somewhere\old",
        };

        Assert.True(written.TryWrite(dir));

        var read = DataLocationPointer.Read(dir);

        Assert.Equal(DataLocation.UserProfile, read.Location);
        Assert.Equal(@"C:\somewhere\old", read.MigrateFrom);
    }

    [Fact]
    public void Writing_the_default_removes_the_file_rather_than_storing_it()
    {
        // No file at all is the canonical spelling of "default, nothing pending", so a portable folder
        // that has never been reconfigured stays clean - and switching back to the default tidies up
        // after itself rather than leaving a file that says the same thing.
        var dir = AppDir("default");
        var path = DataLocationPointer.PathFor(dir);

        Assert.True(new DataLocationPointer { Location = DataLocation.UserProfile }.TryWrite(dir));
        Assert.True(File.Exists(path));

        Assert.True(new DataLocationPointer { Location = DataLocation.ApplicationFolder }.TryWrite(dir));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void The_enum_is_stored_by_name_so_the_file_stays_readable()
    {
        var dir = AppDir("byname");

        new DataLocationPointer { Location = DataLocation.UserProfile }.TryWrite(dir);

        // Reordering the enum must not silently repoint an existing install, which is exactly what
        // storing the integer would do.
        Assert.Contains("UserProfile", File.ReadAllText(DataLocationPointer.PathFor(dir)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{ "location": "Nonsense" }""")]
    public void An_unusable_pointer_degrades_to_the_default(string content)
    {
        // This runs before the app has a window or a log, so throwing here is a process that dies with
        // no explanation. The wrong data directory is recoverable; a silent failure to start is not.
        var dir = AppDir($"corrupt{content.Length}{content.GetHashCode(StringComparison.Ordinal)}");
        File.WriteAllText(DataLocationPointer.PathFor(dir), content);

        var pointer = DataLocationPointer.Read(dir);

        Assert.Equal(DataLocation.ApplicationFolder, pointer.Location);
        Assert.Null(pointer.MigrateFrom);
    }

    [Fact]
    public void A_blank_migration_source_is_normalised_to_null()
    {
        var dir = AppDir("blankmigrate");
        File.WriteAllText(
            DataLocationPointer.PathFor(dir),
            """{ "location": "UserProfile", "migrateFrom": "   " }""");

        Assert.Null(DataLocationPointer.Read(dir).MigrateFrom);
    }

    // ------------------------------------------------------------------ roots

    [Fact]
    public void The_user_profile_root_is_under_local_appdata()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PasteJump");

        Assert.Equal(expected, AppPaths.RootFor(DataLocation.UserProfile));

        // Local, not Roaming: a clipboard history is machine-specific and grows without bound once
        // images are stored, so roaming it would push it through the profile sync quota.
        Assert.DoesNotContain("Roaming", AppPaths.RootFor(DataLocation.UserProfile), StringComparison.Ordinal);
    }

    [Fact]
    public void The_application_folder_root_is_where_the_executable_is()
        => Assert.Equal(AppPaths.ApplicationDirectory, AppPaths.RootFor(DataLocation.ApplicationFolder));

    // ------------------------------------------------------------------ migration

    private string SeedSource(string name)
    {
        var root = AppDir(name);
        var paths = AppPaths.At(root);
        paths.EnsureCreated();

        File.WriteAllText(paths.DatabaseFile, "database");
        File.WriteAllText(paths.SettingsFile, """{ "maxClips": 42 }""");

        // Nested one level down, mirroring the real blob store's two-character fan-out directories. A
        // flat copy would pass a test that only used top-level files.
        var fanOut = Path.Combine(paths.BlobsDirectory, "ab");
        Directory.CreateDirectory(fanOut);
        File.WriteAllText(Path.Combine(fanOut, "cdef"), "blob");

        return root;
    }

    [Fact]
    public void A_move_copies_the_database_settings_and_nested_blobs()
    {
        var from = SeedSource("move-from");
        var to = AppDir("move-to");

        var report = DataMigrator.Adopt(from, to);

        Assert.True(report.Adopted);
        Assert.Null(report.Error);

        var target = AppPaths.At(to);

        Assert.Equal("database", File.ReadAllText(target.DatabaseFile));
        Assert.Equal("""{ "maxClips": 42 }""", File.ReadAllText(target.SettingsFile));
        Assert.Equal("blob", File.ReadAllText(Path.Combine(target.BlobsDirectory, "ab", "cdef")));
    }

    [Fact]
    public void A_move_never_removes_the_source()
    {
        // A clipboard history is the one thing here that cannot be regenerated. Copy-then-delete is how
        // a tidy-up becomes data loss, so the old folder is left for the user to remove.
        var from = SeedSource("keep-from");

        DataMigrator.Adopt(from, AppDir("keep-to"));

        Assert.Equal("database", File.ReadAllText(AppPaths.At(from).DatabaseFile));
    }

    [Fact]
    public void A_destination_that_already_has_a_database_is_left_alone()
    {
        var from = SeedSource("clash-from");
        var to = AppDir("clash-to");

        var target = AppPaths.At(to);
        target.EnsureCreated();
        File.WriteAllText(target.DatabaseFile, "existing history");

        var report = DataMigrator.Adopt(from, to);

        Assert.False(report.Adopted);
        Assert.Equal(0, report.FilesCopied);

        // Two histories cannot be merged by copying files over each other, and overwriting would discard
        // whichever one the user had been using.
        Assert.Equal("existing history", File.ReadAllText(target.DatabaseFile));
    }

    [Fact]
    public void Moving_to_the_same_place_does_nothing()
    {
        var from = SeedSource("same");

        Assert.False(DataMigrator.Adopt(from, from).Adopted);
    }

    [Fact]
    public void A_source_with_no_data_folder_does_nothing()
        => Assert.False(DataMigrator.Adopt(AppDir("empty-from"), AppDir("empty-to")).Adopted);

    [Fact]
    public void Interrupted_temp_files_are_not_carried_over()
    {
        var from = SeedSource("temp-from");
        var to = AppDir("temp-to");

        // Left behind by an interrupted settings save. Copying it would resurrect rubbish at the new
        // location, where it would sit for ever because nothing looks for it.
        File.WriteAllText(AppPaths.At(from).SettingsFile + ".tmp", "half-written");

        DataMigrator.Adopt(from, to);

        Assert.False(File.Exists(AppPaths.At(to).SettingsFile + ".tmp"));
        Assert.True(File.Exists(AppPaths.At(to).SettingsFile));
    }
}
