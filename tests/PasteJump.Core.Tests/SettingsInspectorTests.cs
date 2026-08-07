using PasteJump.Core.Formatting;
using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The Advanced settings inventory. Reflection-based, so it needs tests: a change to
/// <see cref="PasteJumpSettings"/> cannot break it at compile time, only at runtime.
/// </summary>
public sealed class SettingsInspectorTests
{
    [Fact]
    public void Every_persisted_setting_appears()
    {
        var rows = SettingsInspector.Describe(new PasteJumpSettings());

        Assert.Contains(rows, r => r.Name == nameof(PasteJumpSettings.MaxClips));
        Assert.Contains(rows, r => r.Name == nameof(PasteJumpSettings.Theme));
        Assert.Contains(rows, r => r.Name == nameof(PasteJumpSettings.GridDensity));
        Assert.Contains(rows, r => r.Name == nameof(PasteJumpSettings.IgnoredProcesses));
    }

    [Fact]
    public void Computed_views_over_other_settings_are_excluded()
    {
        // PasteModeOptions is [JsonIgnore] and derived from other properties. Listing it would imply it
        // can be set on its own.
        var rows = SettingsInspector.Describe(new PasteJumpSettings());

        Assert.DoesNotContain(rows, r => r.Name == nameof(PasteJumpSettings.PasteModeOptions));
    }

    [Fact]
    public void A_freshly_constructed_settings_object_has_nothing_modified()
    {
        var rows = SettingsInspector.Describe(new PasteJumpSettings());

        Assert.All(rows, r => Assert.False(r.IsModified, $"{r.Name} reported as modified at defaults"));
    }

    [Fact]
    public void A_changed_value_is_flagged_and_shows_both_values()
    {
        var settings = new PasteJumpSettings { MaxClips = 999 };

        var row = SettingsInspector.Describe(settings).Single(r => r.Name == nameof(PasteJumpSettings.MaxClips));

        Assert.True(row.IsModified);
        Assert.Equal("999", row.Value);
        Assert.Equal("200", row.Default);
    }

    [Fact]
    public void An_enum_lists_its_legal_values_as_its_type()
    {
        // More useful to the reader than "Enum": it says what may be written in settings.json.
        var row = SettingsInspector.Describe(new PasteJumpSettings())
            .Single(r => r.Name == nameof(PasteJumpSettings.Theme));

        Assert.Contains("Light", row.TypeName, StringComparison.Ordinal);
        Assert.Contains("Dark", row.TypeName, StringComparison.Ordinal);
        Assert.Contains("System", row.TypeName, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_values_are_labelled_rather_than_blank()
    {
        // A blank cell reads as a rendering bug; "(empty)" reads as information.
        var rows = SettingsInspector.Describe(new PasteJumpSettings());

        Assert.Equal("(empty)", rows.Single(r => r.Name == nameof(PasteJumpSettings.IgnoredProcesses)).Value);
    }

    [Fact]
    public void The_default_formatter_id_is_stored_explicitly_not_as_null()
    {
        // Regression: null and "original" both resolve to the Original formatter, and the setting
        // defaulted to null while the settings dialog wrote "original". The Advanced page therefore
        // reported the setting as changed from its default on an untouched install.
        var row = SettingsInspector.Describe(new PasteJumpSettings())
            .Single(r => r.Name == nameof(PasteJumpSettings.DefaultFormatterId));

        Assert.Equal(FormatterRegistry.DefaultId, row.Value);
        Assert.Equal(FormatterRegistry.DefaultId, row.Default);
        Assert.False(row.IsModified);
    }

    [Fact]
    public void A_blank_formatter_id_from_a_hand_edited_file_is_normalised()
    {
        var settings = new PasteJumpSettings { DefaultFormatterId = "  " };
        settings.Normalise();

        Assert.Equal(FormatterRegistry.DefaultId, settings.DefaultFormatterId);
    }

    [Fact]
    public void An_unknown_formatter_id_is_preserved_rather_than_reset()
    {
        // It may belong to a formatter registered later, and Resolve already falls back safely - so
        // rewriting it would silently discard a valid preference.
        var settings = new PasteJumpSettings { DefaultFormatterId = "some-future-formatter" };
        settings.Normalise();

        Assert.Equal("some-future-formatter", settings.DefaultFormatterId);
        Assert.Equal("Original", new FormatterRegistry().Resolve(settings.DefaultFormatterId).DisplayName);
    }

    [Fact]
    public void A_list_is_flattened_onto_one_line()
    {
        var settings = new PasteJumpSettings { IgnoredProcesses = ["keepass.exe", "1password.exe"] };

        var row = SettingsInspector.Describe(settings)
            .Single(r => r.Name == nameof(PasteJumpSettings.IgnoredProcesses));

        Assert.Equal("keepass.exe, 1password.exe", row.Value);
        Assert.DoesNotContain('\n', row.Value);
    }

    [Fact]
    public void Rows_are_ordered_by_name()
    {
        var rows = SettingsInspector.Describe(new PasteJumpSettings());
        var names = rows.Select(static r => r.Name).ToList();

        Assert.Equal(names.OrderBy(static n => n, StringComparer.Ordinal).ToList(), names);
    }
}
