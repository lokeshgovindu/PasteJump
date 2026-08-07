using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Clipjog.Core.Model;
using Clipjog.Core.PasteMode;

namespace Clipjog.Interop.Probe;

/// <summary>
/// Phase 0 spike harness. Not shipped - it exists to answer the two questions that decide whether
/// the whole design is viable, before any effort goes into building on top of it.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Win32ClipboardAccess _clipboard = new(new ForegroundWindowInfo());
    private readonly StringBuilder _keyLog = new();

    private LowLevelKeyboardHook? _hook;
    private ProbeOverlay? _overlay;
    private IReadOnlyList<ClipPayload>? _captured;

    private IntPtr _foregroundAtHookInstall;
    private int _foregroundChangeCount;
    private long _eventCount;

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => TearDown();
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

        _hook = new LowLevelKeyboardHook(OnKeyEvent);

        try
        {
            _hook.Install();
            Log("hook installed. Switch to another app and hold Ctrl while tapping V.");
            InstallHookButton.IsEnabled = false;
            RemoveHookButton.IsEnabled = true;
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
        InstallHookButton.IsEnabled = true;
        RemoveHookButton.IsEnabled = false;
        Log("hook removed.");
    }

    private bool OnKeyEvent(KeyboardHookEvent e)
    {
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

        return false;
    }

    private void UpdateHookStats()
    {
        if (_hook is null)
        {
            HookStatsText.Text = string.Empty;
            return;
        }

        var worst = _hook.WorstHandlerDuration.TotalMilliseconds;

        HookStatsText.Text =
            $"events={_eventCount}  worst handler={worst:0.000}ms  " +
            $"faults={_hook.HandlerFaultCount}  fg changes={_foregroundChangeCount}  " +
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

    private void Log(string line)
    {
        _keyLog.AppendLine($"{Stopwatch.GetTimestamp():X} {line}");

        // Keep the buffer bounded; a long session otherwise grows this without limit.
        if (_keyLog.Length > 200_000)
        {
            _keyLog.Remove(0, 100_000);
        }

        KeyLog.Text = _keyLog.ToString();
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
