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

    /// <summary>
    /// Every row's key names a writable property on <see cref="PasteJumpSettings"/>, apart from the two data
    /// locations that live in their own file.
    /// <para>
    /// This is what makes the Advanced page's Reset to Default safe. It resets a setting by looking its property
    /// up by this key and writing the default into it, so a key that did not resolve would be a Reset button
    /// that silently did nothing - and being reflection, nothing would fail to compile.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_row_key_resolves_to_a_writable_property()
    {
        var rows = SettingsInspector.Describe(new PasteJumpSettings());

        foreach (var row in rows)
        {
            if (row.Key is "ClipsLocation" or "SettingsLocation")
            {
                // Not settings: they are in data-location.json, because one of them decides where the settings
                // file itself is. The dialog resets these by moving their combo instead.
                continue;
            }

            var property = typeof(PasteJumpSettings).GetProperty(row.Key);

            Assert.NotNull(property);
            Assert.NotNull(property!.SetMethod);
        }
    }

    /// <summary>
    /// And resetting one property through that key genuinely returns the row to its default, which is the
    /// end-to-end shape of what the Reset button does.
    /// </summary>
    [Fact]
    public void Writing_the_default_through_a_row_key_clears_the_modified_flag()
    {
        var settings = new PasteJumpSettings { OverlayPreviewMaxWidth = 900 };
        var row = SettingsInspector.Describe(settings)
            .Single(r => r.Name == nameof(PasteJumpSettings.OverlayPreviewMaxWidth));

        Assert.True(row.IsModified);

        var property = typeof(PasteJumpSettings).GetProperty(row.Key)!;
        property.SetValue(settings, property.GetValue(new PasteJumpSettings()));

        Assert.False(SettingsInspector.Describe(settings)
            .Single(r => r.Name == nameof(PasteJumpSettings.OverlayPreviewMaxWidth))
            .IsModified);
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
        // More useful to the reader than "Enum": it says what may be written in PasteJump.json.
        var row = SettingsInspector.Describe(new PasteJumpSettings())
            .Single(r => r.Name == nameof(PasteJumpSettings.GridDensity));

        Assert.Contains("Roomy", row.TypeName, StringComparison.Ordinal);
        Assert.Contains("Cozy", row.TypeName, StringComparison.Ordinal);
        Assert.Contains("Compact", row.TypeName, StringComparison.Ordinal);
    }

    /// <summary>
    /// The theme used to be one of those enums and is a name now, so this page can no longer list what is legal -
    /// the set includes whatever theme files exist. Asserted rather than left implicit, because the obvious "fix"
    /// would be to special-case it back into a value list that is complete only until someone writes a theme.
    /// </summary>
    [Fact]
    public void The_theme_is_a_name_so_its_legal_values_are_not_listed()
    {
        var row = SettingsInspector.Describe(new PasteJumpSettings())
            .Single(r => r.Name == nameof(PasteJumpSettings.Theme));

        Assert.Equal("System", row.Value);
        Assert.DoesNotContain("Light", row.TypeName, StringComparison.Ordinal);
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
