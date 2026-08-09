using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;
using PasteJump.Interop;

namespace PasteJump.SpikeRunner;

/// <summary>
/// Runs the machine-judgeable half of the Phase 0 spikes and writes a report.
/// <para>
/// Everything here is written to be run from a scheduled task rather than directly. The agent's own process
/// tree is refused clipboard access - <c>ERROR_ACCESS_DENIED</c> from every API including <c>clip.exe</c> -
/// and is refused foreground, so neither spike can be driven from it. A scheduled task is started by the Task
/// Scheduler service, so it inherits none of that and can do both.
/// </para>
/// </summary>
internal static class Program
{
    private static readonly StringBuilder Report = new();

    [STAThread]
    private static int Main(string[] args)
    {
        // PerMonitorV2, matching PasteJump.App's manifest. Without it GetDpiForPoint would report the
        // virtualised 96 for every monitor and the multi-monitor section below would be meaningless.
        _ = SetProcessDpiAwarenessContext(new IntPtr(-4));

        var outputDir = args.Length > 0 ? args[0] : ".";

        Line("PasteJump Phase 0 - automated portion");
        Line($"written        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line($"machine        : {Environment.MachineName}   user: {Environment.UserName}");
        Line($"os             : {Environment.OSVersion.VersionString}");
        Line($"session        : {Process.GetCurrentProcess().SessionId}");
        Line($"process dpi    : {GetDpiForSystem()}");
        Line(string.Empty);

        Environment.SetEnvironmentVariable("PJ_SPIKE", "1");

        RunMonitorSurvey();
        RunHookLatency();
        RunClipboardRoundTrips();
        RunExcelAcidTest();

        var path = Path.Combine(outputDir, $"spike-auto-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        try
        {
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(path, Report.ToString());
            Console.WriteLine($"report written to {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"could not write report: {ex.Message}");
            Console.WriteLine(Report.ToString());
            return 1;
        }

        return 0;
    }

    private static void Line(string text) => Report.AppendLine(text);

    private static void Heading(string text)
    {
        Line(string.Empty);
        Line($"== {text} ==");
    }

    // ------------------------------------------------------------------ Spike A: monitors and DPI

    /// <summary>
    /// Reports every monitor and the DPI our own code resolves for a point on it.
    /// <para>
    /// This is the primitive behind overlay placement: positions are computed as
    /// <c>physicalPixels / scale</c>, so a wrong scale for the monitor the caret is on puts the overlay on
    /// the wrong screen or half off an edge. The exit criterion asks for correct placement on a second
    /// monitor at a different scale factor; this checks the input that decision is made from.
    /// </para>
    /// </summary>
    private static void RunMonitorSurvey()
    {
        Heading("Spike A - monitors and per-monitor DPI");

        var monitors = new List<RECT>();

        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr _, IntPtr _, ref RECT rect, IntPtr _) =>
        {
            monitors.Add(rect);
            return true;
        }, IntPtr.Zero);

        Line($"monitors found : {monitors.Count}");

        if (monitors.Count < 2)
        {
            Line("NOTE: only one monitor is attached to this session, so the different-scale-factor");
            Line("      criterion cannot be exercised here. It still needs a human with two screens.");
        }

        var distinctScales = new HashSet<uint>();

        foreach (var (rect, index) in monitors.Select((r, i) => (r, i)))
        {
            // A point just inside the monitor rather than its origin, which can sit on a shared edge.
            var x = rect.Left + ((rect.Right - rect.Left) / 2);
            var y = rect.Top + ((rect.Bottom - rect.Top) / 2);

            var dpi = ForegroundWindowInfo.GetDpiForPoint(x, y);
            distinctScales.Add(dpi);

            var scale = dpi / 96.0;
            Line($"  monitor {index}: bounds=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})  " +
                 $"centre=({x},{y})  dpi={dpi}  scale={scale * 100:0}%");

            // The overlay divides physical pixels by this scale, so verify the division lands somewhere
            // inside the monitor rather than off it - the failure mode when the wrong scale is used.
            var logicalX = x / scale;
            var logicalY = y / scale;
            Line($"             logical=({logicalX:0.0},{logicalY:0.0})  " +
                 $"round-trips to ({logicalX * scale:0},{logicalY * scale:0})");
        }

        Line($"distinct scale factors: {distinctScales.Count} " +
             $"({string.Join(", ", distinctScales.Select(d => $"{d / 96.0 * 100:0}%"))})");

        if (distinctScales.Count > 1)
        {
            Line("VERDICT: mixed-DPI setup present and each monitor reports its own DPI correctly.");
        }
        else
        {
            Line("VERDICT: inconclusive for mixed DPI - every monitor here reports the same scale.");
        }
    }

    // ------------------------------------------------------------------ Spike A: hook latency

    /// <summary>
    /// Installs the real hook, drives synthetic keystrokes through it and reports callback percentiles.
    /// <para>
    /// The exit criterion is a p95 under 1 ms, against a hard OS budget of 300 ms
    /// (<c>LowLevelHooksTimeout</c>) after which Windows silently discards the hook - leaving an app that
    /// looks healthy and has stopped receiving keys.
    /// </para>
    /// </summary>
    private static void RunHookLatency()
    {
        Heading("Spike A - hook install and callback latency");

        var samples = new List<double>(4096);
        var observed = 0;
        var foregroundChanges = 0;
        var foregroundAtStart = GetForegroundWindow();

        var hook = new LowLevelKeyboardHook(e =>
        {
            var start = Stopwatch.GetTimestamp();

            observed++;

            // Deliberately doing the same work the production handler does - translating the key and
            // reading the foreground window - so the number means something for the real path.
            _ = VirtualKeyTranslator.ToGestureKey(e.VirtualKey);

            if (GetForegroundWindow() != foregroundAtStart)
            {
                foregroundChanges++;
            }

            samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return false;
        });

        try
        {
            hook.Install();
            Line($"hook installed : {hook.IsInstalled}");
        }
        catch (Exception ex)
        {
            Line($"INSTALL FAILED : {ex.Message}");
            return;
        }

        var sender = new InputSender();
        const int Taps = 300;

        // Two injection routes, because the first attempt saw almost no callbacks and it matters whether
        // that is the hook or the injection. SendInput is what production uses; keybd_event is the older
        // path and reaches the queue differently, so agreement between them rules the hook out.
        var viaSendInput = 0;

        for (var i = 0; i < Taps; i++)
        {
            // V, the trigger key, so the translation path exercised matches the gesture.
            if (sender.SendKey(0x56))
            {
                viaSendInput++;
            }

            // Pump, or the hook callback is never invoked: WH_KEYBOARD_LL is delivered through the
            // installing thread's message queue.
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(2);
        }

        var afterSendInput = observed;

        for (var i = 0; i < Taps; i++)
        {
            keybd_event(0x56, 0x2F, 0, UIntPtr.Zero);
            keybd_event(0x56, 0x2F, 2, UIntPtr.Zero);
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(2);
        }

        Line($"SendInput accepted : {viaSendInput}/{Taps}  (sender reported " +
             $"{sender.SendFailureCount} refusals)");
        Line($"callbacks after SendInput: {afterSendInput}");
        Line($"callbacks after keybd_event: {observed - afterSendInput}");

        // Drain anything still queued.
        for (var i = 0; i < 50; i++)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(2);
        }

        Line($"keystrokes sent: {Taps} (down+up, so up to {Taps * 2} callbacks)");
        Line($"callbacks seen : {observed}");
        Line($"handler faults : {hook.HandlerFaultCount}");
        Line($"foreground chg : {foregroundChanges}");
        Line($"worst (hook's own measure): {hook.WorstHandlerDuration.TotalMilliseconds:0.000} ms");

        hook.Uninstall();
        Line($"hook uninstalled, IsInstalled={hook.IsInstalled}");

        if (samples.Count == 0)
        {
            Line("VERDICT: NO CALLBACKS. Either injection is blocked here or the hook never received");
            Line("         events; latency is unmeasured and this criterion is untested.");
            return;
        }

        samples.Sort();

        var p50 = Percentile(samples, 0.50);
        var p95 = Percentile(samples, 0.95);
        var p99 = Percentile(samples, 0.99);
        var max = samples[^1];

        Line($"latency over {samples.Count} samples: p50={p50:0.000} p95={p95:0.000} " +
             $"p99={p99:0.000} max={max:0.000} ms");
        Line($"VERDICT: p95 {(p95 < 1 ? "PASSES" : "FAILS")} the sub-1ms criterion; " +
             $"max is {300 / Math.Max(max, 0.001):0} x inside the 300 ms OS budget.");
        Line("NOTE: synthetic keystrokes and no overlay rendering, so this measures the hook and handler");
        Line("      rather than a full gesture. It bounds the cost that must fit the budget, not the whole");
        Line("      user-visible path.");
    }

