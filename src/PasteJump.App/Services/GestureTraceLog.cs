using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace PasteJump.App.Services;

/// <summary>
/// A one-line-per-keystroke account of what the keyboard hook saw and what the recognizer did with it, written to
/// <c>logs\gesture.log</c> in the data folder.
/// </summary>
/// <remarks>
/// <para>
/// Exists because "the overlay does not appear in one particular application" cannot be diagnosed from outside the
/// process. It was tried: an external observer polling <c>GetAsyncKeyState</c> reported no Ctrl press at all across
/// ~500 samples while the application in question had the foreground, purely because the observing process could
/// not read input state for a window outside its own process tree. Every conclusion drawn that way was wrong. The
/// hook itself is the only witness that cannot be fooled about what the hook received.
/// </para>
/// <para>
/// <b>It ships, rather than living on a diagnostic branch, and that is a correction.</b> It was written as a
/// throwaway and left behind on <c>diag/gesture-trace</c>, which cost another day of guessing at the Edge report -
/// while the one deployment that did carry it had already settled the question in a single line: the gesture opens
/// perfectly well in Edge (<c>fg=msedge.exe ... recognizer HANDLED it, sessionNow=True</c>), which moves the search
/// off the recognizer entirely and onto what becomes of the overlay afterwards. An instrument that answers a
/// recurring question belongs in the product, not in a branch somebody has to remember to deploy. It costs a
/// bounded amount of disk - the file is capped and rewritten - and nothing measurable in the hook.
/// </para>
/// <para>
/// <b>Never writes to disk from the hook callback.</b> Lines are queued in memory and flushed by a thread-pool
/// timer, because the callback runs on the UI thread and blocks all keyboard input machine-wide - a file write
/// there is exactly how <c>LowLevelHooksTimeout</c> gets exceeded and Windows silently discards the hook, which
/// would destroy the behaviour being measured.
/// </para>
/// <para>
/// <b>Not a keylogger, and the distinction is structural rather than a promise.</b> Only the trigger key and the
/// modifiers are named. Every other key is recorded as <c>other</c> with no virtual key code, so the file cannot
/// reconstruct anything typed - while still answering the question that matters, which is whether the hook is
/// receiving events at all when a given application has the foreground.
/// </para>
/// </remarks>
internal sealed class GestureTraceLog : IDisposable
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private readonly string _path;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly Timer _flush;
    private readonly Lock _gate = new();

    public GestureTraceLog(string dataFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataFolder);

        var folder = Path.Combine(dataFolder, "logs");

        Directory.CreateDirectory(folder);

        _path = Path.Combine(folder, "gesture.log");

        _flush = new Timer(_ => Drain(), null, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(400));
    }

    public string Path_ => _path;

    /// <summary>
    /// Queues one line. Safe to call from the hook: an enqueue and a timestamp, no I/O and no lock contention with
    /// the flush.
    /// </summary>
    public void Note(string message)
        => _pending.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}");

    private void Drain()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var batch = new StringBuilder();

        while (_pending.TryDequeue(out var line))
        {
            batch.AppendLine(line);
        }

        try
        {
            lock (_gate)
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    File.Delete(_path);
                }

                File.AppendAllText(_path, batch.ToString(), Encoding.UTF8);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        _flush.Dispose();
        Drain();
    }
}
