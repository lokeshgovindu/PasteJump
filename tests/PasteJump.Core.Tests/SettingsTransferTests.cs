using PasteJump.Core.Settings;
using PasteJump.Core.Theming;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Exporting and importing a settings file. The interesting half is what an import refuses: a file that parses but
/// is not about PasteJump would otherwise deserialise to an object full of defaults and read as a successful
/// import that quietly reset everything.
/// </summary>
public class SettingsTransferTests
{
    [Fact]
    public void A_round_trip_preserves_the_settings()
    {
        var original = new PasteJumpSettings
        {
            MaxClips = 1_234,
            Theme = ThemeNames.Dark,
            CopyNotificationMs = 42,
            TrayLeftClick = TrayClickAction.Menu,
            PasteModeKeys = "tags=J;format=",
            IgnoredProcesses = ["keepass.exe", "1password.exe"],
        };

        var imported = SettingsTransfer.TryImport(SettingsTransfer.Export(original), new PasteJumpSettings(), out var error);

        Assert.Equal(string.Empty, error);
        Assert.NotNull(imported);
        Assert.Equal(1_234, imported.MaxClips);
        Assert.Equal(ThemeNames.Dark, imported.Theme);
        Assert.Equal(42, imported.CopyNotificationMs);
        Assert.Equal(TrayClickAction.Menu, imported.TrayLeftClick);
        Assert.Equal(["keepass.exe", "1password.exe"], imported.IgnoredProcesses);
    }

    /// <summary>
    /// The exported file is the same shape as the live <c>PasteJump.json</c>, so one can be dropped in place of the
    /// other. Camel case is the observable part of that.
    /// </summary>
    [Fact]
    public void The_exported_shape_matches_the_live_settings_file()
    {
        var json = SettingsTransfer.Export(new PasteJumpSettings());

        Assert.Contains("\"maxClips\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"MaxClips\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value outside its bound is clamped on the way in, so a hand-edited or older file cannot introduce
    /// something the dialog would refuse.
    /// </summary>
    [Fact]
    public void An_out_of_range_value_is_normalised_on_import()
    {
        var imported = SettingsTransfer.TryImport(
            """{ "copyNotificationMs": 99999 }""",
            new PasteJumpSettings(),
            out _);

        Assert.NotNull(imported);
        Assert.Equal(SettingsBounds.CopyNotificationMs.Max, imported.CopyNotificationMs);
    }

    /// <summary>
    /// Absent properties keep their defaults, so a file holding only the three settings someone cared about is a
    /// perfectly good import. This is why there is no schema or version check.
    /// </summary>
    [Fact]
    public void A_partial_file_leaves_everything_else_at_its_default()
    {
        var imported = SettingsTransfer.TryImport("""{ "maxClips": 7 }""", new PasteJumpSettings(), out _);

        Assert.NotNull(imported);
        Assert.Equal(7, imported.MaxClips);
        Assert.Equal(new PasteJumpSettings().CopyNotificationMs, imported.CopyNotificationMs);
    }

    /// <summary>
    /// The one-time legacy-import flag comes from the machine, not the file. Importing someone else's "already
    /// done" would silently suppress the Clipjump import prompt on a machine that has never run it.
    /// </summary>
    [Fact]
    public void The_legacy_import_flag_is_not_taken_from_the_file()
    {
        var local = new PasteJumpSettings { LegacyImportCompleted = false };

        var imported = SettingsTransfer.TryImport(
            """{ "maxClips": 10, "legacyImportCompleted": true }""",
            local,
            out _);

        Assert.NotNull(imported);
        Assert.False(imported.LegacyImportCompleted);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("{ not json", "valid JSON")]
    [InlineData("[]", "does not look like")]
    [InlineData("42", "does not look like")]
    [InlineData("\"a string\"", "does not look like")]
    [InlineData("""{ "somethingElse": 1 }""", "does not look like")]
    public void Anything_that_is_not_exported_settings_is_refused_with_a_reason(string json, string expected)
    {
        var imported = SettingsTransfer.TryImport(json, new PasteJumpSettings(), out var error);

        Assert.Null(imported);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Refused rather than partially applied, unlike SettingsStore.Load which degrades to defaults on a broken
    /// file. There is a person watching who chose this file, so naming the problem beats silently loading defaults
    /// over the settings they already had.
    /// </summary>
    [Fact]
    public void An_unusable_file_reports_rather_than_returning_defaults()
    {
        Assert.Null(SettingsTransfer.TryImport("{}", new PasteJumpSettings(), out var error));
        Assert.NotEmpty(error);
    }

    /// <summary>Comments and trailing commas are tolerated, since these files get hand-edited.</summary>
    [Fact]
    public void A_hand_edited_file_with_comments_still_imports()
    {
        var imported = SettingsTransfer.TryImport(
            """
            {
                // how many clips the gesture can reach
                "maxClips": 25,
            }
            """,
            new PasteJumpSettings(),
            out var error);

        Assert.Equal(string.Empty, error);
        Assert.NotNull(imported);
        Assert.Equal(25, imported.MaxClips);
    }

    [Fact]
    public void The_suggested_name_carries_the_date_so_two_exports_do_not_collide()
        => Assert.Equal(
            "PasteJump-settings-2026-08-11.json",
            SettingsTransfer.SuggestFileName(new DateTimeOffset(2026, 8, 11, 18, 30, 0, TimeSpan.Zero)));
}