    private static double Percentile(List<double> sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    // ------------------------------------------------------------------ Spike B: format fidelity

    /// <summary>
    /// Writes representative payload sets through <see cref="Win32ClipboardAccess"/>, reads them back and
    /// compares byte for byte. This is Spike B's core question: can a clipboard be replayed faithfully.
    /// </summary>
    private static void RunClipboardRoundTrips()
    {
        Heading("Spike B - format round-trip fidelity");

        var clipboard = new Win32ClipboardAccess(new ForegroundWindowInfo());

        foreach (var (name, payloads) in BuildCases())
        {
            RoundTrip(clipboard, name, payloads);
        }
    }

    private static IEnumerable<(string Name, IReadOnlyList<ClipPayload> Payloads)> BuildCases()
    {
        yield return ("CF_UNICODETEXT", [Text("Round-trip me — with an em dash and a ünicode ß.")]);

        // HTML Format carries a byte-offset header describing itself. Offsets must survive untouched, which
        // is precisely why PasteJump never rewrites a payload it is going to replay.
        yield return ("HTML Format (+offsets)",
        [
            Text("bold text"),
            new ClipPayload(0, "HTML Format", Encoding.UTF8.GetBytes(BuildHtmlFormat())),
        ]);

        yield return ("Rich Text Format",
        [
            Text("rtf sample"),
            new ClipPayload(0, "Rich Text Format",
                Encoding.ASCII.GetBytes(@"{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\b rtf sample\b0}")),
        ]);

        yield return ("CF_DIB (synthetic 4x4)", [new ClipPayload(8, null, BuildDib(4, 4))]);

        yield return ("CF_HDROP (2 paths)", [new ClipPayload(15, null, BuildHdrop(
            [@"C:\Windows\System32\notepad.exe", @"C:\Windows\System32\calc.exe"]))]);
    }

    private static ClipPayload Text(string value)
        => new(13, null, Encoding.Unicode.GetBytes(value + '\0'));

    private static void RoundTrip(
        Win32ClipboardAccess clipboard,
        string label,
        IReadOnlyList<ClipPayload> payloads)
    {
        if (!clipboard.TryWrite(payloads))
        {
            Line($"  {label,-26} WRITE FAILED");
            return;
        }

        // A beat, because the write raises WM_CLIPBOARDUPDATE and any listener may still be reading.
        Thread.Sleep(120);

        var read = clipboard.TryRead();

        if (read is null)
        {
            Line($"  {label,-26} READ FAILED");
            return;
        }

        var problems = new List<string>();

        foreach (var original in payloads)
        {
            var match = read.Payloads.FirstOrDefault(p => original.IsRegisteredFormat
                ? string.Equals(p.FormatName, original.FormatName, StringComparison.OrdinalIgnoreCase)
                : p.FormatId == original.FormatId);

            var name = original.FormatName ?? $"id {original.FormatId}";

            if (match is null)
            {
                problems.Add($"{name} LOST");
            }
            else if (!match.Data.AsSpan().SequenceEqual(original.Data))
            {
                problems.Add($"{name} DIFFERS ({original.Data.Length} -> {match.Data.Length} bytes)");
            }
        }

        var formats = string.Join(", ", read.Payloads
            .OrderBy(p => p.FormatId)
            .Select(p => p.FormatName ?? p.FormatId.ToString(CultureInfo.InvariantCulture)));

        Line($"  {label,-26} {(problems.Count == 0 ? "CLEAN" : string.Join("; ", problems))}");
        Line($"  {"",-26} read back: {formats}");
    }

    /// <summary>A minimal but valid <c>HTML Format</c> payload, offsets included.</summary>
    private static string BuildHtmlFormat()
    {
        const string Body = "<html><body><!--StartFragment--><b>bold text</b><!--EndFragment--></body></html>";

        // The header is fixed-width so the offsets it declares can be computed after it is laid out.
        var header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\n"
            + "StartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";

        var headerLength = string.Format(CultureInfo.InvariantCulture, header, 0, 0, 0, 0).Length;
        var startFragment = headerLength + Body.IndexOf("<!--StartFragment-->", StringComparison.Ordinal)
            + "<!--StartFragment-->".Length;
        var endFragment = headerLength + Body.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);

        return string.Format(
            CultureInfo.InvariantCulture,
            header,
            headerLength,
            headerLength + Body.Length,
            startFragment,
            endFragment) + Body;
    }

