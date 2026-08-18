using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The numeric settings that shape what the user sees, and the bounds <see cref="PasteJumpSettings.Normalise"/>
/// puts on them.
/// <para>
/// Worth testing rather than trusting: the settings dialog rejects out-of-range input, so these clamps only ever
/// run against a hand-edited JSON file - which means a wrong bound would never show up in normal use and would
/// then hand a nonsensical value to a decoder or a window size.
/// </para>
/// </summary>
public sealed class SettingsClampTests
{
    [Fact]
    public void Overlay_preview_size_is_clamped_both_ways()
    {
        var low = new PasteJumpSettings { OverlayPreviewMaxWidth = 1, OverlayPreviewMaxHeight = 1 };
        var high = new PasteJumpSettings { OverlayPreviewMaxWidth = 99_999, OverlayPreviewMaxHeight = 99_999 };

        low.Normalise();
        high.Normalise();

        Assert.Equal(120, low.OverlayPreviewMaxWidth);
        Assert.Equal(80, low.OverlayPreviewMaxHeight);
        Assert.Equal(1400, high.OverlayPreviewMaxWidth);
        Assert.Equal(900, high.OverlayPreviewMaxHeight);
    }

    [Fact]
    public void Overlay_preview_characters_are_clamped()
    {
        var low = new PasteJumpSettings { OverlayPreviewChars = 0 };
        var high = new PasteJumpSettings { OverlayPreviewChars = 1_000_000 };

        low.Normalise();
        high.Normalise();

        Assert.Equal(40, low.OverlayPreviewChars);
        Assert.Equal(4_000, high.OverlayPreviewChars);
    }

    /// <summary>
    /// The stored-preview cap has a floor well above zero because <c>history_fts</c> indexes that column: a cap
    /// of zero would leave search nothing to match on, which looks like search being broken rather than like a
    /// setting being set to zero.
    /// </summary>
    [Fact]
    public void Preview_characters_kept_are_clamped()
    {
        var low = new PasteJumpSettings { PreviewMaxChars = 0 };
        var high = new PasteJumpSettings { PreviewMaxChars = 10_000_000 };

        low.Normalise();
        high.Normalise();

        Assert.Equal(256, low.PreviewMaxChars);
        Assert.Equal(65_536, high.PreviewMaxChars);
    }

    [Fact]
    public void History_limits_are_clamped()
    {
        var low = new PasteJumpSettings { HistoryLoadLimit = 0, HistoryPreviewMaxWidth = 0 };
        var high = new PasteJumpSettings { HistoryLoadLimit = 99_000_000, HistoryPreviewMaxWidth = 99_999 };

        low.Normalise();
        high.Normalise();

        Assert.Equal(100, low.HistoryLoadLimit);
        Assert.Equal(120, low.HistoryPreviewMaxWidth);
        Assert.Equal(1_000_000, high.HistoryLoadLimit);
        Assert.Equal(4_096, high.HistoryPreviewMaxWidth);
    }

    /// <summary>
    /// Zero is a legal argument to the Win32 Beep API and simply makes no sound, which is indistinguishable from
    /// the feature being broken - hence a floor rather than a pass-through.
    /// </summary>
    [Fact]
    public void Beep_duration_is_clamped()
    {
        var low = new PasteJumpSettings { BeepDurationMs = 0 };
        var high = new PasteJumpSettings { BeepDurationMs = 60_000 };

        low.Normalise();
        high.Normalise();

        Assert.Equal(20, low.BeepDurationMs);
        Assert.Equal(2_000, high.BeepDurationMs);
    }

    /// <summary>
    /// A fixed overlay position needs both halves. One alone would pin the overlay in one axis and let it follow
    /// the caret in the other, which reads as a bug rather than as a setting.
    /// </summary>
    [Fact]
    public void Half_a_fixed_overlay_position_is_discarded()
    {
        var onlyX = new PasteJumpSettings { OverlayX = 400 };
        var onlyY = new PasteJumpSettings { OverlayY = 300 };

        onlyX.Normalise();
        onlyY.Normalise();

        Assert.Null(onlyX.OverlayX);
        Assert.Null(onlyX.OverlayY);
        Assert.Null(onlyY.OverlayX);
        Assert.Null(onlyY.OverlayY);
    }

    [Fact]
    public void A_complete_fixed_overlay_position_survives_including_the_origin()
    {
        // Zero is a real coordinate - the top-left of the primary monitor - so it must not be treated as "unset".
        var settings = new PasteJumpSettings { OverlayX = 0, OverlayY = 0 };

        settings.Normalise();

        Assert.Equal(0, settings.OverlayX);
        Assert.Equal(0, settings.OverlayY);
    }

