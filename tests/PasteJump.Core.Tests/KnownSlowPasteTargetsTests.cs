using PasteJump.Core.Paste;
using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The suggested delays for applications known to cache the clipboard. Offered by a button rather than applied,
/// because a table nobody asked for would change every paste into a dozen programs on the strength of a guess about
/// someone else's machine.
/// </summary>
public class KnownSlowPasteTargetsTests
{
    /// <summary>
    /// Every suggestion must be usable as typed. A value outside the bound would be clamped the moment it was saved,
    /// so the grid would show one number and the app would use another.
    /// </summary>
    [Fact]
    public void Every_suggested_delay_is_inside_the_bound()
    {
        foreach (var target in KnownSlowPasteTargets.All)
        {
            Assert.True(
                SettingsBounds.PasteSettleDelayMs.Admits(target.Milliseconds),
                $"{target.Process} suggests {target.Milliseconds} ms, outside the accepted range.");
        }
    }

    /// <summary>
    /// And every one must be longer than the global default, or it is not a suggestion - it is a row that does
    /// nothing while looking as though it does something.
    /// </summary>
    [Fact]
    public void Every_suggestion_is_longer_than_the_default()
    {
        var standard = new PasteJumpSettings().PasteSettleDelayMs;

        foreach (var target in KnownSlowPasteTargets.All)
        {
            Assert.True(target.Milliseconds > standard, $"{target.Process} suggests {target.Milliseconds} ms.");
        }
    }

    [Fact]
    public void Every_entry_names_a_program_and_says_why()
    {
        foreach (var target in KnownSlowPasteTargets.All)
        {
            Assert.NotNull(ExcludedApps.Normalise(target.Process));
            Assert.False(string.IsNullOrWhiteSpace(target.Why));
        }
    }

    /// <summary>No duplicates, or adding the set would produce a table the dialog then refuses.</summary>
    [Fact]
    public void The_list_holds_each_program_once()
    {
        var names = KnownSlowPasteTargets.All.Select(static t => t.Process).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Null(PerAppSettleDelays.Validate(
            KnownSlowPasteTargets.All.Select(static t => (t.Process, t.Milliseconds))));
    }

    /// <summary>
    /// Pressing the button twice must add nothing the second time, and a value the user has tuned must never be
    /// overwritten by the suggestion.
    /// </summary>
    [Fact]
    public void Programs_already_listed_are_left_alone()
    {
        var all = KnownSlowPasteTargets.All.Select(static t => t.Process).ToList();

        Assert.Empty(KnownSlowPasteTargets.NotAlreadyListed(all));

        var remaining = KnownSlowPasteTargets.NotAlreadyListed(["WINWORD.EXE"]);

        Assert.DoesNotContain(remaining, t => t.Process.Equals("winword.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(KnownSlowPasteTargets.All.Count - 1, remaining.Count);
    }

    /// <summary>Matching is on the file name, so a full path or different case still counts as listed.</summary>
    [Fact]
    public void An_existing_entry_matches_however_it_was_written()
    {
        var remaining = KnownSlowPasteTargets.NotAlreadyListed(
            [@"C:\Program Files\Microsoft Office\root\Office16\winword.exe"]);

        Assert.DoesNotContain(remaining, t => t.Process.Equals("WINWORD.EXE", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Excel earns its place: it is the acid test this whole delay exists for.</summary>
    [Fact]
    public void The_office_and_electron_families_are_covered()
    {
        var names = KnownSlowPasteTargets.All.Select(static t => t.Process).ToList();

        Assert.Contains("EXCEL.EXE", names);
        Assert.Contains("WINWORD.EXE", names);
        Assert.Contains("ms-teams.exe", names);
        Assert.Contains("mstsc.exe", names);
    }
}
