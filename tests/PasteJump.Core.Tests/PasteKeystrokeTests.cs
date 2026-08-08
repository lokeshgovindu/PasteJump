using PasteJump.Core.Capture;
using PasteJump.Core.Model;
using PasteJump.Core.Paste;
using PasteJump.Core.Settings;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The configurable paste chord and the rival-manager detection behind it.
/// <para>
/// This exists because of a failure that no unit test could previously have caught: with Clipjump
/// running, PasteJump wrote the clip successfully, reported success, and pasted nothing. Clipjump
/// registers <c>$^V</c> (<c>Clipjump.ahk:227</c>, with <c>paste_k=V</c> in its settings.ini), the
/// <c>$</c> forcing its own <c>WH_KEYBOARD_LL</c> hook and the absence of <c>~</c> suppressing the key -
/// so it consumed the Ctrl+V we injected before the focused application saw it. Copying was unaffected
/// throughout, because capture runs off <c>WM_CLIPBOARDUPDATE</c> and no keyboard hook can suppress that.
/// </para>
/// </summary>
public sealed class PasteKeystrokeTests
{
    private static readonly ClipPayload TextPayload = new(13, null, [1, 2, 3]);

    private static (ClipboardPaster Paster, FakePasteSender Sender) Build()
    {
        var sender = new FakePasteSender();

        var paster = new ClipboardPaster(
            new FakeClipboardAccess(),
            sender,
            new SelfWriteGuard(),

            // Immediate, so the settle delay does not have to elapse in a test.
            schedule: static (_, action) => action());

        return (paster, sender);
    }

    // ------------------------------------------------------------------ the chord that is sent

    [Fact]
    public void Ctrl_V_is_the_default()
    {
        var (paster, sender) = Build();

        paster.Write([TextPayload], thenPaste: true);

        Assert.Equal(PasteKeystroke.CtrlV, paster.Keystroke);
        Assert.Equal([PasteKeystroke.CtrlV], sender.Sent);
    }

    [Fact]
    public void The_configured_chord_is_the_one_actually_sent()
    {
        var (paster, sender) = Build();

        paster.SetPasteKeystroke(PasteKeystroke.ShiftInsert);
        paster.Write([TextPayload], thenPaste: true);

        Assert.Equal([PasteKeystroke.ShiftInsert], sender.Sent);
    }

    [Fact]
    public void The_pass_through_path_uses_the_configured_chord_too()
    {
        // The empty-store path sends a bare paste without touching the clipboard. It has to honour the
        // setting as well, or an empty store would break pasting for exactly the users who changed it.
        var (paster, sender) = Build();

        paster.SetPasteKeystroke(PasteKeystroke.ShiftInsert);

        Assert.True(paster.SendPasteOnly());
        Assert.Equal([PasteKeystroke.ShiftInsert], sender.Sent);
    }

    [Fact]
    public void A_failed_write_still_sends_no_keystroke_whichever_chord_is_set()
    {
        // The ordering rule is independent of the chord: a keystroke after a failed write pastes whatever
        // was on the clipboard before, which looks exactly like choosing the wrong clip.
        var clipboard = new FakeClipboardAccess { WriteSucceeds = false };
        var sender = new FakePasteSender();

        var paster = new ClipboardPaster(
            clipboard, sender, new SelfWriteGuard(), static (_, action) => action());

        paster.SetPasteKeystroke(PasteKeystroke.ShiftInsert);
        paster.Write([TextPayload], thenPaste: true);

        Assert.Empty(sender.Sent);
        Assert.Equal(1, paster.AbandonedCount);
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void An_undefined_chord_in_a_hand_edited_file_falls_back_to_ctrl_v()
    {
        var settings = new PasteJumpSettings { PasteKeystroke = (PasteKeystroke)99 };

        settings.Normalise();

        Assert.Equal(PasteKeystroke.CtrlV, settings.PasteKeystroke);
    }

    [Fact]
    public void The_conflict_warning_is_on_by_default()
        // Off by default would mean the app's only symptom of the collision is pasting that does
        // nothing, which reads as a PasteJump bug rather than a conflict.
        => Assert.True(new PasteJumpSettings().WarnAboutClipboardManagerConflict);

    // ------------------------------------------------------------------ detection

    [Theory]
    [InlineData("Clipjump")]
    [InlineData("Clipjump_x64")]
    [InlineData("clipjump_x64")]
    [InlineData("Clipjump.exe")]
    [InlineData(@"D:\Lokesh\DoNotMove\Clipjump_x64\Clipjump_x64.exe")]
    public void Clipjump_is_recognised_however_it_is_spelled(string processName)
    {
        // The 64-bit build is Clipjump_x64.exe. Matching only "Clipjump" is how this check quietly fails
        // on the install that is actually most common.
        Assert.Equal(["Clipjump"], RivalClipboardManagers.Detect([processName]));
    }

    [Fact]
    public void Unrelated_processes_are_not_flagged()
    {
        // False positives are worse than misses here: they tell the user to change a setting that was
        // correct. PowerToys is the specific trap - its Advanced Paste is Ctrl+Shift+V and does not clash.
        var running = new[]
        {
            "explorer", "devenv", "msedge", "PowerToys.QuickAccess", "PasteJump", "WindowsTerminal", null,
        };

        Assert.Empty(RivalClipboardManagers.Detect(running));
    }

    [Fact]
    public void Each_manager_is_reported_once_however_many_processes_it_runs()
    {
        var found = RivalClipboardManagers.Detect(["Clipjump", "Clipjump_x64", "Ditto", "Ditto"]);

        Assert.Equal(["Clipjump", "Ditto"], found);
    }

    [Fact]
    public void The_conflict_message_names_the_manager_and_the_way_out()
    {
        var message = RivalClipboardManagers.DescribeConflict(["Clipjump"]);

        Assert.Contains("Clipjump is running", message, StringComparison.Ordinal);
        Assert.Contains("Shift+Insert", message, StringComparison.Ordinal);

        // The asymmetry is the confusing part and the thing a user reports, so it must be said outright.
        Assert.Contains("Copying still works", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_conflict_message_reads_correctly_for_more_than_one()
    {
        var message = RivalClipboardManagers.DescribeConflict(["Clipjump", "Ditto"]);

        Assert.Contains("Clipjump and Ditto are running", message, StringComparison.Ordinal);
    }
}
