using PasteJump.Core;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The command line an elevated restart passes to its replacement.
/// <para>
/// Small, and worth pinning anyway: this is parsed before there is a window to report anything in, so every
/// malformed case has to degrade to "start normally" rather than throw. A crash here would be a PasteJump that
/// refuses to start after a failed elevation, which is the worst possible outcome of a feature whose whole
/// purpose is recovering privileges.
/// </para>
/// </summary>
public sealed class RelaunchRequestTests
{
    [Fact]
    public void The_process_id_is_read_from_the_switch()
    {
        Assert.Equal(4321, RelaunchRequest.TryParseReplacedProcessId(["--replace", "4321"]));
    }

    [Fact]
    public void The_switch_is_recognised_among_other_arguments()
    {
        Assert.Equal(77, RelaunchRequest.TryParseReplacedProcessId(["--something", "--replace", "77", "--more"]));
    }

    [Fact]
    public void The_switch_is_case_insensitive()
    {
        Assert.Equal(9, RelaunchRequest.TryParseReplacedProcessId(["--REPLACE", "9"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_value_that_is_not_a_process_id_yields_nothing(string value)
    {
        // Zero and negatives are not process ids, and a name is not one either. Null rather than an exception:
        // this runs before there is anywhere to report a problem, and starting normally is the sane outcome.
        Assert.Null(RelaunchRequest.TryParseReplacedProcessId(["--replace", value]));
    }

    [Fact]
    public void The_switch_with_no_value_at_all_yields_nothing()
    {
        Assert.Null(RelaunchRequest.TryParseReplacedProcessId(["--replace"]));
    }

    [Fact]
    public void An_ordinary_launch_yields_nothing()
    {
        Assert.Null(RelaunchRequest.TryParseReplacedProcessId([]));
        Assert.Null(RelaunchRequest.TryParseReplacedProcessId(null));
        Assert.Null(RelaunchRequest.TryParseReplacedProcessId(["--whatever"]));
    }

    /// <summary>The two halves have to agree, or the replacement waits for nobody.</summary>
    [Fact]
    public void What_is_written_is_what_is_read()
    {
        var arguments = RelaunchRequest.Arguments(2468).Split(' ');

        Assert.Equal(2468, RelaunchRequest.TryParseReplacedProcessId(arguments));
    }

    [Fact]
    public void The_logon_task_request_travels_with_the_relaunch()
    {
        // One UAC prompt has to buy both the elevation and the registration: asking twice for one decision is
        // how a switch comes to feel broken.
        var arguments = RelaunchRequest.Arguments(1234, enableElevatedLogon: true).Split(' ');

        Assert.Equal(1234, RelaunchRequest.TryParseReplacedProcessId(arguments));
        Assert.True(RelaunchRequest.WantsElevatedLogonTask(arguments));
    }

    [Fact]
    public void An_ordinary_relaunch_does_not_ask_for_the_logon_task()
    {
        var arguments = RelaunchRequest.Arguments(1234).Split(' ');

        Assert.False(RelaunchRequest.WantsElevatedLogonTask(arguments));
        Assert.False(RelaunchRequest.WantsElevatedLogonTask([]));
        Assert.False(RelaunchRequest.WantsElevatedLogonTask(null));
    }

    /// <summary>
    /// The wait is bounded. Waiting for ever would turn a restart into a process that never appears, which is
    /// worse than the mutex collision it avoids - and that collision already has a sane outcome of its own.
    /// </summary>
    [Fact]
    public void The_wait_is_bounded_and_short()
    {
        Assert.InRange(RelaunchRequest.MaxWait, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    }
}
