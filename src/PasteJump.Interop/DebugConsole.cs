using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PasteJump.Interop;

/// <summary>
/// A console window for Debug builds, and the log sink that writes to it.
/// <para>
/// PasteJump is a <c>WinExe</c>, so Windows gives it no console and <c>Console.WriteLine</c> goes nowhere.
/// <see cref="Attach"/> allocates one. Every method here is <see cref="ConditionalAttribute"/>-marked on
/// <c>DEBUG</c>, so in a Release build the call sites do not exist: no console, no log lines, and no way to
/// leave diagnostics switched on by accident in a shipped build.
/// </para>
/// <para>
/// Deliberately not a logging framework. What this is for is watching what the application does while
/// developing it - start-up timings above all - and a framework would bring configuration, levels and a
/// dependency for a job that is one P/Invoke and a Write.
/// </para>
/// </summary>
public static class DebugConsole
{
    private const int AttachParentProcess = -1;

    private static bool _attached;

    private static string? _logFile;

    /// <summary>
    /// Lines written before a log file was known, so none is lost.
    /// <para>
    /// It has to be buffered: the most interesting lines are the start-up timings, and several of those are
    /// recorded before the data directory has been resolved - which is what decides where the log goes.
    /// </para>
    /// </summary>
    private static readonly List<string> Buffered = [];

    /// <summary>
    /// Starts writing to <c>pastejump-debug.log</c> in the given directory, flushing anything logged so far.
    /// The file is replaced on each run rather than appended, so what is in it is always this launch.
    /// </summary>
    [Conditional("DEBUG")]
    public static void SetLogDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "pastejump-debug.log");
            File.WriteAllLines(path, Buffered);

            _logFile = path;
            Buffered.Clear();
        }
        catch (Exception)
        {
            // A read-only or missing directory must not take the application down for the sake of a log.
            _logFile = null;
        }
    }

    /// <summary>Where the log is being written, or null when it is console-only.</summary>
    public static string? LogFile => _logFile;

    /// <summary>
    /// Allocates the console and points <see cref="Console"/> at it. Safe to call more than once.
    /// <para>
    /// It attaches to the parent's console first when there is one, so running the app from a terminal writes
    /// into that terminal rather than opening a second window beside it. Only when there is no parent console
    /// does it allocate its own.
    /// </para>
    /// </summary>
    [Conditional("DEBUG")]
    public static void Attach(string? title = null)
    {
        if (_attached)
        {
            return;
        }

        if (!AttachConsole(AttachParentProcess) && !AllocConsole())
        {
            // Neither worked - a session with no console at all, for instance. Not worth reporting: the
            // application's job is unaffected and Debug.WriteLine below still reaches a debugger.
            return;
        }

        _attached = true;

        // Console.Out is created lazily and caches the handle it found the first time. If anything has already
        // touched it - and WPF might - it is holding the handle from before the console existed, so it has to
        // be replaced rather than trusted.
        var stdout = Console.OpenStandardOutput();

        if (stdout != Stream.Null)
        {
            Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
        }

        if (!string.IsNullOrEmpty(title))
        {
            try
            {
                Console.Title = title;
            }
            catch (IOException)
            {
                // Setting the title fails when the console is redirected to a pipe. Cosmetic either way.
            }
        }
    }

    /// <summary>Writes one line, to the console and to any attached debugger.</summary>
    [Conditional("DEBUG")]
    public static void Log(string message)
    {
        Debug.WriteLine(message);

        if (_attached)
        {
            Console.WriteLine(message);
        }

        if (_logFile is null)
        {
            Buffered.Add(message);
            return;
        }

        try
        {
            File.AppendAllLines(_logFile, [message]);
        }
        catch (IOException)
        {
            // Something else has the file open. A dropped log line is not worth an exception on a path that
            // runs during start-up.
        }
    }

    /// <summary>Writes a titled block, indented, skipping it entirely when there is nothing to show.</summary>
    [Conditional("DEBUG")]
    public static void LogBlock(string title, IReadOnlyList<string> lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return;
        }

        Log(string.Empty);
        Log(title);

        foreach (var line in lines)
        {
            Log(line);
        }

        Log(string.Empty);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
