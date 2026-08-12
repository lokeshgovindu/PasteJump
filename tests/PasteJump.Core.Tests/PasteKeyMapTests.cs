using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The configurable letter bindings. These were a hard-coded switch in Interop until the user asked to be able
/// to move them, and the rules about what a legal set looks like are the whole reason the table moved to Core.
/// </summary>
public class PasteKeyMapTests
{
    [Fact]
    public void The_defaults_are_the_letters_the_help_documents()
    {
        var map = PasteKeyMap.Default;

        Assert.Equal(GestureKey.Back, map.ForLetter('C'));
        Assert.Equal(GestureKey.JumpToNewest, map.ForLetter('A'));
        Assert.Equal(GestureKey.ToggleSearch, map.ForLetter('F'));
        Assert.Equal(GestureKey.TogglePin, map.ForLetter('P'));
        Assert.Equal(GestureKey.PromoteToFront, map.ForLetter('M'));
        Assert.Equal(GestureKey.CycleFormatter, map.ForLetter('Z'));
        Assert.Equal(GestureKey.EditTags, map.ForLetter('T'));
        Assert.Equal(GestureKey.PushToClipboard, map.ForLetter('S'));
        Assert.Equal(GestureKey.EditClip, map.ForLetter('O'));
        Assert.Equal(GestureKey.ShowHistory, map.ForLetter('H'));
        Assert.Equal(GestureKey.ExportClip, map.ForLetter('E'));
        Assert.Equal(GestureKey.CycleCommitMode, map.ForLetter('X'));
    }

    /// <summary>Clipjump's Q still moves a clip to the front, whatever the configurable letter says.</summary>
    [Fact]
    public void A_fixed_alias_fires_regardless_of_the_letter()
    {
        var map = PasteKeyMap.Parse("front=J");

        Assert.Equal(GestureKey.PromoteToFront, map.ForLetter('J'));
        Assert.Equal(GestureKey.PromoteToFront, map.ForLetter('Q'));
        Assert.Equal(GestureKey.None, map.ForLetter('M'));
    }

    /// <summary>
    /// Switching an action off leaves its letter free to be typed into the search box, exactly as any unbound
    /// letter always could - and the fixed alias survives, which is what makes "off" safe rather than lossy.
    /// </summary>
    [Fact]
    public void An_action_switched_off_frees_its_letter_but_keeps_its_alias()
    {
        var map = PasteKeyMap.Parse("pin=;front=");

        Assert.Equal(GestureKey.None, map.ForLetter('P'));
        Assert.Equal(GestureKey.None, map.ForLetter('M'));
        Assert.False(map.IsEnabled("pin"));

        // Space and Q are not letters this map owns, so they are unaffected - they are in the fixed half of the
        // table in VirtualKeyTranslator, or claimed as an alias here.
        Assert.Equal(GestureKey.PromoteToFront, map.ForLetter('Q'));
    }

