using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Validating a folder the user named, and resolving it into the paths the app runs with.
/// <para>
/// Worth testing thoroughly because of what it prevents: accept a folder that cannot be written, restart onto
/// it, and the database cannot be opened - so the application looks as though it has lost every clip, while the
/// old data sits untouched somewhere nobody is looking.
/// </para>
/// </summary>
public sealed class CustomDataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "pastejump-tests",
        Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public void A_writable_folder_is_accepted_and_canonicalised()
    {
        Directory.CreateDirectory(_root);

        // Deliberately untidy - a trailing separator and a redundant segment - because this is what a typed
        // path looks like, and the resolved form is what gets written to the pointer file.
        var untidy = Path.Combine(_root, "sub", "..", "sub") + Path.DirectorySeparatorChar;

        Assert.Equal(CustomFolderProblem.Ok, CustomDataFolder.Validate(untidy, out var resolved));
        Assert.Equal(Path.Combine(_root, "sub"), resolved);
    }

    /// <summary>
    /// A folder that does not exist yet is created rather than refused. Someone naming a new folder in the
    /// dialog expects it to be made, and inspecting a path cannot answer "can I write here" on Windows anyway.
    /// </summary>
    [Fact]
    public void A_missing_folder_is_created()
    {
        var target = Path.Combine(_root, "brand", "new", "folder");

        Assert.False(Directory.Exists(target));
        Assert.Equal(CustomFolderProblem.Ok, CustomDataFolder.Validate(target, out _));
        Assert.True(Directory.Exists(target));
    }

    /// <summary>And nothing is left behind by the write test itself.</summary>
    [Fact]
    public void The_write_test_leaves_no_files_behind()
    {
        Directory.CreateDirectory(_root);

        Assert.Equal(CustomFolderProblem.Ok, CustomDataFolder.Validate(_root, out _));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_entered_is_reported_as_empty(string? path)
        => Assert.Equal(CustomFolderProblem.Empty, CustomDataFolder.Validate(path, out _));

    /// <summary>
    /// A relative path is refused rather than resolved. It would resolve against the working directory, which
    /// for a process launched from the Startup folder is not the program folder and is not predictable.
    /// </summary>
    [Theory]
    [InlineData("data")]
    [InlineData(@".\data")]
    [InlineData(@"..\somewhere")]
    [InlineData(@"\no-drive")]
    public void A_relative_path_is_refused(string path)
        => Assert.Equal(CustomFolderProblem.NotAFullPath, CustomDataFolder.Validate(path, out _));

    [Fact]
    public void A_path_that_is_a_file_is_refused()
    {
        Directory.CreateDirectory(_root);

        var file = Path.Combine(_root, "not-a-folder.txt");
        File.WriteAllText(file, "x");

        Assert.Equal(CustomFolderProblem.IsAFile, CustomDataFolder.Validate(file, out _));
    }

    [Fact]
    public void An_unusable_path_is_refused_rather_than_thrown()
    {
        // A drive letter nothing is mounted on. Refused as "cannot create" rather than blowing up.
        var problem = CustomDataFolder.Validate(@"Q:\pastejump-should-not-exist", out _);

        Assert.NotEqual(CustomFolderProblem.Ok, problem);
    }

    [Fact]
    public void Every_problem_has_a_sentence_naming_the_path()
    {
        foreach (var problem in Enum.GetValues<CustomFolderProblem>())
        {
            var text = CustomDataFolder.Describe(problem, @"D:\Example");

            if (problem == CustomFolderProblem.Ok)
            {
                Assert.Equal(string.Empty, text);
                continue;
            }

            Assert.NotEqual(string.Empty, text);
        }
    }

    // ------------------------------------------------------------- resolution

    [Fact]
    public void A_custom_location_resolves_to_the_given_folder()
    {
        var expected = Path.GetFullPath(_root);

        Assert.Equal(expected, AppPaths.RootFor(DataLocation.CustomFolder, _root));
    }

    /// <summary>
    /// A custom location with nothing usable falls back to the application folder rather than throwing. This
    /// runs during start-up, before there is a window to report anything in, and running from the default is
    /// recoverable while failing to start is not.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\path")]
    public void A_custom_location_without_a_usable_path_falls_back(string? path)
        => Assert.Equal(
            AppPaths.ApplicationDirectory,
            AppPaths.RootFor(DataLocation.CustomFolder, path));

    [Fact]
    public void The_other_locations_ignore_the_path()
    {
        Assert.Equal(
            AppPaths.ApplicationDirectory,
            AppPaths.RootFor(DataLocation.ApplicationFolder, @"D:\ignored"));

        Assert.Equal(
            AppPaths.UserProfileDirectory,
            AppPaths.RootFor(DataLocation.UserProfile, @"D:\ignored"));
    }

    // ------------------------------------------------------------- the choice

    [Fact]
    public void A_choice_resolves_and_compares_by_root()
    {
        var choice = new DataLocationChoice(DataLocation.CustomFolder, _root);

        Assert.Equal(Path.GetFullPath(_root), choice.Root);
        Assert.True(choice.SameRootAs(Path.GetFullPath(_root)));

        // Case-insensitive, because Windows paths are.
        Assert.True(choice.SameRootAs(Path.GetFullPath(_root).ToUpperInvariant()));
        Assert.False(choice.SameRootAs(@"D:\somewhere-else"));
    }
}
