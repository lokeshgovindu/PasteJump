using System.Globalization;
using PasteJump.Core;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Timestamps follow the machine's own date and time format.
/// <para>
/// Asserted against the culture's patterns rather than against literal strings: a test that hard-codes
/// "21-08-2026 3:12 pm" passes on one machine and fails on the build server, and would have to be rewritten
/// the first time anybody changed their regional settings - which is the very thing this is meant to respect.
/// </para>
/// </summary>
public sealed class LocalTimestampTests
{
    [Theory]
    [InlineData("en-IN")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void The_format_is_the_cultures_short_date_and_short_time(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        var instant = new DateTimeOffset(2026, 8, 21, 9, 42, 0, TimeSpan.Zero);

        var expected = instant.ToLocalTime().ToString("g", culture);

        Assert.Equal(expected, LocalTimestamp.Format(instant, culture));

        // And "g" really is the pair of patterns Windows exposes, rather than something of ours that happens
        // to look similar - so a user's custom override reaches it.
        var byPattern = instant.ToLocalTime().ToString(
            culture.DateTimeFormat.ShortDatePattern + " " + culture.DateTimeFormat.ShortTimePattern,
            culture);

        Assert.Equal(byPattern, LocalTimestamp.Format(instant, culture));
    }

    /// <summary>
    /// Local, not UTC. A clip copied twenty minutes ago must not read as one from the small hours.
    /// </summary>
    [Fact]
    public void The_time_is_local()
    {
        var instant = new DateTimeOffset(2026, 8, 21, 0, 30, 0, TimeSpan.Zero);
        var culture = CultureInfo.InvariantCulture;

        Assert.Equal(instant.ToLocalTime().ToString("g", culture), LocalTimestamp.Format(instant, culture));
    }

    /// <summary>
    /// The measuring sample has to be at least as wide as any real value, or a column sized from it truncates
    /// later. Checked against a year of month ends and both halves of the clock rather than by inspection.
    /// </summary>
    [Theory]
    [InlineData("en-IN")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void The_widest_sample_is_no_narrower_than_a_real_value(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        var widest = LocalTimestamp.WidestSample(culture).Length;

        for (var month = 1; month <= 12; month++)
        {
            foreach (var hour in new[] { 0, 1, 11, 12, 13, 23 })
            {
                var value = new DateTimeOffset(new DateTime(2026, month, 28, hour, 59, 0, DateTimeKind.Local));

                Assert.True(
                    LocalTimestamp.Format(value, culture).Length <= widest,
                    $"{cultureName}: \"{LocalTimestamp.Format(value, culture)}\" is wider than the sample "
                        + $"\"{LocalTimestamp.WidestSample(culture)}\"");
            }
        }
    }

    /// <summary>The sample is a real formatted value, not a placeholder that only looks like one.</summary>
    [Fact]
    public void The_widest_sample_is_a_formatted_timestamp()
    {
        var sample = LocalTimestamp.WidestSample(new CultureInfo("en-GB"));

        Assert.Contains("2026", sample);
        Assert.Contains(":", sample);
    }
}
