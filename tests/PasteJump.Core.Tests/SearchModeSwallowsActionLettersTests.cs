using PasteJump.Core.Formatting;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Guards the one rule search mode has: while a query is being typed, <b>no letter and no digit is an action</b>.
/// </summary>
/// <remarks>
/// <para>
/// Reported by typing <c>output</c> into the search box: the <c>o</c> opened the clip in an editor. The cause was
/// a guard that could never be false - the search branch fell through to the action dispatch "except when Ctrl is
/// held", and holding Ctrl is what keeps the gesture open, so it is held for every keystroke of every query. Four
/// of the six letters in that one word are bound: <c>o</c> editor, <c>t</c> tags, <c>p</c> pin, <c>u</c> nothing.
/// </para>
/// <para>
/// <b>1002 tests passed while this was broken, and the reason is worth more than the fix.</b> Every existing
/// search test typed its query by calling <see cref="PasteGestureRecognizer.HandleCharacter"/> directly - which
/// sits <em>downstream</em> of the decision that was wrong. They proved the buffer accumulates characters, which
/// was never in doubt. Nothing drove a real bound letter through <c>Handle</c> while searching, which is what a
/// keyboard does. So these tests go through the key path, letter by letter, exactly as the hook would.
/// </para>
/// </remarks>
public class SearchModeSwallowsActionLettersTests
{
    private static (PasteGestureRecognizer Recognizer, PasteModeController Controller, RecordingPasteModeHost Host)
        Searching()
    {
        var catalog = new FakeClipCatalog();
        catalog.Add("output of the build");
        catalog.Add("something else");

        var host = new RecordingPasteModeHost();
        var controller = new PasteModeController(
            catalog,
            host,
            new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });

        var recognizer = new PasteGestureRecognizer(controller) { CtrlHeld = true };

        recognizer.Handle(GestureKey.Control, isDown: true);
        recognizer.Handle(GestureKey.Paste, isDown: true);
        recognizer.Handle(GestureKey.ToggleSearch, isDown: true);

        Assert.Equal(PasteSessionState.Searching, controller.State);

