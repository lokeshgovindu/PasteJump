using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;

namespace PasteJump.Interop.Probe;

/// <summary>
/// Phase 0 spike harness. Not shipped - it exists to answer the two questions that decide whether
/// the whole design is viable, before any effort goes into building on top of it.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Ring buffer of callback durations, in milliseconds. A ring rather than a growing list because the
    /// 30-minute typing run in the exit criteria would otherwise accumulate hundreds of thousands of samples;
    /// 20,000 is several minutes of hard typing, which is plenty for a percentile.
    /// </summary>
    private readonly double[] _latencies = new double[20_000];

    private readonly Win32ClipboardAccess _clipboard = new(new ForegroundWindowInfo());
    private readonly StringBuilder _keyLog = new();
    private readonly DispatcherTimer _idleTimer;
    private readonly Stopwatch _sinceLastEvent = new();

    private LowLevelKeyboardHook? _hook;
    private ProbeOverlay? _overlay;
    private IReadOnlyList<ClipPayload>? _captured;

    private IntPtr _foregroundAtHookInstall;
    private int _foregroundChangeCount;
    private long _eventCount;
    private int _latencyCount;
    private int _latencyNext;

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => TearDown();

        // Drives the "last event" age. A silently dropped hook - what happens if we ever exceed
        // LowLevelHooksTimeout - looks exactly like a working app that has stopped receiving keys, so the only
        // way to see it is to watch the gap since the last callback while still typing.
        _idleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _idleTimer.Tick += (_, _) => UpdateHookStats();
    }

    // ============================================================ Spike A

    private void OnInstallHook(object sender, RoutedEventArgs e)
    {
        if (_hook is not null)
        {
            return;
        }

        _foregroundAtHookInstall = GetForegroundWindow();
        _foregroundChangeCount = 0;
        _eventCount = 0;
        _latencyCount = 0;
        _latencyNext = 0;

        _hook = new LowLevelKeyboardHook(OnKeyEvent);

        try
        {
            _hook.Install();
            Log("hook installed. Switch to another app and hold Ctrl while tapping V.");
            InstallHookButton.IsEnabled = false;
            RemoveHookButton.IsEnabled = true;
            _sinceLastEvent.Restart();
            _idleTimer.Start();
        }
        catch (Exception ex)
        {
            Log($"INSTALL FAILED: {ex.Message}");
            _hook = null;
        }
    }

    private void OnRemoveHook(object sender, RoutedEventArgs e)
    {
        _hook?.Dispose();
        _hook = null;
        _idleTimer.Stop();
        _sinceLastEvent.Reset();
        InstallHookButton.IsEnabled = true;
        RemoveHookButton.IsEnabled = false;
        Log("hook removed.");
        Log(LatencyReport());
    }

    private bool OnKeyEvent(KeyboardHookEvent e)
    {
        // WH_KEYBOARD_LL callbacks are delivered to the thread that installed the hook, so this runs on the
        // UI thread - which is why nothing here may touch the UI directly, and why the sample buffer below
        // needs no synchronisation.
        var start = Stopwatch.GetTimestamp();

        _eventCount++;

        // Injected events are our own SendInput coming back around. Reporting them is useful here
        // precisely because the production code must ignore them.
        var key = VirtualKeyTranslator.ToGestureKey(e.VirtualKey);

        var foreground = GetForegroundWindow();

        if (foreground != _foregroundAtHookInstall)
        {
            _foregroundChangeCount++;
            _foregroundAtHookInstall = foreground;
        }

        // The log is appended on the dispatcher rather than inline: touching the UI from inside the
        // hook is exactly the mistake that blows the LowLevelHooksTimeout budget.
        var line =
            $"vk=0x{e.VirtualKey:X2} {(e.IsKeyDown ? "down" : "up  ")} " +
            $"gesture={key,-20} injected={e.IsInjected,-5} fg=0x{foreground.ToInt64():X} " +
            $"worst={_hook?.WorstHandlerDuration.TotalMilliseconds:0.000}ms";

        Dispatcher.BeginInvoke(() =>
        {
            Log(line);
            UpdateHookStats();
        });

        _latencies[_latencyNext] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _latencyNext = (_latencyNext + 1) % _latencies.Length;
        _latencyCount = Math.Min(_latencyCount + 1, _latencies.Length);

        _sinceLastEvent.Restart();

        return false;
    }

    /// <summary>
    /// Percentiles over the samples held in the ring, formatted for the report.
    /// <para>
    /// This is a deliberately <em>pessimistic</em> stand-in for the production hook: the probe's handler calls
    /// <c>GetForegroundWindow</c> and queues a log line on every single key, neither of which
    /// <c>PasteJumpPasteHost</c> does. A p95 comfortably under 1 ms here therefore means production is clear
    /// with room to spare; a p95 above it would need re-measuring without the logging before concluding
    /// anything.
    /// </para>
    /// </summary>
    private string LatencyReport()
    {
        if (_latencyCount == 0)
        {
            return "no callback samples recorded.";
        }

        var samples = new double[_latencyCount];
        Array.Copy(_latencies, samples, _latencyCount);
        Array.Sort(samples);

        var p95 = Percentile(samples, 0.95);

        return
            $"callback latency over {_latencyCount:N0} samples: " +
            $"p50={Percentile(samples, 0.50):0.000}ms  " +
            $"p95={p95:0.000}ms  " +
            $"p99={Percentile(samples, 0.99):0.000}ms  " +
            $"max={samples[^1]:0.000}ms  " +
            $"-> p95 {(p95 < 1 ? "PASSES" : "FAILS")} the sub-1ms exit criterion";
    }

    /// <summary>Nearest-rank percentile over an already-sorted array.</summary>
    private static double Percentile(double[] sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    private void UpdateHookStats()
    {
        if (_hook is null)
        {
            HookStatsText.Text = string.Empty;
            return;
        }

        var worst = _hook.WorstHandlerDuration.TotalMilliseconds;

        // Percentiles are not computed here: this runs per key event and sorting the ring on every keystroke
        // would make the harness itself the slow thing being measured. Save report and Remove hook print them.
        HookStatsText.Text =
            $"events={_eventCount}  worst={worst:0.000}ms  " +
            $"faults={_hook.HandlerFaultCount}  fg changes={_foregroundChangeCount}  " +
            $"last event={_sinceLastEvent.Elapsed.TotalSeconds:0}s  " +
            $"{(worst < 5 ? "OK" : "INVESTIGATE - approaching the 300ms hook timeout")}";
    }

    private void OnToggleOverlay(object sender, RoutedEventArgs e)
    {
        if (_overlay is { IsVisible: true })
        {
            _overlay.Hide();
            Log("probe overlay hidden.");
            return;
        }

        _overlay ??= new ProbeOverlay();
        _overlay.Show();

        var (x, y) = ForegroundWindowInfo.GetPreferredOverlayAnchor();
        var dpi = ForegroundWindowInfo.GetDpiForPoint(x, y);

        _overlay.ShowAt(x, y, dpi);
        Log($"probe overlay shown at physical ({x},{y}), monitor dpi={dpi}. " +
            "It must not take focus, and clicks must fall through it.");
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        _keyLog.Clear();
        KeyLog.Text = string.Empty;
    }

    /// <summary>
    /// Writes both spikes' output to a file under <c>artifacts/phase0/</c>.
    /// <para>
    /// A spike is only worth running if its result can be read afterwards, and the alternative here is
    /// transcribing a read-only text box by hand - or copying out of it, which would disturb the very
    /// clipboard Spike B is measuring. The path is deterministic rather than chosen through a dialog so the
    /// result can be picked up without being told where it went.
    /// </para>
    /// </summary>
    private void OnSaveReport(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "phase0");

        try
        {
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"spike-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var report = new StringBuilder();
            report.AppendLine("PasteJump Phase 0 spike report");
            report.AppendLine($"written       : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"machine       : {Environment.MachineName}");
            report.AppendLine($"os            : {Environment.OSVersion.VersionString}");
            report.AppendLine($"hook installed: {_hook?.IsInstalled == true}");
            report.AppendLine($"events        : {_eventCount:N0}");
            report.AppendLine($"handler faults: {_hook?.HandlerFaultCount ?? 0}");
            report.AppendLine($"fg changes    : {_foregroundChangeCount}");
            report.AppendLine();
            report.AppendLine("== Spike A - latency ==");
            report.AppendLine(LatencyReport());
            report.AppendLine();
            report.AppendLine("== Spike B - last capture or round-trip ==");
            report.AppendLine($"verdict: {VerdictText.Text}");
            report.AppendLine(FormatLog.Text);
            report.AppendLine();
            report.AppendLine("== Spike A - key log ==");
            report.Append(_keyLog);

            File.WriteAllText(path, report.ToString());
            Log($"report written to {path}");
        }
        catch (Exception ex)
        {
            Log($"SAVE FAILED: {ex.Message}");
        }
    }

    private void Log(string line)
    {
        var entry = $"{Stopwatch.GetTimestamp():X} {line}{Environment.NewLine}";
        _keyLog.Append(entry);

        // Keep the buffer bounded; a long session otherwise grows this without limit.
        if (_keyLog.Length > 200_000)
        {
            _keyLog.Remove(0, 100_000);
            KeyLog.Text = _keyLog.ToString();
        }
        else
        {
            // AppendText rather than reassigning Text, which would re-marshal the whole 200 KB buffer into the
            // control on every keystroke. Harmless in a short run; over the 30-minute typing test in the exit
            // criteria it makes the harness the slowest thing in the measurement.
            KeyLog.AppendText(entry);
        }

        KeyLog.ScrollToEnd();
    }

    // ============================================================ Spike B

    private void OnCapture(object sender, RoutedEventArgs e)
    {
        var snapshot = _clipboard.TryRead();

        if (snapshot is null)
        {
            VerdictText.Text = "clipboard could not be opened";
            FormatLog.Text = "TryRead returned null - another process is holding the clipboard.";
            return;
        }

        _captured = snapshot.Payloads;
        RoundTripButton.IsEnabled = true;
        RestoreButton.IsEnabled = true;

        var report = new StringBuilder();
        report.AppendLine($"kind          : {snapshot.Kind}");
        report.AppendLine($"source        : {snapshot.SourceExecutable ?? "(unknown)"}");
        report.AppendLine($"total bytes   : {snapshot.TotalBytes:N0}");
        report.AppendLine($"content hash  : {snapshot.ContentHash}");
        report.AppendLine($"formats       : {snapshot.Payloads.Count}");
        report.AppendLine();
        report.AppendLine($"{"id",-8} {"name",-34} {"bytes",12}");
        report.AppendLine(new string('-', 58));

        foreach (var payload in snapshot.Payloads.OrderBy(static p => p.FormatId))
        {
            report.AppendLine(
                $"{payload.FormatId,-8} {payload.FormatName ?? StandardFormatName(payload.FormatId),-34} {payload.ByteLength,12:N0}");
        }

        if (snapshot.Text is { Length: > 0 } text)
        {
            report.AppendLine();
            report.AppendLine("text preview:");
            report.AppendLine(text.Length > 500 ? text[..500] + "…" : text);
        }

        FormatLog.Text = report.ToString();
        VerdictText.Text = $"captured {snapshot.Payloads.Count} formats";
    }

    /// <summary>
    /// Writes the captured payloads back and re-reads, comparing byte for byte. This is the
    /// question Spike B exists to answer.
    /// </summary>
    private void OnRoundTrip(object sender, RoutedEventArgs e)
    {
        if (_captured is null)
        {
            return;
        }

        var originalHash = new ClipboardSnapshot(_captured, null, ClipKind.Other, null).ContentHash;

        if (!_clipboard.TryWrite(_captured))
        {
            VerdictText.Text = "write FAILED";
            return;
        }

        var reread = _clipboard.TryRead();

        if (reread is null)
        {
            VerdictText.Text = "re-read FAILED";
            return;
        }

        var report = new StringBuilder();
        report.AppendLine($"original hash : {originalHash}");
        report.AppendLine($"re-read hash  : {reread.ContentHash}");
        report.AppendLine();

        var mismatches = 0;

        foreach (var original in _captured.OrderBy(static p => p.FormatId))
        {
            var match = reread.Payloads.FirstOrDefault(p =>
                p.FormatId == original.FormatId
                || (original.FormatName is not null && p.FormatName == original.FormatName));

            var name = original.FormatName ?? StandardFormatName(original.FormatId);

            if (match is null)
            {
                // Expected for the formats we intentionally do not write back, since Windows
                // regenerates them. Flagged rather than silently ignored.
                var synthesised = original.FormatId is 1 or 7 or 16;
                report.AppendLine($"{(synthesised ? "SKIP " : "LOST ")} {name,-34} (not present after write)");

                if (!synthesised)
                {
                    mismatches++;
                }

                continue;
            }

            if (match.Data.AsSpan().SequenceEqual(original.Data))
            {
                report.AppendLine($"OK    {name,-34} {original.ByteLength,12:N0} bytes identical");
            }
            else
            {
                report.AppendLine(
                    $"DIFF  {name,-34} {original.ByteLength,12:N0} -> {match.ByteLength:N0} bytes");
                mismatches++;
            }
        }

        FormatLog.Text = report.ToString();

        VerdictText.Text = mismatches == 0
            ? "ROUND-TRIP CLEAN"
            : $"{mismatches} FORMAT(S) DID NOT SURVIVE";
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_captured is null)
        {
            return;
        }

        VerdictText.Text = _clipboard.TryWrite(_captured)
            ? "written - now paste into the target app and check fidelity"
            : "write FAILED";
    }

    private static string StandardFormatName(uint id) => id switch
    {
        1 => "CF_TEXT",
        2 => "CF_BITMAP",
        3 => "CF_METAFILEPICT",
        7 => "CF_OEMTEXT",
        8 => "CF_DIB",
        9 => "CF_PALETTE",
        13 => "CF_UNICODETEXT",
        14 => "CF_ENHMETAFILE",
        15 => "CF_HDROP",
        16 => "CF_LOCALE",
        17 => "CF_DIBV5",
        _ => $"(unnamed {id})",
    };

    private void TearDown()
    {
        _hook?.Dispose();
        _overlay?.Close();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
