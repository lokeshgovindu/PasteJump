using PasteJump.Core.Theming;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Reading a user-authored theme. Every rejection here is one a person will read, so the tests assert that the
/// message names what is wrong rather than only that parsing failed.
/// </summary>
public class ThemeDefinitionTests
{
    private static ThemeDefinition Parse(string json)
    {
        Assert.True(ThemeDefinition.TryParse(json, out var theme, out var error), error);

        return theme;
    }

    private static string Refuse(string json)
    {
        Assert.False(ThemeDefinition.TryParse(json, out _, out var error));

        return error;
    }

    [Fact]
    public void A_minimal_theme_needs_only_a_name_and_one_colour()
    {
        var theme = Parse("""
            { "name": "Ink", "colors": { "AccentBrush": "#2563EB" } }
            """);

        Assert.Equal("Ink", theme.Name);
        Assert.Equal(ThemeBase.Light, theme.BasedOn);
        Assert.Equal(new ThemeColor(255, 0x25, 0x63, 0xEB), Assert.Single(theme.Colors).Value.Top);
    }

    /// <summary>
    /// Partial themes are the whole point: a key a theme omits comes from its base, so a file recolouring one thing
    /// is complete. The alternative - demanding all 25 keys - is what a silently-unstyled control would otherwise
    /// force.
    /// </summary>
    [Fact]
    public void A_theme_may_omit_almost_every_key()
    {
        var theme = Parse("""
            { "name": "Ink", "basedOn": "dark", "colors": { "AccentBrush": "#2563EB" } }
            """);

        Assert.Single(theme.Colors);
        Assert.DoesNotContain("SurfaceBrush", theme.Colors.Keys);
    }

    [Theory]
    [InlineData("light", ThemeBase.Light)]
    [InlineData("dark", ThemeBase.Dark)]
    [InlineData("Dark", ThemeBase.Dark)]
    [InlineData("DARK", ThemeBase.Dark)]
    public void The_base_is_read_case_insensitively(string written, ThemeBase expected)
    {
        var theme = Parse($$"""
            { "name": "Ink", "basedOn": "{{written}}", "colors": { "AccentBrush": "#000" } }
            """);

        Assert.Equal(expected, theme.BasedOn);
    }

    [Fact]
    public void An_omitted_base_is_light_like_the_application_default()
    {
        Assert.Equal(ThemeBase.Light, Parse("""
            { "name": "Ink", "colors": { "AccentBrush": "#000" } }
            """).BasedOn);
    }

    [Theory]
    [InlineData("#FFF", 255, 255, 255, 255)]
    [InlineData("#000", 255, 0, 0, 0)]
    [InlineData("#1a2B3c", 255, 0x1A, 0x2B, 0x3C)]
    [InlineData("#802563EB", 0x80, 0x25, 0x63, 0xEB)]
    public void Colours_are_read_in_every_supported_form(string written, int a, int r, int g, int b)
    {
        var theme = Parse($$"""
            { "name": "Ink", "colors": { "AccentBrush": "{{written}}" } }
            """);

        Assert.Equal(new ThemeColor((byte)a, (byte)r, (byte)g, (byte)b), theme.Colors["AccentBrush"].Top);
    }

    /// <summary>
    /// #RGB doubles each digit, as CSS and WPF do - so #F00 is pure red rather than a very dark one.
    /// </summary>
    [Fact]
    public void Three_digit_colours_double_each_digit()
    {
        var theme = Parse("""
            { "name": "Ink", "colors": { "AccentBrush": "#F80" } }
            """);

        Assert.Equal(new ThemeColor(255, 0xFF, 0x88, 0x00), theme.Colors["AccentBrush"].Top);
    }

    [Fact]
    public void The_one_gradient_key_takes_two_colours()
    {
        var theme = Parse("""
            { "name": "Ink", "colors": { "SelectionBrush": ["#DCEBFB", "#BAD8F7"] } }
            """);

        var value = theme.Colors["SelectionBrush"];

        Assert.Equal(new ThemeColor(255, 0xDC, 0xEB, 0xFB), value.Top);
        Assert.Equal(new ThemeColor(255, 0xBA, 0xD8, 0xF7), value.Bottom);
    }

    [Fact]
    public void The_gradient_key_also_takes_a_single_flat_colour()
    {
        var theme = Parse("""
            { "name": "Ink", "colors": { "SelectionBrush": "#DCEBFB" } }
            """);

        Assert.Null(theme.Colors["SelectionBrush"].Bottom);
    }

    [Fact]
    public void A_gradient_written_for_a_solid_key_is_refused_rather_than_half_used()
    {
        var error = Refuse("""
            { "name": "Ink", "colors": { "AccentBrush": ["#000", "#FFF"] } }
            """);

        Assert.Contains("AccentBrush", error);
        Assert.Contains("not a gradient", error);
    }

    [Fact]
    public void A_gradient_needs_exactly_two_stops()
    {
        Assert.Contains("two colours", Refuse("""
            { "name": "Ink", "colors": { "SelectionBrush": ["#000", "#111", "#222"] } }
            """));
    }

    /// <summary>
    /// The rejection that matters most. A mistyped key would otherwise load cleanly and do nothing, and nothing
    /// would tell the author the difference between that and a colour that merely looks wrong.
    /// </summary>
    [Fact]
    public void An_unknown_key_is_refused_and_named()
    {
        var error = Refuse("""
            { "name": "Ink", "colors": { "SurfceBrush": "#FFF" } }
            """);

        Assert.Contains("SurfceBrush", error);
        Assert.Contains("not a palette key", error);
    }