        return (recognizer, controller, host);
    }

    /// <summary>
    /// The report, in the shape it was reported: press the search key, then type <c>output</c>.
    /// </summary>
    [Fact]
    public void Typing_output_into_the_search_box_opens_nothing()
    {
        var (recognizer, controller, host) = Searching();

        // Each letter is offered to the recognizer as a key first, exactly as the hook does it, and only becomes
        // search text because nothing claimed it. Typing it straight into the buffer would prove nothing.
        foreach (var (key, character) in new (GestureKey, char)[]
        {
            (GestureKey.EditClip, 'o'),        // O - was opening the clip editor
            (GestureKey.None, 'u'),
            (GestureKey.None, 't'),            // T is EditTags only if bound; None here keeps the test honest
            (GestureKey.PushToClipboard, 'p'), // P - pin
            (GestureKey.None, 'u'),
            (GestureKey.EditTags, 't'),
        })
        {
            if (key != GestureKey.None)
            {
                Assert.False(
                    recognizer.Handle(key, isDown: true),
                    $"'{character}' was claimed as an action while searching");
            }

            Assert.True(recognizer.HandleCharacter(character));
        }

        Assert.Equal("output", controller.SearchQuery);
        Assert.Null(host.ClipEditorRequestedFor);
        Assert.Null(host.TagEditorRequestedFor);
        Assert.Equal(0, host.HistoryCount);
        Assert.Equal(0, host.HelpCount);
        Assert.Empty(host.PushedClips);
    }

    /// <summary>
    /// Swept rather than asserted per letter, because the defect was not about any particular binding: it was
    /// that the search branch reached the dispatch at all. A letter bound tomorrow inherits this.
    /// </summary>
    [Theory]
    [InlineData(GestureKey.EditClip)]
    [InlineData(GestureKey.EditTags)]
    [InlineData(GestureKey.ShowHistory)]
    [InlineData(GestureKey.ExportClip)]
    [InlineData(GestureKey.TogglePin)]
    [InlineData(GestureKey.PromoteToFront)]
    [InlineData(GestureKey.PushToClipboard)]
    [InlineData(GestureKey.CycleFormatter)]
    [InlineData(GestureKey.CycleKindFilter)]
    [InlineData(GestureKey.ToggleJoinMark)]
    [InlineData(GestureKey.JumpToNewest)]
    [InlineData(GestureKey.CycleCommitMode)]
    [InlineData(GestureKey.DeleteCurrent)]
    [InlineData(GestureKey.Help)]
    public void No_lettered_action_fires_while_searching(GestureKey key)
    {
        var (recognizer, _, host) = Searching();

        Assert.False(recognizer.Handle(key, isDown: true));

        Assert.DoesNotContain(
            host.Calls,
            c => c is "RequestClipEditor" or "RequestTagEditor" or "RequestHistoryWindow" or "RequestExport"
                or "ShowShortcutHelp" or "PushToClipboard");
    }

    /// <summary>
    /// Digits are text too. Searching for <c>output2</c> has to be possible, and the digit jump would otherwise
    /// move the cursor to the second clip mid-word.
    /// </summary>
    [Fact]
    public void Digits_are_search_text_rather_than_a_jump()
    {
        var (recognizer, controller, _) = Searching();

        Assert.False(recognizer.Handle(GestureKey.Digit2, isDown: true));
        Assert.True(recognizer.HandleCharacter('2'));

        Assert.Equal("2", controller.SearchQuery);
    }

    /// <summary>
    /// The arrows survive, and they are the only way to step while searching - the same pair Clipjump bound for
    /// this (<c>spm_nextres</c> / <c>spm_prevres</c>, <c>searchPasteMode.ahk:19</c>). Being physical keys they
    /// can never be part of a query, which is exactly why they are the ones that keep working.
    /// </summary>
    [Fact]
    public void The_arrows_still_step_through_the_matches()
    {
        var (recognizer, controller, _) = Searching();

        var before = controller.Window.Count;

        Assert.True(recognizer.Handle(GestureKey.StepOlder, isDown: true));
        Assert.True(recognizer.Handle(GestureKey.Back, isDown: true));

        // The point is that the keys were claimed rather than typed into the query, which is what would happen
        // to a letter.
        Assert.Equal(string.Empty, controller.SearchQuery);
        Assert.Equal(before, controller.Window.Count);
    }

    /// <summary>
    /// Escape, Backspace, Enter and Ctrl+F keep working, or search would be a state with no way out and no way
    /// to correct a typo. These are the four the original kept too.
    /// </summary>
    [Fact]
    public void The_way_out_of_search_still_works()
    {
        var (recognizer, controller, _) = Searching();

        recognizer.HandleCharacter('o');
        Assert.True(recognizer.Handle(GestureKey.Backspace, isDown: true));
        Assert.Equal(string.Empty, controller.SearchQuery);

        Assert.True(recognizer.Handle(GestureKey.ToggleSearch, isDown: true));
        Assert.NotEqual(PasteSessionState.Searching, controller.State);
        Assert.True(controller.IsActive);
    }

    [Fact]
    public void Escape_still_cancels_from_inside_search()
    {
        var (recognizer, controller, host) = Searching();

        recognizer.HandleCharacter('o');

        Assert.True(recognizer.Handle(GestureKey.Escape, isDown: true));
        Assert.False(controller.IsActive);
        Assert.Empty(host.PastedClips);
    }

    /// <summary>
    /// A lettered action is reachable again the moment search is closed. This is what the fix costs, stated as a
    /// test so nobody has to wonder whether the letters were disabled permanently.
    /// </summary>
    [Fact]
    public void Closing_search_makes_the_letters_act_again()
    {
        var (recognizer, controller, host) = Searching();

        recognizer.Handle(GestureKey.ToggleSearch, isDown: true);
        Assert.NotEqual(PasteSessionState.Searching, controller.State);

        Assert.True(recognizer.Handle(GestureKey.EditClip, isDown: true));
        Assert.NotNull(host.ClipEditorRequestedFor);
    }
}
