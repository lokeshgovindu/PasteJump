using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The configurable paste-mode trigger and the history hotkey - both Clipjump settings PasteJump lacked
/// (<c>paste_k</c> and <c>history_k</c>).
/// <para>
/// The trigger rules are worth pinning down because getting them wrong silently removes a feature: the
/// trigger key doubles as "step to an older clip", so allowing a letter already bound to another action
/// would make that action unreachable with no error anywhere.
/// </para>
/// </summary>
public sealed class TriggerKeyAndHotkeyTests
{
    // ------------------------------------------------------------------ trigger key

    [Fact]
    public void The_default_trigger_is_the_originals()
        => Assert.Equal('V', TriggerKey.Default);

    [Theory]
    [InlineData('C')] // step to a newer clip
    [InlineData('X')] // cycle what release does
    [InlineData('A')] // jump to the newest
    [InlineData('F')] // search
    [InlineData('Z')] // cycle format
    [InlineData('H')] // edit
    [InlineData('E')] // export
    [InlineData('Q')]
    [InlineData('S')]
    [InlineData('T')]
    public void A_letter_already_bound_to_an_action_is_refused(char key)
    {
        Assert.False(TriggerKey.IsAvailable(key));

        // And it says what the letter is for, so the dialog can explain rather than just decline.
        Assert.NotNull(TriggerKey.ReservedFor(key));
    }

    [Theory]
    [InlineData('V')]
    [InlineData('B')]
    [InlineData('G')]
    [InlineData('P')]
    public void An_unbound_letter_is_accepted(char key)
    {
        Assert.True(TriggerKey.IsAvailable(key));
        Assert.Null(TriggerKey.ReservedFor(key));
    }

    [Fact]
    public void The_offered_list_excludes_every_bound_letter_and_keeps_the_default()
    {
        Assert.Contains('V', TriggerKey.Available);
        Assert.DoesNotContain('C', TriggerKey.Available);

        // Both letters that open the clip in an editor. O is the one the help names and H is the original
        // binding kept working beside it, so an alias has to be reserved as firmly as the primary - a trigger
        // on either would steal the action from whichever letter the user still presses.
        Assert.DoesNotContain('O', TriggerKey.Available);
        Assert.DoesNotContain('H', TriggerKey.Available);

        // 26 letters minus the 11 bound to other actions - 10 actions, one of which answers to two letters.
        Assert.Equal(15, TriggerKey.Available.Count);
    }

    [Theory]
    [InlineData("B", 'B')]
    [InlineData("b", 'B')]
    [InlineData("  b  ", 'B')]
    [InlineData("", 'V')]
    [InlineData(null, 'V')]
    [InlineData("BB", 'V')]
    [InlineData("1", 'V')]
    [InlineData("C", 'V')] // reserved, so coerced rather than accepted
    [InlineData("!", 'V')]
    public void A_hand_edited_trigger_is_coerced_to_something_usable(string? stored, char expected)
        => Assert.Equal(expected, TriggerKey.Normalise(stored));

    [Fact]
    public void The_virtual_key_for_a_letter_is_its_uppercase_code()
    {
        // A-Z virtual keys are the ASCII codes of the uppercase letters, which is why no table is needed.
        Assert.Equal(0x56, TriggerKey.ToVirtualKey('V'));
        Assert.Equal(0x42, TriggerKey.ToVirtualKey('b'));
    }

    [Fact]
    public void The_chord_is_described_the_way_a_user_would_write_it()
        => Assert.Equal("Ctrl+B", TriggerKey.Describe('b'));

    [Fact]
    public void The_setting_round_trips_through_normalise()
    {
        var settings = new PasteJumpSettings { PasteModeTriggerKey = "b" };

        settings.Normalise();

        Assert.Equal("B", settings.PasteModeTriggerKey);
    }

    // ------------------------------------------------------------------ hotkey parsing

    [Theory]
    [InlineData("Ctrl+Shift+H")]
    [InlineData("ctrl+shift+h")]
    [InlineData("CONTROL+SHIFT+H")]
    [InlineData("  Ctrl + Shift + H  ")]
    [InlineData("Shift+Ctrl+H")]
    public void Spellings_and_orderings_all_parse_to_the_same_chord(string text)
    {
        Assert.True(HotkeySpec.TryParse(text, out var spec));

        Assert.True(spec.Control);
        Assert.True(spec.Shift);
        Assert.False(spec.Alt);
        Assert.False(spec.Windows);
        Assert.Equal('H', spec.VirtualKey);

        // Canonical rendering, so a round-trip is stable and the Advanced page does not report a
        // difference between two spellings of the same thing.
        Assert.Equal("Ctrl+Shift+H", spec.ToString());
    }

    [Theory]
    [InlineData("Win+V")]
    [InlineData("Alt+F4")]
    [InlineData("Ctrl+Alt+Shift+Win+F12")]
    [InlineData("Ctrl+Insert")]
    [InlineData("Alt+PageUp")]
    public void Modifiers_and_non_letter_keys_are_supported(string text)
        => Assert.True(HotkeySpec.TryParse(text, out _));