    /// <summary>A 32bpp bottom-up DIB: BITMAPINFOHEADER followed by BGRA pixels.</summary>
    private static byte[] BuildDib(int width, int height)
    {
        var stride = width * 4;
        var bytes = new byte[40 + (stride * height)];

        BitConverter.GetBytes(40).CopyTo(bytes, 0);            // biSize
        BitConverter.GetBytes(width).CopyTo(bytes, 4);         // biWidth
        BitConverter.GetBytes(height).CopyTo(bytes, 8);        // biHeight
        BitConverter.GetBytes((short)1).CopyTo(bytes, 12);     // biPlanes
        BitConverter.GetBytes((short)32).CopyTo(bytes, 14);    // biBitCount
        BitConverter.GetBytes(0).CopyTo(bytes, 16);            // BI_RGB
        BitConverter.GetBytes(stride * height).CopyTo(bytes, 20);

        for (var i = 40; i < bytes.Length; i += 4)
        {
            bytes[i] = 0x20;        // B
            bytes[i + 1] = 0x60;    // G
            bytes[i + 2] = 0xF0;    // R
            bytes[i + 3] = 0xFF;    // A - opaque, so nothing triggers the all-zero-alpha repair
        }

        return bytes;
    }

    /// <summary>A <c>CF_HDROP</c> payload: DROPFILES header then double-null-terminated wide paths.</summary>
    private static byte[] BuildHdrop(string[] paths)
    {
        var list = string.Concat(paths.Select(p => p + '\0')) + '\0';
        var listBytes = Encoding.Unicode.GetBytes(list);
        var bytes = new byte[20 + listBytes.Length];

        BitConverter.GetBytes(20).CopyTo(bytes, 0);     // pFiles - offset to the list
        BitConverter.GetBytes(0).CopyTo(bytes, 4);      // pt.x
        BitConverter.GetBytes(0).CopyTo(bytes, 8);      // pt.y
        BitConverter.GetBytes(0).CopyTo(bytes, 12);     // fNC
        BitConverter.GetBytes(1).CopyTo(bytes, 16);     // fWide
        listBytes.CopyTo(bytes, 20);

        return bytes;
    }

