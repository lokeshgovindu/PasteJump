using PasteJump.Core;
using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The data-location pointer and the moves it drives.
/// <para>
/// Clips and settings are located independently, which is the thing most of these tests are pinning down.
/// Both halves are worth testing because both failures are silent and one of them loses a clipboard
/// history: a pointer that fails to round-trip sends the app to the wrong directory, where it finds no
/// database and looks like it has forgotten everything, and a move that overwrites rather than declining
/// destroys whatever was already at the destination.
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

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ------------------------------------------------------------------ pointer

    [Fact]
    public void No_pointer_file_means_the_application_folder_for_both()
    {
        var pointer = DataLocationPointer.Read(Dir("fresh"));

        Assert.Equal(DataLocation.ApplicationFolder, pointer.Clips);
        Assert.Equal(DataLocation.ApplicationFolder, pointer.Settings);
        Assert.Null(pointer.PendingClipsMove);
        Assert.Null(pointer.PendingSettingsMove);
        Assert.True(pointer.IsDefault);
    }

    [Fact]
    public void The_two_halves_round_trip_independently()
    {
        // The whole point of the split: clips in the profile, settings beside the program, so a portable
        // copy carries its own configuration while the history is shared between builds.
        var dir = Dir("roundtrip");

        var written = new DataLocationPointer
        {
            ClipsLocation = DataLocation.UserProfile,
            SettingsLocation = DataLocation.ApplicationFolder,
            MigrateClipsFrom = @"C:\somewhere\old",
        };

        Assert.True(written.TryWrite(dir));

        var read = DataLocationPointer.Read(dir);

        Assert.Equal(DataLocation.UserProfile, read.Clips);
        Assert.Equal(DataLocation.ApplicationFolder, read.Settings);
        Assert.Equal(@"C:\somewhere\old", read.PendingClipsMove);
        Assert.Null(read.PendingSettingsMove);
    }

    [Fact]
    public void Settings_can_move_while_clips_stay_put()
    {
        var dir = Dir("settingsonly");

        Assert.True(new DataLocationPointer { SettingsLocation = DataLocation.UserProfile }.TryWrite(dir));

        var read = DataLocationPointer.Read(dir);

        Assert.Equal(DataLocation.ApplicationFolder, read.Clips);
        Assert.Equal(DataLocation.UserProfile, read.Settings);
    }

    [Fact]
    public void Writing_the_defaults_removes_the_file_rather_than_storing_it()
    {
        // No file at all is the canonical spelling of "defaults, nothing pending", so a portable folder
        // that has never been reconfigured stays clean - and switching back tidies up after itself rather
        // than leaving a file that says the same thing.
        var dir = Dir("default");
        var path = DataLocationPointer.PathFor(dir);

        Assert.True(new DataLocationPointer { ClipsLocation = DataLocation.UserProfile }.TryWrite(dir));
        Assert.True(File.Exists(path));

        Assert.True(new DataLocationPointer { ClipsLocation = DataLocation.ApplicationFolder }.TryWrite(dir));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Enums_are_stored_by_name_so_the_file_stays_readable()
    {
        var dir = Dir("byname");

        new DataLocationPointer { ClipsLocation = DataLocation.UserProfile }.TryWrite(dir);

        // Reordering the enum must not silently repoint an existing install, which is exactly what
        // storing the integer would do.
        Assert.Contains("UserProfile", File.ReadAllText(DataLocationPointer.PathFor(dir)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{ "clipsLocation": "Nonsense" }""")]
    public void An_unusable_pointer_degrades_to_the_defaults(string content)
    {
        // This runs before the app has a window or a log, so throwing here is a process that dies with no
        // explanation. The wrong data directory is recoverable; a silent failure to start is not.
        var dir = Dir($"corrupt{content.Length}{Math.Abs(content.GetHashCode(StringComparison.Ordinal))}");
        File.WriteAllText(DataLocationPointer.PathFor(dir), content);

        var pointer = DataLocationPointer.Read(dir);

        Assert.Equal(DataLocation.ApplicationFolder, pointer.Clips);
        Assert.Equal(DataLocation.ApplicationFolder, pointer.Settings);
    }

    [Fact]
    public void A_blank_migration_source_is_normalised_to_null()
    {
        var dir = Dir("blankmigrate");
        File.WriteAllText(
            DataLocationPointer.PathFor(dir),
            """{ "clipsLocation": "UserProfile", "migrateClipsFrom": "   " }""");

        Assert.Null(DataLocationPointer.Read(dir).PendingClipsMove);
    }

    // ------------------------------------------------------------------ the superseded single setting

    [Fact]
    public void A_pointer_from_the_single_setting_build_applies_to_both_halves()
    {
        // Dropping this instead would send an existing install back to the application folder without
        // saying so, where it would find no database and appear to have lost every clip.
        var dir = Dir("legacy");
        File.WriteAllText(
            DataLocationPointer.PathFor(dir),
            """{ "location": "UserProfile", "migrateFrom": "C:\\old" }""");

        var pointer = DataLocationPointer.Read(dir);

        Assert.Equal(DataLocation.UserProfile, pointer.Clips);
        Assert.Equal(DataLocation.UserProfile, pointer.Settings);
        Assert.Equal(@"C:\old", pointer.PendingClipsMove);
        Assert.Equal(@"C:\old", pointer.PendingSettingsMove);
    }

    [Fact]
    public void An_explicit_half_wins_over_the_legacy_value()
    {
        var dir = Dir("legacymixed");
        File.WriteAllText(
            DataLocationPointer.PathFor(dir),
            """{ "location": "UserProfile", "settingsLocation": "ApplicationFolder" }""");

        var pointer = DataLocationPointer.Read(dir);

        Assert.Equal(DataLocation.UserProfile, pointer.Clips);
        Assert.Equal(DataLocation.ApplicationFolder, pointer.Settings);
    }

    [Fact]
    public void Rewriting_a_legacy_pointer_upgrades_it_to_the_two_field_form()
    {
        var dir = Dir("legacyupgrade");
        var path = DataLocationPointer.PathFor(dir);
        File.WriteAllText(path, """{ "location": "UserProfile" }""");

        Assert.True(DataLocationPointer.Read(dir).TryWrite(dir));

        var json = File.ReadAllText(path);

        Assert.Contains("clipsLocation", json, StringComparison.Ordinal);
        Assert.Contains("settingsLocation", json, StringComparison.Ordinal);

        // The superseded key is gone, so the fallback stops being consulted at all.
        Assert.DoesNotContain("\"location\"", json, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ roots

    [Fact]
    public void The_user_profile_root_is_under_local_appdata()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PasteJump");

        Assert.Equal(expected, AppPaths.RootFor(DataLocation.UserProfile));

        // Local, not Roaming: a clipboard history is machine-specific and grows without bound once images
        // are stored, so roaming it would push it through the profile sync quota.
        Assert.DoesNotContain("Roaming", AppPaths.RootFor(DataLocation.UserProfile), StringComparison.Ordinal);
    }

    [Fact]
    public void Separate_roots_put_the_database_and_the_settings_file_in_different_places()
    {
        var paths = AppPaths.At(Dir("clips-root"), Dir("settings-root"));

        Assert.StartsWith(paths.ClipsRoot, paths.DatabaseFile, StringComparison.Ordinal);
        Assert.StartsWith(paths.SettingsRoot, paths.SettingsFile, StringComparison.Ordinal);

        // Blobs follow the clips, not the settings: they are runtime data that grows, not configuration you
        // would want travelling with a portable copy.
        Assert.StartsWith(paths.ClipsRoot, paths.BlobsDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCreated_creates_both_sides()
    {
        var paths = AppPaths.At(Dir("ec-clips"), Dir("ec-settings"));

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.BlobsDirectory));
        Assert.True(Directory.Exists(paths.SettingsDirectory));

        // Nothing else. The app has no logger, so it must not leave an empty data\logs folder behind.
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(paths.ClipsDirectory),
            d => Path.GetFileName(d).Equals("logs", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ migration

    private string SeedSource(string name)
    {
        var root = Dir(name);
        var paths = AppPaths.At(root);
        paths.EnsureCreated();

        File.WriteAllText(paths.DatabaseFile, "database");
        File.WriteAllText(paths.DatabaseFile + "-wal", "write ahead log");
        File.WriteAllText(paths.SettingsFile, """{ "maxClips": 42 }""");

        // Nested one level down, mirroring the real blob store's two-character fan-out directories. A flat
        // copy would pass a test that only used top-level files.
        var fanOut = Path.Combine(paths.BlobsDirectory, "ab");
        Directory.CreateDirectory(fanOut);
        File.WriteAllText(Path.Combine(fanOut, "cdef"), "blob");

        return root;
    }

    [Fact]
    public void Moving_clips_copies_the_database_its_sidecars_and_nested_blobs()
    {
        var from = SeedSource("move-from");
        var to = Dir("move-to");

        var report = DataMigrator.AdoptClips(from, to);

        Assert.True(report.Adopted);
        Assert.Null(report.Error);

        var target = AppPaths.At(to);

        Assert.Equal("database", File.ReadAllText(target.DatabaseFile));

        // SQLite derives the sidecar names from the database filename, so leaving them behind would
        // discard whatever the log had not yet checkpointed.
        Assert.Equal("write ahead log", File.ReadAllText(target.DatabaseFile + "-wal"));
        Assert.Equal("blob", File.ReadAllText(Path.Combine(target.BlobsDirectory, "ab", "cdef")));
    }

    [Fact]
    public void Moving_clips_leaves_the_settings_file_behind()
    {
        // The two halves are independent, so moving one must not drag the other along - otherwise
        // "clips in the profile, settings beside the program" would be unreachable.
        var from = SeedSource("clips-only-from");
        var to = Dir("clips-only-to");

        DataMigrator.AdoptClips(from, to);

        Assert.False(File.Exists(AppPaths.At(to).SettingsFile));
        Assert.True(File.Exists(AppPaths.At(from).SettingsFile));
    }

    [Fact]
    public void Moving_settings_copies_only_the_settings_file()
    {
        var from = SeedSource("settings-from");
        var to = Dir("settings-to");

        var report = DataMigrator.AdoptSettings(from, to);

        Assert.True(report.Adopted);
        Assert.Equal(1, report.FilesCopied);

        var target = AppPaths.At(to);

        Assert.Equal("""{ "maxClips": 42 }""", File.ReadAllText(target.SettingsFile));
        Assert.False(File.Exists(target.DatabaseFile));
        Assert.False(Directory.Exists(Path.Combine(target.BlobsDirectory, "ab")));
    }

    [Fact]
    public void A_move_never_removes_the_source()
    {
        // A clipboard history is the one thing here that cannot be regenerated. Copy-then-delete is how a
        // tidy-up becomes data loss, so the old folder is left for the user to remove.
        var from = SeedSource("keep-from");

        DataMigrator.AdoptClips(from, Dir("keep-to"));

        Assert.Equal("database", File.ReadAllText(AppPaths.At(from).DatabaseFile));
    }

    [Fact]
    public void A_destination_that_already_has_a_database_is_left_alone()
    {
        var from = SeedSource("clash-from");
        var to = Dir("clash-to");

        var target = AppPaths.At(to);
        target.EnsureCreated();
        File.WriteAllText(target.DatabaseFile, "existing history");

        var report = DataMigrator.AdoptClips(from, to);

        Assert.False(report.Adopted);
        Assert.Equal(0, report.FilesCopied);

        // Two histories cannot be merged by copying files over each other - the blobs are addressed by
        // content but the database rows are not - and overwriting would discard whichever one the user had
        // been using.
        Assert.Equal("existing history", File.ReadAllText(target.DatabaseFile));
    }

    [Fact]
    public void A_destination_that_already_has_settings_is_left_alone()
    {
        var from = SeedSource("sclash-from");
        var to = Dir("sclash-to");

        var target = AppPaths.At(to);
        target.EnsureCreated();
        File.WriteAllText(target.SettingsFile, """{ "maxClips": 7 }""");

        Assert.False(DataMigrator.AdoptSettings(from, to).Adopted);
        Assert.Equal("""{ "maxClips": 7 }""", File.ReadAllText(target.SettingsFile));
    }

    [Fact]
    public void Moving_to_the_same_place_does_nothing()
    {
        var from = SeedSource("same");

        Assert.False(DataMigrator.AdoptClips(from, from).Adopted);
        Assert.False(DataMigrator.AdoptSettings(from, from).Adopted);
    }

    [Fact]
    public void A_source_with_no_data_folder_does_nothing()
    {
        Assert.False(DataMigrator.AdoptClips(Dir("empty-from"), Dir("empty-to")).Adopted);
        Assert.False(DataMigrator.AdoptSettings(Dir("empty-from2"), Dir("empty-to2")).Adopted);
    }

    [Fact]
    public void Interrupted_temp_files_are_not_carried_over()
    {
        var from = SeedSource("temp-from");
        var to = Dir("temp-to");

        // Left behind by an interrupted settings save. Copying it would resurrect rubbish at the new
        // location, where it would sit for ever because nothing looks for it.
        File.WriteAllText(AppPaths.At(from).SettingsFile + ".tmp", "half-written");

        DataMigrator.AdoptSettings(from, to);

        Assert.False(File.Exists(AppPaths.At(to).SettingsFile + ".tmp"));
        Assert.True(File.Exists(AppPaths.At(to).SettingsFile));
    }
}
