using PasteJump.Core.Paste;
using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Per-application paste delays.
/// <para>
/// The delay is a property of the application being pasted into, not of PasteJump - Office, Electron shells and
/// remote-desktop clients cache the clipboard and serve a too-early keystroke from that cache. Until this existed
/// the only fix was raising the delay globally, so curing Word slowed every paste everywhere for ever.
/// </para>
/// </summary>
public class PerAppSettleDelayTests
{
    [Fact]
    public void With_no_overrides_every_application_takes_the_global_delay()
    {
        var delays = PerAppSettleDelays.Empty;

        Assert.Equal(25, delays.For("WINWORD.EXE", 25));
        Assert.Equal(25, delays.For("notepad.exe", 25));
    }

    [Fact]
    public void A_listed_application_takes_its_own_delay_and_others_do_not()
    {
        var delays = PerAppSettleDelays.Parse("winword.exe=80;ms-teams.exe=100");

        Assert.Equal(80, delays.For("WINWORD.EXE", 25));
        Assert.Equal(100, delays.For("ms-teams.exe", 25));
        Assert.Equal(25, delays.For("notepad.exe", 25));
    }

    /// <summary>
    /// Matched on the executable name, case-insensitively - the same key the ignore list uses, so a name typed into
    /// one is recognised by the other.
    /// </summary>
    [Theory]
    [InlineData("WINWORD.EXE")]
    [InlineData("winword.exe")]
    [InlineData("WinWord.Exe")]
    [InlineData(@"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE")]
    public void The_match_is_on_the_file_name_and_ignores_case(string process)
        => Assert.Equal(80, PerAppSettleDelays.Parse("winword.exe=80").For(process, 25));

    /// <summary>
    /// A null process name happens for real - the foreground window cannot be identified on a secure desktop - and
    /// must take the fallback rather than matching something arbitrary.
    /// </summary>
    [Fact]
    public void An_unidentifiable_foreground_window_takes_the_global_delay()
        => Assert.Equal(25, PerAppSettleDelays.Parse("winword.exe=80").For(null, 25));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("rubbish")]
    [InlineData("winword.exe")]
    [InlineData("=80")]
    [InlineData("winword.exe=notanumber")]
    public void Rubbish_yields_no_overrides_rather_than_refusing(string? stored)
        => Assert.Equal(25, PerAppSettleDelays.Parse(stored).For("winword.exe", 25));

    /// <summary>
    /// Clamped rather than dropped: a hand-edited 5000 plainly means "as long as possible", and honouring the
    /// ceiling is closer to that intent than ignoring the line.
    /// </summary>
    [Fact]
    public void An_out_of_range_delay_is_clamped()
    {
        Assert.Equal(SettingsBounds.PasteSettleDelayMs.Max, PerAppSettleDelays.Parse("winword.exe=5000").For("winword.exe", 25));
        Assert.Equal(SettingsBounds.PasteSettleDelayMs.Min, PerAppSettleDelays.Parse("winword.exe=-5").For("winword.exe", 25));
    }

    [Fact]
    public void A_settings_string_round_trips_and_sorts()
    {
        var delays = PerAppSettleDelays.Parse("ms-teams.exe=100;winword.exe=80");

        Assert.Equal("ms-teams.exe=100;winword.exe=80", delays.ToSettingsString());
        Assert.Equal(delays.ToSettingsString(), PerAppSettleDelays.Parse(delays.ToSettingsString()).ToSettingsString());
    }

    /// <summary>A later entry wins, so a hand-edited file with a repeat cannot hold two answers.</summary>
    [Fact]
    public void A_repeated_application_keeps_the_last_entry()
        => Assert.Equal(90, PerAppSettleDelays.Parse("winword.exe=80;WINWORD.EXE=90").For("winword.exe", 25));

    // ---- validation, which is what the dialog uses

    [Fact]
    public void A_valid_set_is_accepted()
        => Assert.Null(PerAppSettleDelays.Validate([("winword.exe", 80), ("ms-teams.exe", 100)]));

    [Fact]
    public void Two_rows_for_one_program_are_refused_by_name()
    {
        var error = PerAppSettleDelays.Validate([("winword.exe", 80), ("WINWORD.EXE", 100)]);

        Assert.NotNull(error);
        Assert.Contains("winword.exe", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_blank_program_is_refused()
        => Assert.NotNull(PerAppSettleDelays.Validate([("   ", 80)]));

    /// <summary>The refusal quotes the shared bound, so it cannot disagree with what Normalise would clamp to.</summary>
    [Fact]
    public void An_out_of_range_delay_is_refused_using_the_shared_bound()
    {
        var error = PerAppSettleDelays.Validate([("winword.exe", 9_000)]);

        Assert.NotNull(error);
        Assert.Contains(SettingsBounds.PasteSettleDelayMs.Max.ToString(), error, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalise_canonicalises_the_stored_value()
    {
        var settings = new PasteJumpSettings { PasteSettleDelayPerApp = "  WINWORD.EXE = 80 ; rubbish ; x=1 " };

        settings.Normalise();

        Assert.Equal(80, PerAppSettleDelays.Parse(settings.PasteSettleDelayPerApp).For("winword.exe", 25));
        Assert.Equal(
            settings.PasteSettleDelayPerApp,
            PerAppSettleDelays.Parse(settings.PasteSettleDelayPerApp).ToSettingsString());
    }

    [Fact]
    public void There_are_no_overrides_by_default()
        => Assert.Equal(string.Empty, new PasteJumpSettings().PasteSettleDelayPerApp);
}
