using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// What a left click on the tray icon does. Configurable because there is no single convention - plenty of tray
/// applications open their menu on a left click and plenty open a window - and PasteJump's choice was a guess
/// about habit rather than a fact.
/// </summary>
public class TrayClickActionTests
{
    /// <summary>
    /// The default must not change. Someone upgrading has years of muscle memory for a left click opening the
    /// history, and a new setting whose default silently moved their icon would be a regression dressed as a
    /// feature.
    /// </summary>
    [Fact]
    public void The_default_is_the_history_which_is_what_it_always_did()
        => Assert.Equal(TrayClickAction.History, new PasteJumpSettings().TrayLeftClick);

    /// <summary>
    /// History is zero on purpose. A settings file written before this existed has no such property, so the
    /// deserialised value is the enum's default - and that has to land on the old behaviour rather than on
    /// whichever member happened to be declared first.
    /// </summary>
    [Fact]
    public void History_is_the_zero_value_so_an_older_settings_file_keeps_its_behaviour()
        => Assert.Equal(TrayClickAction.History, default(TrayClickAction));

    [Fact]
    public void Normalise_leaves_a_valid_choice_alone()
    {
        var settings = new PasteJumpSettings { TrayLeftClick = TrayClickAction.Menu };

        settings.Normalise();

        Assert.Equal(TrayClickAction.Menu, settings.TrayLeftClick);
    }

    /// <summary>
    /// Every member is offered by the dialog, which is the list this enum exists to drive. Asserted by count so
    /// adding a member without adding it to the dialog fails here - the same bargain the Advanced tab strikes.
    /// </summary>
    [Fact]
    public void There_are_exactly_four_choices()
        => Assert.Equal(4, Enum.GetValues<TrayClickAction>().Length);

    /// <summary>
    /// Right click is deliberately not configurable, so there is no setting for it. If one is ever added, the
    /// menu must remain reachable by some button - a machine where neither opened it could not be put back.
    /// </summary>
    [Fact]
    public void There_is_no_setting_for_the_right_button()
    {
        var properties = typeof(PasteJumpSettings).GetProperties().Select(static p => p.Name).ToList();

        Assert.Contains("TrayLeftClick", properties);
        Assert.DoesNotContain("TrayRightClick", properties);
    }
}
