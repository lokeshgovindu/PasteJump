using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Normalising the excluded-application list.
/// <para>
/// Worth testing because the failure is silent and the setting is a privacy one. Capture compares the
/// foreground window's process against these entries by file name; an entry stored in any other shape simply
/// never matches, so a password manager the user believed was excluded is quietly recorded anyway. There is no
/// error to notice - only clipboard history that should not exist.
/// </para>
/// </summary>
public sealed class ExcludedAppsTests
{
    [Theory]
    [InlineData("keepass.exe", "keepass.exe")]
    [InlineData("  keepass.exe  ", "keepass.exe")]
    [InlineData("KeePass.EXE", "KeePass.EXE")]
    public void An_already_valid_name_is_kept(string entry, string expected)
        => Assert.Equal(expected, ExcludedApps.Normalise(entry));

    [Theory]
    [InlineData("keepass")]
    [InlineData(" keepass ")]
    public void A_missing_extension_is_filled_in(string entry)
    {
        // Someone typing "keepass" means the program. Rejecting it for the want of four characters would be
        // pedantry, and silently storing it unmatched would be worse.
        Assert.Equal("keepass.exe", ExcludedApps.Normalise(entry));
    }

    [Theory]
    [InlineData(@"C:\Program Files\KeePass\KeePass.exe", "KeePass.exe")]
    [InlineData(@"""C:\Program Files\KeePass\KeePass.exe""", "KeePass.exe")]
    [InlineData(@"D:\portable\1Password\1Password.exe", "1Password.exe")]
    public void A_full_path_is_reduced_to_its_file_name(string entry, string expected)
    {
        // What the Browse button hands over. Capture resolves the foreground window to a process and gets a
        // file name, never a path, so storing the path would produce an entry that can never match.
        Assert.Equal(expected, ExcludedApps.Normalise(entry));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"\"")]
    public void Nothing_usable_yields_null(string? entry)
        => Assert.Null(ExcludedApps.Normalise(entry));

    [Fact]
    public void A_list_keeps_its_order_and_drops_duplicates()
    {
        var result = ExcludedApps.NormaliseAll(
            ["keepass.exe", "  ", "1password", "KEEPASS.EXE", null, @"C:\x\bitwarden.exe"]);

        // Order preserved rather than sorted: the list is the user's own record of decisions, and re-sorting it
        // under them on every save makes it hard to see what was just added.
        Assert.Equal(["keepass.exe", "1password.exe", "bitwarden.exe"], result);
    }

    [Fact]
    public void Duplicates_are_compared_the_way_windows_compares_file_names()
    {
        // Windows file names are case-insensitive, so these are one program. Showing both would invite the
        // belief that they differ.
        var result = ExcludedApps.NormaliseAll(["KeePass.exe", "keepass.exe", "KEEPASS.EXE"]);

        Assert.Single(result);
    }

    [Fact]
    public void A_null_list_is_handled()
        => Assert.Empty(ExcludedApps.NormaliseAll(null));

    [Theory]
    [InlineData("keepass.exe", true)]
    [InlineData("KEEPASS.EXE", true)]
    [InlineData("keepass", true)]
    [InlineData(@"C:\somewhere\keepass.exe", true)]
    [InlineData("notepad.exe", false)]
    [InlineData(null, false)]
    public void Contains_matches_regardless_of_case_extension_or_path(string? candidate, bool expected)
    {
        string[] existing = ["KeePass.exe", "1password.exe"];

        Assert.Equal(expected, ExcludedApps.Contains(existing, candidate));
    }

    [Fact]
    public void Contains_normalises_the_existing_entries_too()
    {
        // A hand-edited PasteJump.json can hold a path or a bare name. Contains has to see through both, or the
        // dialog offers to add a duplicate of something already there.
        string[] existing = [@"C:\Program Files\KeePass\KeePass.exe", "1password"];

        Assert.True(ExcludedApps.Contains(existing, "keepass.exe"));
        Assert.True(ExcludedApps.Contains(existing, "1Password.exe"));
    }

    [Fact]
    public void Normalising_is_idempotent()
    {
        // Applied on the way into the list and again on the way out, so a second pass must not change anything.
        var once = ExcludedApps.NormaliseAll([@"C:\x\KeePass.exe", "1password"]);
        var twice = ExcludedApps.NormaliseAll(once);

        Assert.Equal(once, twice);
    }
}
