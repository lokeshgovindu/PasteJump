using PasteJump.Core.Settings;
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
        => Assert.Equal(AppTheme.System, Fresh.Theme);

    /// <summary>
    /// And note this is NOT the enum's zero value, which is <see cref="AppTheme.Light"/>. A settings file that
    /// spells out <c>"Theme": 0</c> therefore still gets Light - only an absent property picks up the default.
    /// That is the right way round, but it is the sort of thing worth stating so nobody "fixes" it by renumbering
    /// the enum and silently reassigning everyone's stored value.
    /// </summary>
    [Fact]
    public void The_theme_default_is_deliberately_not_the_enums_zero()
    {
        Assert.Equal(AppTheme.Light, default(AppTheme));
        Assert.NotEqual(default, Fresh.Theme);
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

        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(GridDensity.Cozy, settings.GridDensity);
        Assert.Equal(500, settings.CopyNotificationMs);
    }
}