    [Fact]
    public void Unbound_letters_map_to_nothing_so_they_can_be_typed_into_search()
    {
        var map = PasteKeyMap.Default;

        // J is absent: it is bound to "mark to join". Kept as a literal list rather than derived from the map, so
        // binding a new action forces a decision here about a letter someone may be typing into search.
        foreach (var letter in "BDGILNRUWY")
        {
            Assert.Equal(GestureKey.None, map.ForLetter(letter));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    [InlineData("=;=;=")]
    [InlineData("unknown=Q")]
    [InlineData("pin=notaletter")]
    [InlineData("pin=7")]
    public void Rubbish_falls_back_to_the_defaults_rather_than_unbinding_anything(string? stored)
    {
        var map = PasteKeyMap.Parse(stored);

        // Silence rather than a complaint is deliberate: this runs during start-up before there is a window to
        // report in, and refusing to start over one bad letter in a hand-edited file would be far worse.
        Assert.Equal(GestureKey.TogglePin, map.ForLetter('P'));
        Assert.Equal(GestureKey.Back, map.ForLetter('C'));
    }

    /// <summary>
    /// A letter the user bound wins over an action that merely defaults to it, and the defaulted action is left
    /// unbound rather than stealing it.
    /// <para>
    /// This is what stops an ADDED action breaking an existing configuration. "Mark to join" arrived with a default
    /// of J, which was free - but free is not unused, and anyone who had moved pin to J would otherwise have lost
    /// it silently, since the later entry wins when the table is rebuilt.
    /// </para>
    /// </summary>
    [Fact]
    public void A_stored_binding_beats_another_action_defaulting_to_the_same_letter()
    {
        var map = PasteKeyMap.Parse("pin=J");

        Assert.Equal(GestureKey.TogglePin, map.ForLetter('J'));
        Assert.Null(map.LetterFor("join"));
    }

    [Fact]
    public void Defaults_are_left_alone_when_nothing_was_stored()
    {
        var map = PasteKeyMap.Default;

        Assert.Equal(GestureKey.ToggleJoinMark, map.ForLetter('J'));
        Assert.Equal(GestureKey.TogglePin, map.ForLetter('P'));
    }

    [Fact]
    public void A_settings_string_round_trips()
    {
        var original = PasteKeyMap.Parse("pin=J;format=;tags=T");
        var again = PasteKeyMap.Parse(original.ToSettingsString());

        Assert.Equal(GestureKey.TogglePin, again.ForLetter('J'));
        Assert.Equal(GestureKey.None, again.ForLetter('Z'));
        Assert.Equal(GestureKey.EditTags, again.ForLetter('T'));
        Assert.Equal(original.ToSettingsString(), again.ToSettingsString());
    }

    // ---- validation

    [Fact]
    public void The_defaults_validate()
        => Assert.Null(PasteKeyMap.Validate(Choices(), 'V'));

    [Fact]
    public void Two_actions_on_one_letter_is_refused_and_both_are_named()
    {
        var choices = Choices();
        choices["tags"] = 'F';

        var error = PasteKeyMap.Validate(choices, 'V');

        Assert.NotNull(error);
        Assert.Contains("Open search", error, StringComparison.Ordinal);
        Assert.Contains("Edit tags", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_letter_that_opens_paste_mode_cannot_also_be_an_action()
    {
        var choices = Choices();
        choices["tags"] = 'V';

        var error = PasteKeyMap.Validate(choices, 'V');

        Assert.NotNull(error);
        Assert.Contains("opens paste mode", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fixed alias is claimed as firmly as a configurable letter. Binding tags to Q would give Q two jobs,
    /// with whichever the lookup wrote last winning - silently.
    /// </summary>
    [Fact]
    public void A_letter_taken_by_a_fixed_alias_is_refused()
    {
        var choices = Choices();
        choices["tags"] = 'Q';

        Assert.NotNull(PasteKeyMap.Validate(choices, 'V'));
    }

    /// <summary>Switching an action off is always legal, and frees its letter for another action.</summary>
    [Fact]
    public void Off_is_legal_and_releases_the_letter()
    {
        var choices = Choices();
        choices["search"] = null;
        choices["tags"] = 'F';

        Assert.Null(PasteKeyMap.Validate(choices, 'V'));
    }

    /// <summary>
    /// The trigger list follows the bindings rather than a frozen table, which is what retires the
    /// keep-these-two-in-step-by-hand rule the old TriggerKey.Reserved needed.
    /// </summary>
    [Fact]
    public void Freeing_a_letter_offers_it_to_the_trigger()
    {
        Assert.DoesNotContain('T', TriggerKey.AvailableFor(PasteKeyMap.Default));
        Assert.Contains('T', TriggerKey.AvailableFor(PasteKeyMap.Parse("tags=")));
    }

    /// <summary>
    /// Settings normalisation runs the value through the parser, so a hand-edited file cannot leave a binding
    /// that says one thing and behaves as another.
    /// </summary>
    [Fact]
    public void Normalise_canonicalises_the_stored_value()
    {
        var settings = new PasteJumpSettings { PasteModeKeys = "pin=j;rubbish;unknown=Z" };

        settings.Normalise();

        Assert.Equal(PasteKeyMap.Parse(settings.PasteModeKeys).ToSettingsString(), settings.PasteModeKeys);
        Assert.Equal(GestureKey.TogglePin, PasteKeyMap.Parse(settings.PasteModeKeys).ForLetter('J'));
    }

    private static Dictionary<string, char?> Choices()
    {
        var choices = new Dictionary<string, char?>();

        foreach (var entry in PasteKeyMap.Entries)
        {
            choices[entry.Name] = entry.DefaultLetter;
        }

        return choices;
    }
}