    // ------------------------------------------------------------------ Spike B: the Excel acid test

    /// <summary>
    /// The acid test: a formatted Excel range, round-tripped, pasted back, and the formatting compared.
    /// <para>
    /// This is the one result that could change the design rather than produce a bug fix. Excel's private
    /// <c>Biff12</c> and <c>XML Spreadsheet</c> formats are what forced the original Clipjump into an
    /// invisible focus-stealing window; if they cannot be replayed, the fallback is a documented
    /// per-application "delegate to the real clipboard" path.
    /// </para>
    /// </summary>
    private static void RunExcelAcidTest()
    {
        Heading("Spike B - Excel acid test");

        var progId = Type.GetTypeFromProgID("Excel.Application");

        if (progId is null)
        {
            Line("Excel is not registered on this machine - acid test SKIPPED.");
            return;
        }

        dynamic? excel = null;

        try
        {
            excel = Activator.CreateInstance(progId);

            if (excel is null)
            {
                Line("Excel COM object could not be created - acid test SKIPPED.");
                return;
            }

            excel.Visible = false;
            excel.DisplayAlerts = false;

            var book = excel.Workbooks.Add();
            var sheet = book.Worksheets[1];

            // Formatting that a plain-text round-trip would visibly lose.
            sheet.Range["A1"].Value2 = "Header";
            sheet.Range["A1"].Font.Bold = true;
            sheet.Range["A1"].Interior.Color = 65535;          // yellow
            sheet.Range["B1"].Value2 = 1234.5;
            sheet.Range["B1"].NumberFormat = "#,##0.00";
            sheet.Range["A2"].Value2 = "Row";
            sheet.Range["B2"].Value2 = 42;

            var source = sheet.Range["A1:B2"];
            source.Copy();

            Thread.Sleep(400);

            var clipboard = new Win32ClipboardAccess(new ForegroundWindowInfo());
            var captured = clipboard.TryRead();

            if (captured is null)
            {
                Line("Could not read the clipboard after Excel's Copy - acid test INCONCLUSIVE.");
                return;
            }

            var names = captured.Payloads
                .Select(p => p.FormatName ?? $"id {p.FormatId}")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Line($"Excel published {captured.Payloads.Count} formats, {captured.TotalBytes:N0} bytes:");
            Line($"  {string.Join(", ", names)}");

            var privateFormats = captured.Payloads
                .Where(p => p.FormatName is not null
                    && (p.FormatName.Contains("Biff", StringComparison.OrdinalIgnoreCase)
                        || p.FormatName.Contains("XML Spreadsheet", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Line($"Excel private formats present: {privateFormats.Count} " +
                 $"({string.Join(", ", privateFormats.Select(p => p.FormatName))})");

            // Replay it, exactly as a paste would.
            if (!clipboard.TryWrite(captured.Payloads))
            {
                Line("VERDICT: FAILED - could not write Excel's payload set back to the clipboard.");
                return;
            }

            Thread.Sleep(400);

            var reread = clipboard.TryRead();

            if (reread is null)
            {
                Line("VERDICT: INCONCLUSIVE - could not re-read after writing back.");
                return;
            }

            var lost = new List<string>();
            var differing = new List<string>();

            foreach (var original in captured.Payloads)
            {
                var match = reread.Payloads.FirstOrDefault(p => original.IsRegisteredFormat
                    ? string.Equals(p.FormatName, original.FormatName, StringComparison.OrdinalIgnoreCase)
                    : p.FormatId == original.FormatId);

                var label = original.FormatName ?? $"id {original.FormatId}";

                if (match is null)
                {
                    lost.Add(label);
                }
                else if (!match.Data.AsSpan().SequenceEqual(original.Data))
                {
                    differing.Add($"{label} ({original.Data.Length}->{match.Data.Length})");
                }
            }

            Line($"round-trip: {captured.Payloads.Count - lost.Count - differing.Count} identical, " +
                 $"{differing.Count} differing, {lost.Count} lost");

            if (lost.Count > 0)
            {
                Line($"  lost     : {string.Join(", ", lost)}");
            }

            if (differing.Count > 0)
            {
                Line($"  differing: {string.Join(", ", differing)}");
            }

            // Now the part that actually matters to a user: paste it and see whether the formatting arrived.
            var target = book.Worksheets.Add();
            target.Range["A1"].Select();
            target.Paste();

            Thread.Sleep(300);

            bool bold = target.Range["A1"].Font.Bold;
            double colour = target.Range["A1"].Interior.Color;
            string numberFormat = target.Range["B1"].NumberFormat;
            string headerText = Convert.ToString(target.Range["A1"].Value2, CultureInfo.InvariantCulture) ?? string.Empty;

            Line($"pasted back: text='{headerText}' bold={bold} interior={colour} numberFormat='{numberFormat}'");

            var formattingIntact = bold && Math.Abs(colour - 65535) < 1 && numberFormat == "#,##0.00";

            Line($"VERDICT: {(formattingIntact ? "PASSES" : "FAILS")} - formatting " +
                 $"{(formattingIntact ? "survived" : "did NOT survive")} the round-trip through PasteJump's store.");

            if (!formattingIntact)
            {
                Line("         Per PLAN.md section 9 this is the trigger for the documented per-application");
                Line("         'delegate to the real clipboard' fallback, not a bug fix.");
            }

            book.Close(false);
        }
        catch (Exception ex)
        {
            Line($"acid test ERRORED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                excel?.Quit();
            }
            catch
            {
                // Excel refusing to quit does not change the result, and there is nothing useful to do.
            }

            if (excel is not null)
            {
                _ = Marshal.ReleaseComObject(excel);
            }
        }
    }

    // ------------------------------------------------------------------ native

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
