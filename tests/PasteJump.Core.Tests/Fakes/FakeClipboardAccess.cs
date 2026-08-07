using PasteJump.Core.Abstractions;
using PasteJump.Core.Model;

namespace PasteJump.Core.Tests.Fakes;

/// <summary>
/// Scriptable clipboard. Lets tests stage read failures, which is the behaviour that matters most
/// - a real clipboard can and does refuse to open.
/// </summary>
internal sealed class FakeClipboardAccess : IClipboardAccess
{
    private readonly Queue<ClipboardSnapshot?> _reads = new();
    private readonly Queue<bool> _writeResults = new();

    public uint SequenceNumber { get; set; } = 1;

    public int ReadCallCount { get; private set; }

    public List<IReadOnlyList<ClipPayload>> Writes { get; } = [];

    /// <summary>Fallback returned once the scripted queue is exhausted.</summary>
    public ClipboardSnapshot? Standing { get; set; }

    /// <summary>Result returned by <see cref="TryWrite"/> once the scripted queue is exhausted.</summary>
    public bool WriteSucceeds { get; set; } = true;

    /// <summary>Queues the next read result. Null means "could not open the clipboard".</summary>
    public FakeClipboardAccess EnqueueRead(ClipboardSnapshot? snapshot)
    {
        _reads.Enqueue(snapshot);
        return this;
    }

    /// <summary>Queues the next write outcome, so a failing clipboard can be staged.</summary>
    public FakeClipboardAccess EnqueueWriteResult(bool succeeded)
    {
        _writeResults.Enqueue(succeeded);
        return this;
    }

    public ClipboardSnapshot? TryRead()
    {
        ReadCallCount++;
        return _reads.Count > 0 ? _reads.Dequeue() : Standing;
    }

    public bool TryWrite(IReadOnlyList<ClipPayload> payloads)
    {
        Writes.Add(payloads);
        return _writeResults.Count > 0 ? _writeResults.Dequeue() : WriteSucceeds;
    }

    public static ClipboardSnapshot TextSnapshot(string text, string? sourceExe = null)
    {
        var payload = new ClipPayload(13, null, System.Text.Encoding.Unicode.GetBytes(text));
        return new ClipboardSnapshot([payload], text, ClipKind.Text, sourceExe);
    }
}

/// <summary>
/// Counts paste keystrokes, and can refuse to send them - which is what a real
/// <c>SendInput</c> does when the foreground window is elevated.
/// </summary>
internal sealed class FakePasteSender : IPasteSender
{
    public int SendCount { get; private set; }

    /// <summary>Set false to simulate UIPI refusing the keystroke.</summary>
    public bool Succeeds { get; set; } = true;

    public bool SendPaste()
    {
        SendCount++;
        return Succeeds;
    }
}

/// <summary>Reports a fixed foreground process name.</summary>
internal sealed class FakeForegroundWindowInfo(string? processName = null) : IForegroundWindowInfo
{
    public string? ProcessName { get; set; } = processName;

    public string? GetForegroundProcessName() => ProcessName;
}

/// <summary>
/// Collects scheduled retries instead of waiting, so deferred-retry behaviour can be tested
/// without real time passing.
/// </summary>
internal sealed class ManualScheduler
{
    private readonly List<Action> _pending = [];

    public int ScheduledCount { get; private set; }

    public List<TimeSpan> Delays { get; } = [];

    public void Schedule(TimeSpan delay, Action action)
    {
        ScheduledCount++;
        Delays.Add(delay);
        _pending.Add(action);
    }

    /// <summary>Runs everything queued so far. Actions may queue more; those need another call.</summary>
    public int RunPending()
    {
        var batch = _pending.ToList();
        _pending.Clear();

        foreach (var action in batch)
        {
            action();
        }

        return batch.Count;
    }

    /// <summary>Drains the queue, following chained retries up to a sane ceiling.</summary>
    public void RunAll(int maxRounds = 10)
    {
        for (var round = 0; round < maxRounds && RunPending() > 0; round++)
        {
        }
    }
}