    /// <summary>
    /// The overlay text cap reaches paste mode through <see cref="PasteJumpSettings.PasteModeOptions"/>. Left out
    /// of that projection the setting would be editable, persisted, listed on the Advanced page and inert.
    /// </summary>
    [Fact]
    public void Paste_mode_options_carry_the_overlay_text_cap()
    {
        var settings = new PasteJumpSettings { OverlayPreviewChars = 777 };

        Assert.Equal(777, settings.PasteModeOptions.OverlayPreviewChars);
    }

    [Fact]
    public void Paste_mode_options_default_to_the_controllers_own_default()
        => Assert.Equal(
            PasteModeController.DefaultOverlayPreviewChars,
            new PasteModeOptions().OverlayPreviewChars);

    /// <summary>
    /// And the controller honours it: the overlay frame is elided at the configured length rather than at the
    /// constant it used to be fixed to.
    /// </summary>
    [Fact]
    public void The_overlay_frame_is_elided_at_the_configured_length()
    {
        var catalog = new Fakes.FakeClipCatalog();
        catalog.Add(new string('x', 500));

        var host = new Fakes.RecordingPasteModeHost();

        var controller = new PasteModeController(
            catalog,
            host,
            new Formatting.FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false, OverlayPreviewChars = 50 });

        controller.Begin();

        // 50 characters plus the ellipsis that says there is more.
        Assert.Equal(new string('x', 50) + "…", host.LastFrame!.PreviewText);
    }

    [Fact]
    public void A_clip_shorter_than_the_configured_length_is_not_elided()
    {
        var catalog = new Fakes.FakeClipCatalog();
        catalog.Add("short");

        var host = new Fakes.RecordingPasteModeHost();

        var controller = new PasteModeController(
            catalog,
            host,
            new Formatting.FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false, OverlayPreviewChars = 50 });

        controller.Begin();

        Assert.Equal("short", host.LastFrame!.PreviewText);
    }
    /// <summary>
    /// The overlay's font, asked for so the gesture can be read at a size and in a face of the user's choosing -
    /// its colours already come from the theme, which is why only the font became a setting.
    /// </summary>
    [Fact]
    public void Overlay_font_size_is_clamped_both_ways()
    {
        var low = new PasteJumpSettings { OverlayFontSize = 1 };
        var high = new PasteJumpSettings { OverlayFontSize = 400 };

        low.Normalise();
        high.Normalise();

        Assert.Equal(9, low.OverlayFontSize);
        Assert.Equal(24, high.OverlayFontSize);
    }

    [Fact]
    public void The_default_overlay_font_is_the_built_in_look()
    {
        var settings = new PasteJumpSettings();

        settings.Normalise();

        // Empty, not "Segoe UI": the built-in look is two fonts, the UI face for labels and Consolas for a clip's
        // own text, and naming one of them here would silently make the preview proportional.
        Assert.Equal(string.Empty, settings.OverlayFontFamily);
        Assert.Equal(12, settings.OverlayFontSize);
    }

    [Fact]
    public void An_overlay_font_name_is_trimmed_but_never_dropped()
    {
        // Not validated against installed families on purpose: a settings file travels between machines, and a
        // font missing here may well be present there. Dropping the name would lose it on the next save.
        var settings = new PasteJumpSettings { OverlayFontFamily = "  Cascadia Mono  " };

        settings.Normalise();

        Assert.Equal("Cascadia Mono", settings.OverlayFontFamily);
    }

    [Fact]
    public void The_history_window_size_is_clamped_to_something_usable()
    {
        var tiny = new PasteJumpSettings { HistoryWindowWidth = 10, HistoryWindowHeight = 10 };
        var huge = new PasteJumpSettings { HistoryWindowWidth = 999_999, HistoryWindowHeight = 999_999 };

        tiny.Normalise();
        huge.Normalise();

        // The floors are the window's own MinWidth and MinHeight; a stored value below them would be ignored by
        // WPF anyway, so clamping keeps the file honest about what is actually in force.
        Assert.Equal(680, tiny.HistoryWindowWidth);
        Assert.Equal(400, tiny.HistoryWindowHeight);
        Assert.Equal(20_000, huge.HistoryWindowWidth);
        Assert.Equal(20_000, huge.HistoryWindowHeight);
    }

    [Fact]
    public void The_history_window_opens_at_the_size_that_was_asked_for()
    {
        var settings = new PasteJumpSettings();

        settings.Normalise();

        // 1260x770 is the size the user resized it to and asked to have as the default.
        Assert.Equal(1260, settings.HistoryWindowWidth);
        Assert.Equal(770, settings.HistoryWindowHeight);
        Assert.False(settings.HistoryWindowMaximised);
    }

}
