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

    /// <summary>
    /// A second notification for one paste is still recognisable after <c>IsOwnWrite</c> has consumed its entry.
    /// Without this, an application that republishes the clipboard after the settle window closed produced a read
    /// that looked like a fresh copy of identical text - and the consecutive-duplicate branch announces itself, so
    /// every paste into such an application ended with a "Same as the last copy" toast.
    /// </summary>
    [Fact]
    public void RecognisesASecondNotificationForTheSameWrite()
    {
        var guard = new SelfWriteGuard();

        guard.NoteWrite("abc123");

        Assert.True(guard.IsOwnWrite("abc123"));
        Assert.False(guard.IsOwnWrite("abc123"), "the entry is consumed, which is what made the echo invisible");
        Assert.True(guard.IsEchoOfOwnWrite("abc123"));
    }

    /// <summary>Does not consume, because an application may publish more than twice.</summary>
    [Fact]
    public void AnEchoStaysRecognisableForMoreThanOneNotification()
    {
        var guard = new SelfWriteGuard();

        guard.NoteWrite("abc123");
        Assert.True(guard.IsOwnWrite("abc123"));

        Assert.True(guard.IsEchoOfOwnWrite("abc123"));
        Assert.True(guard.IsEchoOfOwnWrite("abc123"));
        Assert.True(guard.IsEchoOfOwnWrite("abc123"));
    }

    /// <summary>
    /// The reason the echo window is short. A copy the user really made of text they had just pasted is still
    /// theirs to be told about, and the content is identical by definition - so only time can tell the two apart.
    /// </summary>
    [Fact]
    public void AGenuineRecopyAfterTheEchoWindowIsNotTreatedAsAnEcho()
    {
        var clock = new MutableClock();
        var guard = new SelfWriteGuard(clock, echoWindow: TimeSpan.FromMilliseconds(200));

        guard.NoteWrite("abc123");
        Assert.True(guard.IsOwnWrite("abc123"));
        Assert.True(guard.IsEchoOfOwnWrite("abc123"));

        clock.Advance(TimeSpan.FromMilliseconds(500));

        Assert.False(guard.IsEchoOfOwnWrite("abc123"));
    }

    /// <summary>An echo is never claimed for content we never wrote.</summary>
    [Fact]
    public void SomethingWeNeverWroteIsNotAnEcho()
    {
        var guard = new SelfWriteGuard();

        guard.NoteWrite("abc123");
        Assert.True(guard.IsOwnWrite("abc123"));

        Assert.False(guard.IsEchoOfOwnWrite("something else entirely"));
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