    [Theory]
    [InlineData("H")]
    [InlineData("F1")]
    public void A_chord_with_no_modifier_is_refused(string text)
    {
        // Registering a bare key makes it untypeable in every application on the desktop, and the way back
        // is editing a settings file with a keyboard that can no longer type the letter.
        Assert.False(HotkeySpec.TryParse(text, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Nonsense")]
    [InlineData("Ctrl+H+J")]
    [InlineData("Ctrl+Escape")]
    [InlineData("Ctrl+Tab")]
    public void Unusable_text_is_refused(string? text)
        => Assert.False(HotkeySpec.TryParse(text, out _));

    [Fact]
    public void An_unset_spec_renders_as_empty_and_is_not_valid()
    {
        var none = default(HotkeySpec);

        Assert.False(none.IsSet);
        Assert.False(none.IsValid);
        Assert.Equal(string.Empty, none.ToString());
    }

    [Fact]
    public void ParseOrNone_swallows_bad_input()
    {
        Assert.Equal("Ctrl+Shift+H", HotkeySpec.ParseOrNone("ctrl+shift+h").ToString());
        Assert.Equal(string.Empty, HotkeySpec.ParseOrNone("rubbish").ToString());
    }

    [Fact]
    public void The_history_hotkey_setting_is_empty_by_default_and_canonicalised_on_normalise()
    {
        // Empty by default because a global hotkey takes that chord from every other application, which is
        // not something to do to someone unasked.
        Assert.Equal(string.Empty, new PasteJumpSettings().HistoryHotkey);

        var settings = new PasteJumpSettings { HistoryHotkey = "control + shift + h" };
        settings.Normalise();
        Assert.Equal("Ctrl+Shift+H", settings.HistoryHotkey);

        var bad = new PasteJumpSettings { HistoryHotkey = "just wrong" };
        bad.Normalise();
        Assert.Equal(string.Empty, bad.HistoryHotkey);
    }

    // ------------------------------------------------------------------ other new settings

    [Fact]
    public void The_beep_matches_the_originals_defaults()
    {
        var settings = new PasteJumpSettings();

        Assert.False(settings.BeepOnCopy);
        Assert.Equal(1500, settings.BeepFrequencyHz);
    }

    [Theory]
    [InlineData(0, 37)]
    [InlineData(-5, 37)]
    [InlineData(99_999, 32_767)]
    [InlineData(440, 440)]
    public void The_beep_pitch_is_clamped_to_what_the_api_accepts(int stored, int expected)
    {
        // Outside this range the Win32 Beep call simply fails, which would turn a mistyped setting into a
        // silently dead feature rather than an audible one.
        var settings = new PasteJumpSettings { BeepFrequencyHz = stored };

        settings.Normalise();

        Assert.Equal(expected, settings.BeepFrequencyHz);
    }

    [Fact]
    public void The_image_editor_has_a_working_default_and_is_never_left_blank()
    {
        Assert.Equal("mspaint.exe", new PasteJumpSettings().ImageEditor);

        var settings = new PasteJumpSettings { ImageEditor = "   " };
        settings.Normalise();
        Assert.Equal("mspaint.exe", settings.ImageEditor);
    }

    // ------------------------------------------------------------------ advanced inventory

    [Fact]
    public void The_advanced_inventory_includes_the_data_locations()
    {
        // They live in data-location.json rather than settings.json, so reflection alone misses them - and a
        // settings inventory that is silently incomplete invites the reader to conclude a setting does not
        // exist.
        var rows = SettingsInspector.Describe(
            new PasteJumpSettings(),
            DataLocation.UserProfile,
            DataLocation.ApplicationFolder);

        var clips = Assert.Single(rows, r => r.Name.StartsWith("ClipsLocation", StringComparison.Ordinal));
        var settings = Assert.Single(rows, r => r.Name.StartsWith("SettingsLocation", StringComparison.Ordinal));

        Assert.Equal("UserProfile", clips.Value);
        Assert.True(clips.IsModified);

        Assert.Equal("ApplicationFolder", settings.Value);
        Assert.False(settings.IsModified);

        // Names carry their file, since someone looking for them in settings.json would not find them.
        Assert.Contains(DataLocationPointer.FileName, clips.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_new_setting_shows_up_in_the_advanced_inventory()
    {
        // The user's rule: everything the application uses must be listed. Reflection gives this for free
        // for anything on PasteJumpSettings, and this test is the guard that they were put there rather than
        // held in a field somewhere.
        var names = SettingsInspector.Describe(new PasteJumpSettings()).Select(static r => r.Name).ToList();

        Assert.Contains("PasteModeTriggerKey", names);
        Assert.Contains("HistoryHotkey", names);
        Assert.Contains("BeepOnCopy", names);
        Assert.Contains("BeepFrequencyHz", names);
        Assert.Contains("ImageEditor", names);
        Assert.Contains("PasteKeystroke", names);
        Assert.Contains("WarnAboutClipboardManagerConflict", names);
    }
}
