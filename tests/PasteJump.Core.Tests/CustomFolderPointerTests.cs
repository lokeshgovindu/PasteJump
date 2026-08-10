using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The pointer file's handling of a custom folder - the one file that decides where a user's clips are, read
/// before the app has a window or a log to complain in.
/// </summary>
public sealed class CustomFolderPointerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "pastejump-tests",
        Guid.NewGuid().ToString("n"));

    public CustomFolderPointerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    private DataLocationPointer RoundTrip(DataLocationPointer pointer)
    {
        Assert.True(pointer.TryWrite(_directory));
        return DataLocationPointer.Read(_directory);
    }

    [Fact]
    public void A_custom_folder_survives_a_round_trip()
    {
        var written = new DataLocationPointer
        {
            ClipsLocation = DataLocation.CustomFolder,
            ClipsPath = @"D:\PasteJumpClips",
            SettingsLocation = DataLocation.ApplicationFolder,
        };

        var read = RoundTrip(written);

        Assert.Equal(DataLocation.CustomFolder, read.Clips);
        Assert.Equal(@"D:\PasteJumpClips", read.ClipsPath);
        Assert.Equal(DataLocation.ApplicationFolder, read.Settings);
    }

    /// <summary>Each half can name a different folder; they are independent by design.</summary>
    [Fact]
    public void The_two_halves_can_name_different_folders()
    {
        var read = RoundTrip(new DataLocationPointer
        {
            ClipsLocation = DataLocation.CustomFolder,
            ClipsPath = @"D:\Clips",
            SettingsLocation = DataLocation.CustomFolder,
            SettingsPath = @"E:\Settings",
        });

        Assert.Equal(@"D:\Clips", read.ClipsPath);
        Assert.Equal(@"E:\Settings", read.SettingsPath);
    }

    /// <summary>
    /// The failure this guards against: a custom location whose path went missing. Honouring it would leave the
    /// app pointed at nothing; defaulting is recoverable and visible.
    /// </summary>
    [Fact]
    public void A_custom_location_with_no_path_degrades_to_the_default()
    {
        File.WriteAllText(
            DataLocationPointer.PathFor(_directory),
            """{ "clipsLocation": "CustomFolder" }""");

        var read = DataLocationPointer.Read(_directory);

        Assert.Equal(DataLocation.ApplicationFolder, read.Clips);
    }

    /// <summary>
    /// A path left behind after switching back to a built-in location is dropped rather than kept, so editing
    /// the location alone by hand cannot resurrect a folder the user moved away from.
    /// </summary>
    [Fact]
    public void A_path_is_not_kept_for_a_non_custom_location()
    {
        File.WriteAllText(
            DataLocationPointer.PathFor(_directory),
            """{ "clipsLocation": "UserProfile", "clipsPath": "D:\\Stale" }""");

        var read = DataLocationPointer.Read(_directory);

        Assert.Equal(DataLocation.UserProfile, read.Clips);
        Assert.Null(read.ClipsPath);
    }

    [Fact]
    public void Switching_away_from_a_custom_folder_writes_no_path()
    {
        var read = RoundTrip(new DataLocationPointer
        {
            ClipsLocation = DataLocation.UserProfile,
            ClipsPath = @"D:\NoLongerUsed",
        });

        Assert.Null(read.ClipsPath);
        Assert.DoesNotContain(
            "NoLongerUsed",
            File.ReadAllText(DataLocationPointer.PathFor(_directory)),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A pending move records the root it is coming FROM as a path, which is what lets the next start-up adopt
    /// data out of a custom folder as readily as out of a built-in one.
    /// </summary>
    [Fact]
    public void A_move_out_of_a_custom_folder_records_the_old_root()
    {
        var read = RoundTrip(new DataLocationPointer
        {
            ClipsLocation = DataLocation.UserProfile,
            MigrateClipsFrom = @"D:\OldCustomFolder",
        });

        Assert.Equal(@"D:\OldCustomFolder", read.PendingClipsMove);
    }

    /// <summary>A custom folder is not the default, so the file must exist rather than being deleted.</summary>
    [Fact]
    public void A_custom_folder_is_not_treated_as_the_default()
    {
        var pointer = new DataLocationPointer
        {
            ClipsLocation = DataLocation.CustomFolder,
            ClipsPath = @"D:\Somewhere",
        };

        Assert.False(pointer.IsDefault);
        Assert.True(pointer.TryWrite(_directory));
        Assert.True(File.Exists(DataLocationPointer.PathFor(_directory)));
    }
}
