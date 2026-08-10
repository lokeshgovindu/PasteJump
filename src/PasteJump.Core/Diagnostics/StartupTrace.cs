using System.Diagnostics;
using System.Globalization;

namespace PasteJump.Core.Diagnostics;

/// <summary>
/// Phase timings for start-up, for answering "why did that take so long".
/// <para>
/// Every <see cref="Mark"/> call site is compiled away in Release by <see cref="ConditionalAttribute"/>, so
/// this costs nothing at all in a shipped build - not a branch, not a string allocation, not the list. That is
/// why the marks can be sprinkled liberally through <c>Compose</c>.
/// </para>
/// <para>
/// The number that matters most is not measured here at all: it is <see cref="BeforeManagedCode"/>, the gap
/// between the OS creating the process and this class first being touched. For a single-file build that gap is
/// bundle extraction, assembly decompression, CLR start-up and WPF initialisation, and it is much larger than
/// anything the application itself does. Reporting only the phases we control would answer the question
/// misleadingly.
/// </para>
/// </summary>
public static class StartupTrace
{
    private static readonly Stopwatch Elapsed = Stopwatch.StartNew();
    private static readonly List<Phase> Phases = [];
    private static readonly Lock Gate = new();

    private static TimeSpan _previous = TimeSpan.Zero;

    /// <summary>One recorded step: when it finished, and how long it took.</summary>
    /// <param name="Name">What finished.</param>
    /// <param name="At">Time from the first mark to the end of this step.</param>
    /// <param name="Took">Time from the previous mark to this one.</param>
    public readonly record struct Phase(string Name, TimeSpan At, TimeSpan Took);

    /// <summary>
    /// How long the process existed before managed start-up reached this class, or null when it cannot be
    /// established.
    /// <para>
    /// Captured in a static initialiser on purpose: the first <see cref="Mark"/> is what triggers it, and by
    /// then bundle extraction and framework start-up have already happened. <c>Process.StartTime</c> is the
    /// only source for that span - a stopwatch started in <c>Main</c> has already missed it.
    /// </para>
    /// </summary>
    public static TimeSpan? BeforeManagedCode { get; } = ResolveProcessAge();

    /// <summary>Records that a step has finished. Compiled out of Release builds entirely.</summary>
    [Conditional("DEBUG")]
    public static void Mark(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (Gate)
        {
            var at = Elapsed.Elapsed;
            Phases.Add(new Phase(name, at, at - _previous));
            _previous = at;
        }
    }

    /// <summary>Everything recorded so far, oldest first.</summary>
    public static IReadOnlyList<Phase> Recorded
    {
        get
        {
            lock (Gate)
            {
                return [.. Phases];
            }
        }
    }

    /// <summary>
    /// The trace as lines of text, slowest-first summary last. Returns an empty list when nothing was
    /// recorded, which is what a Release build produces.
    /// </summary>
    public static IReadOnlyList<string> Format()
    {
        var phases = Recorded;

        if (phases.Count == 0)
        {
            return [];
        }

        var lines = new List<string>();

        if (BeforeManagedCode is { } preamble)
        {
            lines.Add(Line("process start to first mark", preamble));
            lines.Add("  (bundle extraction, CLR and WPF init - not application work)");
        }

        foreach (var phase in phases)
        {
            lines.Add(Line(phase.Name, phase.Took, phase.At));
        }

        var total = phases[^1].At;
        lines.Add(Line("TOTAL traced", total));

        if (BeforeManagedCode is { } before)
        {
            lines.Add(Line("TOTAL to here from process start", before + total));
        }

        // Named explicitly rather than left to be eyeballed: the point of the trace is to say which step to
        // look at, and a column of numbers invites reading the largest total rather than the largest step.
        var slowest = phases.OrderByDescending(static p => p.Took).First();
        lines.Add($"  slowest step: {slowest.Name} ({Milliseconds(slowest.Took)} ms)");

        return lines;
    }

    private static string Line(string name, TimeSpan took, TimeSpan? at = null)
    {
        var running = at is { } value
            ? $"  (at {Milliseconds(value)} ms)"
            : string.Empty;

        return $"  {Milliseconds(took),9} ms  {name}{running}";
    }

    private static string Milliseconds(TimeSpan span)
        => span.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);

    private static TimeSpan? ResolveProcessAge()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var age = DateTime.Now - self.StartTime;

            // A negative or absurd value means the clock moved or the platform lied; reporting nothing beats
            // reporting a number someone might act on.
            return age > TimeSpan.Zero && age < TimeSpan.FromMinutes(5) ? age : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
