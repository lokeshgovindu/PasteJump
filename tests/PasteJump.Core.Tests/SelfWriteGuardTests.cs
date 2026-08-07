using PasteJump.Core.Abstractions;
using PasteJump.Core.Capture;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The mechanism that stops a paste from being re-captured as a new clip. This replaces the
/// original's timing-based <c>blockMonitoring</c> flag and 200 ms heuristic.
/// </summary>
public class SelfWriteGuardTests
{
    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    [Fact]
    public void RecognisesItsOwnWrite()
    {
        var guard = new SelfWriteGuard();

        guard.NoteWrite("abc123");

        Assert.True(guard.IsOwnWrite("abc123"));
    }

    [Fact]
    public void DoesNotClaimAnUnrelatedWrite()
    {
        var guard = new SelfWriteGuard();

        guard.NoteWrite("abc123");

        Assert.False(guard.IsOwnWrite("different"));
    }

    [Fact]
    public void SuppressionIsConsumedAfterOneMatch()
    {
        var guard = new SelfWriteGuard();

        guard.NoteWrite("abc123");

        Assert.True(guard.IsOwnWrite("abc123"));

        // One write produces one notification. Leaving the entry behind would silently swallow a
        // genuine user copy of the same content immediately afterwards.
        Assert.False(guard.IsOwnWrite("abc123"));
    }

    [Fact]
    public void SuppressionExpires_SoReCopyingTheSameContentLaterStillCounts()
    {
        var clock = new MutableClock();
        var guard = new SelfWriteGuard(clock, TimeSpan.FromSeconds(5));

        guard.NoteWrite("abc123");
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.False(guard.IsOwnWrite("abc123"));
    }

    [Fact]
    public void WithinTtl_SuppressionHolds()
    {
        var clock = new MutableClock();
        var guard = new SelfWriteGuard(clock, TimeSpan.FromSeconds(5));

        guard.NoteWrite("abc123");
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(guard.IsOwnWrite("abc123"));
    }

    [Fact]
    public void EmptyHashIsNeverTreatedAsOurOwnWrite()
    {
        var guard = new SelfWriteGuard();

        guard.NoteWrite(string.Empty);

        Assert.False(guard.IsOwnWrite(string.Empty));
    }

    [Fact]
    public void DoesNotGrowWithoutBound()
    {
        var guard = new SelfWriteGuard(maxEntries: 8);

        for (var i = 0; i < 500; i++)
        {
            guard.NoteWrite($"hash-{i}");
        }

        // The most recent write must still be recognised even after heavy churn.
        Assert.True(guard.IsOwnWrite("hash-499"));

        // Something long evicted must not be.
        Assert.False(guard.IsOwnWrite("hash-0"));
    }

    [Fact]
    public void Clear_ForgetsEverything()
    {
        var guard = new SelfWriteGuard();

        guard.NoteWrite("abc123");
        guard.Clear();

        Assert.False(guard.IsOwnWrite("abc123"));
    }
}
