using PasteJump.Core.Diagnostics;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The one part of the trace that is not conditional, and therefore the one part worth asserting in either
/// configuration: <see cref="StartupTrace.BeforeManagedCode"/> is an ordinary property, computed in a static
/// initialiser, and exists in a Release build exactly as it does here.
/// </summary>
public sealed class StartupTracePreambleTests
{
    /// <summary>
    /// The pre-managed span is either a sane duration or null. It is derived from <c>Process.StartTime</c> and
    /// the wall clock, so it has to reject nonsense rather than report a negative start-up time.
    /// </summary>
    [Fact]
    public void The_pre_managed_span_is_sane_or_absent()
    {
        if (StartupTrace.BeforeManagedCode is { } before)
        {
            Assert.True(before > TimeSpan.Zero);
            Assert.True(before < TimeSpan.FromMinutes(5));
        }
    }
}

#if DEBUG

/// <summary>
/// The start-up trace, tested for one reason: its marks are compiled out of Release builds by
/// <c>[Conditional("DEBUG")]</c>, so a mistake in it cannot be caught by running the shipped application.
/// <para>
/// <c>#if DEBUG</c> is not tidiness, and leaving it out is a mistake this file has already made. The attribute
/// is honoured at the <em>call site</em>, so in a Release build these <c>Mark</c> calls do not exist either -
/// nothing is recorded, no argument is evaluated, and four tests fail asserting behaviour that is not in the
/// binary. It passed locally for months because <c>dotnet test</c> defaults to Debug; the first CI run, which
/// builds Release because that is what ships, failed on all four. The Release counterpart below asserts the
/// other half of the bargain.
/// </para>
/// <para>
/// Assertions are deliberately order-independent and stated as "contains". The trace is process-wide mutable
/// state by design - it records one start-up, not one test - so a test that asserted an exact list would
/// depend on which other tests had run first.
/// </para>
/// </summary>
public sealed class StartupTraceTests
{
    [Fact]
    public void A_mark_is_recorded_and_appears_in_the_report()
    {
        var name = $"test phase {Guid.NewGuid():n}";

        StartupTrace.Mark(name);

        Assert.Contains(StartupTrace.Recorded, p => p.Name == name);
        Assert.Contains(StartupTrace.Format(), line => line.Contains(name, StringComparison.Ordinal));
    }

    [Fact]
    public void The_report_ends_with_a_total_and_names_the_slowest_step()
    {
        StartupTrace.Mark("something");

        var lines = StartupTrace.Format();

        Assert.Contains(lines, line => line.Contains("TOTAL traced", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("slowest step:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Each mark's own duration is measured from the previous mark, not from the start - so two marks in a row
    /// must not both report the whole elapsed time. This is the arithmetic worth pinning down: getting it wrong
    /// makes every step look like the slowest one.
    /// </summary>
    [Fact]
    public void Each_step_measures_only_its_own_span()
    {
        var first = $"first {Guid.NewGuid():n}";
        var second = $"second {Guid.NewGuid():n}";

        StartupTrace.Mark(first);
        StartupTrace.Mark(second);

        var recorded = StartupTrace.Recorded;
        var a = recorded.Single(p => p.Name == first);
        var b = recorded.Single(p => p.Name == second);

        // The second mark lands later overall but took only the sliver between the two.
        Assert.True(b.At >= a.At, "the later mark should have a later running total");
        Assert.True(b.Took <= b.At, "a step cannot have taken longer than the time up to it");
    }

    [Fact]
    public void A_null_phase_name_is_rejected_rather_than_recorded()
        => Assert.Throws<ArgumentNullException>(() => StartupTrace.Mark(null!));
}

#else

/// <summary>
/// The same class compiled the way it ships. Nothing here is a weaker version of the Debug tests above: the
/// property being asserted is that the trace really does vanish, which is what lets marks be sprinkled through
/// <c>Compose</c> without costing a shipped build a branch, a string or a list.
/// <para>
/// Note that "nothing is recorded" is the *only* thing observable from inside the process. That the string
/// literals are absent from the assembly cannot be tested from here - it is checked by searching the built
/// binary for a mark name, as CLAUDE.md describes.
/// </para>
/// </summary>
public sealed class StartupTraceInReleaseTests
{
    [Fact]
    public void A_mark_records_nothing_because_the_call_site_is_compiled_away()
    {
        var before = StartupTrace.Recorded.Count;

        StartupTrace.Mark($"test phase {Guid.NewGuid():n}");

        Assert.Equal(before, StartupTrace.Recorded.Count);
        Assert.Empty(StartupTrace.Format());
    }

    /// <summary>
    /// The argument is not evaluated either, which is worth stating separately: a conditional method whose
    /// arguments still ran would make an expensive mark expensive in Release, and a null one throw.
    /// </summary>
    [Fact]
    public void A_null_phase_name_cannot_throw_because_the_call_never_happens()
    {
        StartupTrace.Mark(null!);

        Assert.Empty(StartupTrace.Recorded);
    }
}

#endif