    /// <summary>
    /// Case-sensitive, because WPF's own resource lookup is: accepting "surfacebrush" would produce a theme that
    /// validated and then had no effect.
    /// </summary>
    [Fact]
    public void Keys_are_case_sensitive()
    {
        Assert.Contains("surfacebrush", Refuse("""
            { "name": "Ink", "colors": { "surfacebrush": "#FFF" } }
            """));
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("System")]
    [InlineData("dark")]
    public void A_built_in_name_is_refused(string name)
    {
        var error = Refuse($$"""
            { "name": "{{name}}", "colors": { "AccentBrush": "#000" } }
            """);

        Assert.Contains("built-in theme name", error);
    }

    [Fact]
    public void A_theme_with_no_name_is_refused()
    {
        Assert.Contains("name", Refuse("""
            { "colors": { "AccentBrush": "#000" } }
            """));
    }

    [Fact]
    public void A_theme_with_no_colours_object_is_refused()
    {
        Assert.Contains("colors", Refuse("""
            { "name": "Ink" }
            """));
    }

    [Theory]
    [InlineData("2563EB", "must start with #")]
    [InlineData("#25", "not #RGB")]
    [InlineData("#2563E", "not #RGB")]
    [InlineData("#GGGGGG", "not a hex digit")]
    [InlineData("rebeccapurple", "must start with #")]
    public void A_colour_that_will_not_parse_is_refused_with_a_reason(string written, string expected)
    {
        var error = Refuse($$"""
            { "name": "Ink", "colors": { "AccentBrush": "{{written}}" } }
            """);

        Assert.Contains(expected, error);
    }

    [Fact]
    public void A_number_where_a_colour_belongs_is_refused()
    {
        Assert.Contains("AccentBrush", Refuse("""
            { "name": "Ink", "colors": { "AccentBrush": 16711680 } }
            """));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("42")]
    public void Anything_that_is_not_a_theme_object_is_refused(string json)
    {
        Assert.NotNull(Refuse(json));
    }

    /// <summary>
    /// Comments and trailing commas are allowed, because a theme file is written by hand and the sample carries
    /// explanatory comments. JSON forbids both by default, so this has to be asked for.
    /// </summary>
    [Fact]
    public void Comments_and_trailing_commas_are_allowed_in_a_hand_written_file()
    {
        var theme = Parse("""
            {
                // The accent is the only thing this theme changes.
                "name": "Ink",
                "colors": {
                    "AccentBrush": "#2563EB",
                },
            }
            """);

        Assert.Equal("Ink", theme.Name);
    }

    [Fact]
    public void Every_palette_key_can_actually_be_set()
    {
        foreach (var key in PaletteKeys.All)
        {
            var theme = Parse($$"""
                { "name": "Ink", "colors": { "{{key.Name}}": "#123456" } }
                """);

            Assert.True(theme.Colors.ContainsKey(key.Name), key.Name);
        }
    }

    /// <summary>
    /// The shipped themes are written in the same format as anyone's own, so they are parsed rather than compiled -
    /// which means a typo in one is a run-time absence, not a build error. This is what catches that, and it is the
    /// only thing that would: <c>BuiltInThemes.All</c> deliberately drops what it cannot parse rather than throwing
    /// during start-up.
    /// </summary>
    [Fact]
    public void Every_built_in_theme_parses()
    {
        Assert.NotEmpty(BuiltInThemes.Sources);

        foreach (var source in BuiltInThemes.Sources)
        {
            Assert.True(ThemeDefinition.TryParse(source, out _, out var error), error);
        }

        Assert.Equal(BuiltInThemes.Sources.Count, BuiltInThemes.All.Count);
    }

    [Fact]
    public void Built_in_theme_names_are_distinct_and_not_reserved()
    {
        var names = BuiltInThemes.All.Select(static t => t.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, static name => Assert.False(ThemeDefinition.IsReservedName(name)));
    }

    /// <summary>
    /// A shipped theme is also a worked example, so it must not be one that only works by accident: each has to
    /// name a base explicitly and change enough to be visibly different from it.
    /// </summary>
    [Fact]
    public void Every_built_in_theme_recolours_at_least_the_window_and_the_text()
    {
        foreach (var theme in BuiltInThemes.All)
        {
            Assert.Contains("SurfaceBrush", theme.Colors.Keys);
            Assert.Contains("TextBrush", theme.Colors.Keys);
            Assert.Contains("AccentBrush", theme.Colors.Keys);
        }
    }

    /// <summary>
    /// The contract is a list a human maintains, so this guards the things that would silently break it: a
    /// duplicate name would make one entry unreachable, and an empty description would leave a blank row in the
    /// settings dialog.
    /// </summary>
    [Fact]
    public void The_palette_contract_is_well_formed()
    {
        Assert.Equal(PaletteKeys.All.Count, PaletteKeys.All.Select(static k => k.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(PaletteKeys.All, static key => Assert.False(string.IsNullOrWhiteSpace(key.Description)));
        Assert.All(PaletteKeys.All, static key => Assert.False(string.IsNullOrWhiteSpace(key.Name)));

        // Exactly one gradient and one bare Color today. Asserted so that adding another forces a look at
        // everything that special-cases them - the theme parser, the dictionary builder and the sample file.
        Assert.Single(PaletteKeys.All.Where(static k => k.Kind == PaletteEntryKind.Gradient));
        Assert.Single(PaletteKeys.All.Where(static k => k.Kind == PaletteEntryKind.Color));
    }
}
