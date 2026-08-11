using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The numeric ranges, and the property that matters: <see cref="PasteJumpSettings.Normalise"/> and the settings
/// dialog now read the same definition.
/// <para>
/// They did not. Each bound was written twice - once as a <c>Math.Clamp</c> and once as a hand-typed comparison
/// and message in the dialog - and lowering the notification floor from 250 to 1 changed only the clamp. The
/// dialog went on refusing anything under 250 with a message quoting the old number, so the setting looked
/// deliberately restricted rather than out of step, and nothing warned. These tests pin the shape that prevents it.
/// </para>
/// </summary>
public class SettingsBoundsTests
{
    /// <summary>
    /// Normalise must clamp to exactly the bound, because the dialog refuses exactly the same range. A value the
    /// dialog accepts and Normalise then changes is the same class of disagreement in the other direction.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(10_000)]
    [InlineData(500)]
    public void A_value_the_dialog_would_accept_survives_normalise(int value)
    {
        var settings = new PasteJumpSettings { CopyNotificationMs = value };

        settings.Normalise();

        Assert.Equal(value, settings.CopyNotificationMs);
        Assert.True(SettingsBounds.CopyNotificationMs.Admits(value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void A_value_the_dialog_would_refuse_is_clamped_by_normalise(int value)
    {
        var settings = new PasteJumpSettings { CopyNotificationMs = value };

        settings.Normalise();

        Assert.False(SettingsBounds.CopyNotificationMs.Admits(value));
        Assert.NotEqual(value, settings.CopyNotificationMs);
    }

    /// <summary>The floor the user asked for, stated once so a future change has to be deliberate.</summary>
    [Fact]
    public void The_notification_floor_is_one_millisecond()
    {
        Assert.Equal(1, SettingsBounds.CopyNotificationMs.Min);
        Assert.Equal(10_000, SettingsBounds.CopyNotificationMs.Max);
    }

    /// <summary>
    /// Every bound must be a range rather than a point or an inversion. Cheap, and it catches a typo that would
    /// otherwise make a setting impossible to save at all.
    /// </summary>
    [Fact]
    public void Every_bound_admits_something()
    {
        foreach (var property in typeof(SettingsBounds).GetProperties())
        {
            var bound = (SettingBound)property.GetValue(null)!;

            Assert.True(bound.Min < bound.Max, $"{property.Name} has Min {bound.Min} and Max {bound.Max}.");
            Assert.True(bound.Admits(bound.Min), $"{property.Name} refuses its own minimum.");
            Assert.True(bound.Admits(bound.Max), $"{property.Name} refuses its own maximum.");
            Assert.False(bound.Admits(bound.Min - 1), $"{property.Name} admits below its minimum.");
            Assert.False(bound.Admits(bound.Max + 1), $"{property.Name} admits above its maximum.");
        }
    }

    /// <summary>
    /// The refusal quotes the bound rather than a number someone typed, which is the whole point - the message and
    /// the check can no longer disagree.
    /// </summary>
    [Fact]
    public void The_refusal_is_generated_from_the_bound()
    {
        Assert.Equal(
            "Notification duration must be between 1 and 10000 milliseconds.",
            SettingsBounds.CopyNotificationMs.Refuse("Notification duration", "milliseconds"));

        // And without a unit, for the counts that are not measured in anything.
        Assert.Equal(
            "Rows the history window loads must be between 100 and 1000000.",
            SettingsBounds.HistoryLoadLimit.Refuse("Rows the history window loads"));
    }

    /// <summary>
    /// Each default has to sit inside its own bound. A default outside its range would be clamped on the first
    /// Normalise, so the value a fresh install runs would differ from the one the Advanced tab calls the default -
    /// which is exactly how that page starts reporting rows as modified when nothing was touched.
    /// </summary>
    [Fact]
    public void Every_default_sits_inside_its_bound()
    {
        var fresh = new PasteJumpSettings();

        Assert.True(SettingsBounds.MaxClips.Admits(fresh.MaxClips));
        Assert.True(SettingsBounds.CopyNotificationMs.Admits(fresh.CopyNotificationMs));
        Assert.True(SettingsBounds.PasteSettleDelayMs.Admits(fresh.PasteSettleDelayMs));
        Assert.True(SettingsBounds.BeepFrequencyHz.Admits(fresh.BeepFrequencyHz));
        Assert.True(SettingsBounds.BeepDurationMs.Admits(fresh.BeepDurationMs));
        Assert.True(SettingsBounds.PreviewMaxChars.Admits(fresh.PreviewMaxChars));
        Assert.True(SettingsBounds.HistoryLoadLimit.Admits(fresh.HistoryLoadLimit));
        Assert.True(SettingsBounds.HistoryPreviewMaxWidth.Admits(fresh.HistoryPreviewMaxWidth));
        Assert.True(SettingsBounds.OverlayPreviewChars.Admits(fresh.OverlayPreviewChars));
        Assert.True(SettingsBounds.OverlayPreviewMaxWidth.Admits(fresh.OverlayPreviewMaxWidth));
        Assert.True(SettingsBounds.OverlayPreviewMaxHeight.Admits(fresh.OverlayPreviewMaxHeight));
    }
}
