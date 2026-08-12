using PasteJump.Core.Settings;
using PasteJump.Core.Theming;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The defaults a fresh install gets, pinned.
/// <para>
/// Worth their own tests because a default is the value almost every user actually runs: it is chosen once,
/// changed by hardly anyone, and a silent drift in one is a silent change in the product for everybody. They are
/// also the values the Advanced tab compares against to decide whether a row is modified, so a default that moved
/// without the help moving with it makes that page quietly wrong.
/// </para>
/// </summary>
public class SettingsDefaultsTests
{
    private static readonly PasteJumpSettings Fresh = new();

    /// <summary>
    /// Following Windows, not Light. A utility that lives in the notification area has no branding to assert and
    /// no reason to be the one light window on a dark desktop.
    /// </summary>
    [Fact]
    public void The_theme_follows_Windows()
        => Assert.Equal(ThemeNames.System, Fresh.Theme);

    /// <summary>
    /// The theme is a NAME now rather than the <c>AppTheme</c> enum it used to be, because the set of themes is no
    /// longer fixed. The change is invisible on disk - the enum was written through
    /// <c>JsonStringEnumConverter</c>, so an existing file already says <c>"Theme": "Dark"</c> - and this asserts
    /// the stored spelling so that stays true.
    /// </summary>
    [Fact]
    public void The_theme_is_stored_as_a_name()
    {
        Assert.Equal("System", Fresh.Theme);
        Assert.True(ThemeNames.IsBuiltIn(Fresh.Theme));
    }

    /// <summary>
    /// An unrecognised name is deliberately <em>not</em> corrected: it may be a theme file that is missing for the
    /// moment - an unplugged drive, a file mid-edit - and rewriting the setting would throw the user's choice away
    /// the first time it was unavailable. Only an empty value is repaired.
    /// </summary>
    [Fact]
    public void Normalise_keeps_an_unknown_theme_name_but_repairs_an_empty_one()
    {
        var missing = new PasteJumpSettings { Theme = "Solarized" };
        missing.Normalise();
        Assert.Equal("Solarized", missing.Theme);

        var blank = new PasteJumpSettings { Theme = "   " };
        blank.Normalise();
        Assert.Equal(ThemeNames.System, blank.Theme);
    }

    [Fact]
    public void The_history_list_is_cozy()
        => Assert.Equal(GridDensity.Cozy, Fresh.GridDensity);

    /// <summary>
    /// The copy notification fires on every copy, so it is the most frequently seen thing in the product. It was
    /// 1,200 ms, which outlasted the doubt it answers.
    /// </summary>
    [Fact]
    public void The_copy_notification_is_brief()
        => Assert.Equal(500, Fresh.CopyNotificationMs);

    /// <summary>
    /// The floor is 1 ms, not 250. The old floor silently overrode anyone asking for something shorter; 0 is
    /// excluded because that reads as "off", and off is what <see cref="PasteJumpSettings.ShowCopyNotification"/>
    /// is for - two ways to say the same thing is how they end up contradicting each other.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-40, 1)]
    [InlineData(50, 50)]
    [InlineData(99_999, 10_000)]
    public void The_notification_duration_is_clamped_to_one_millisecond_at_the_bottom(int stored, int expected)
    {
        var settings = new PasteJumpSettings { CopyNotificationMs = stored };

        settings.Normalise();

        Assert.Equal(expected, settings.CopyNotificationMs);
    }

    /// <summary>Normalising a fresh object must not change it, or the default is not actually reachable.</summary>
    [Fact]
    public void Normalise_leaves_the_defaults_alone()
    {
        var settings = new PasteJumpSettings();

        settings.Normalise();

        Assert.Equal(ThemeNames.System, settings.Theme);
        Assert.Equal(GridDensity.Cozy, settings.GridDensity);
        Assert.Equal(500, settings.CopyNotificationMs);
    }
}
