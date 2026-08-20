using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using Application = System.Windows.Application;
using Window = System.Windows.Window;
using PasteJump.App.Views;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;
using PasteJump.Interop;

namespace PasteJump.OverlaySpike;

/// <summary>
/// Asks, for every application running on this machine: does PasteJump's paste overlay end up somewhere a person
/// can actually see?
/// </summary>
/// <remarks>
/// <para>
/// Written because a whole day of "I cannot see the overlay in Edge" could not be settled by reasoning, by unit
/// tests, or by driving the real gesture - an agent's process is refused input injection, and the placement rules
/// were provably correct while the result was reportedly invisible. This closes the loop the only honest way:
/// focus each window in turn, place the REAL overlay through the REAL placement code, then photograph the screen
/// where the overlay claims to be and compare it against what the overlay actually looks like.
/// </para>
/// <para>
/// <b>Run it from a scheduled task, not from a shell.</b> Focusing another application's window needs foreground
/// rights that a sandboxed or background process does not have, and every "no window took focus" line below is
/// that restriction rather than a finding:
/// </para>
/// <code>
/// schtasks /Create /TN Spike /TR "&lt;exe&gt; &lt;outdir&gt;" /SC ONCE /ST 23:59 /IT /F
/// schtasks /Run /TN Spike  &amp;&amp;  schtasks /Delete /TN Spike /F
/// </code>
/// </remarks>
internal static class Program
{
    private const int SampleInset = 14;

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "overlay-spike");
        Directory.CreateDirectory(outDir);

        // Second argument narrows the sweep to one application, because "test Edge only" is the question that
        // actually gets asked: a full sweep is twenty windows and ninety seconds, and buries the one application
        // under investigation in the middle of the report. Matched as a case-insensitive substring of the process
        // name, so "edge" finds msedge and "note" finds Notepad.
        var only = args.Length > 1 && args[1].Length > 0 ? args[1] : null;

        var report = new StringBuilder();
        void Say(string line)
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        // Composed by hand rather than by instantiating PasteJump.App.App, whose startup installs a global
        // keyboard hook and a clipboard listener - not things a spike should do to the machine.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(Load("Themes/Light.xaml"));
        app.Resources.MergedDictionaries.Add(Load("Themes/Controls.xaml"));
        app.Resources.MergedDictionaries.Add(Load("Themes/Shared.xaml"));

        var overlay = new OverlayWindow();
        overlay.ApplyKeyHint(true, 'V');
        overlay.Show();

        Say($"PasteJump overlay placement spike - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say($"this process: pid {Environment.ProcessId}, integrity depends on how it was launched");
        Say("");

        var modes = new[]
        {
            PopupPosition.Automatic,
            PopupPosition.CaretOrMouse,
            PopupPosition.MousePointer,
            PopupPosition.WindowCentre,
            PopupPosition.BottomRight,
        };

        var allWindows = Windows()
            .Where(w => w.Handle != new WindowInteropHelper(overlay).Handle)
            .ToList();

        var targets = only is null
            ? allWindows
            : allWindows.Where(w => w.Process.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();

        Say(only is null
            ? $"{targets.Count} candidate windows"
            : $"{targets.Count} of {allWindows.Count} candidate windows match \"{only}\"");

        // Named rather than left as a bare zero, because "0 candidate windows" reads as the spike being broken
        // when it usually means the filter was spelt for a window title instead of a process name.
        if (targets.Count == 0)
        {
            Say("   nothing matched. Processes with a visible window: "
                + string.Join(", ", allWindows.Select(w => w.Process).Distinct().Order()));
        }

        Say("");

        var invisible = 0;

        foreach (var target in targets)
        {
            Say($"== {target.Process}  {target.Title}");
            Say($"   window {Describe(target.Rect)} ex=0x{target.ExStyle:X8}"
                + $" fullScreen={IsFullScreen(target.Rect)} topmost={(target.ExStyle & 0x8) != 0}");

            if (!Focus(target.Handle))
            {
                Say("   SKIPPED: no foreground rights - run this from a scheduled task");
                Say("");
                continue;
            }

            foreach (var mode in modes)
            {
                var anchor = ForegroundWindowInfo.GetPreferredOverlayAnchor(mode);

                overlay.Render(Frame(target.Process, mode), anchor);
                Wait(180);

                var seen = OverlayIsOnScreen(overlay, out var matched, out var sampled);
                var verdict = seen ? "visible" : "HIDDEN";

                if (!seen)
                {
                    invisible++;
                    Capture(overlay, Path.Combine(outDir, $"hidden-{target.Process}-{mode}.png"));
                }

                Say($"   {mode,-13} anchor=({anchor.X},{anchor.Y}) {anchor.Placement,-20}"
                    + $" drawn=({overlay.Left:F0},{overlay.Top:F0}) {overlay.ActualWidth:F0}x{overlay.ActualHeight:F0}"
                    + $"  {verdict} ({matched}/{sampled} pixels match)");
            }

            Say("");
        }

        Say(invisible == 0
            ? "EVERY placement was visible in every application."
            : $"{invisible} placement(s) were NOT visible - see the hidden-*.png files in {outDir}");

        // Second half: does PasteJump's OWN gesture open in each application? Placement being right says nothing
        // about that, and it is the half a unit test cannot reach - it needs the real hook, in the real running
        // application, with a real keystroke. Escape cancels each session, so nothing is pasted into anybody's
        // documents.
        overlay.Hide();
        Say("");
        Say("== does the real PasteJump gesture open, per application? (Esc cancels each one)");

        var opened = 0;
        var refused = 0;

        // A control, and the gesture pass is worth nothing without one. This window belongs to this process, so
        // focusing it cannot fail for want of foreground rights - which makes it the one case that separates
        // "PasteJump refused the keystroke" from "the spike could not type at all". The first run of this pass
        // reported three failures and no successes, and that reads as damning about PasteJump when it was a
        // statement about the spike. Deliberately holds no text box: even a session that somehow committed has
        // nowhere to paste.
        var control = new Window
        {
            Title = "spike control window",
            Width = 560,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = "Control case for the gesture pass. Ctrl is held and the trigger tapped here first, "
                    + "so a failure over a real application can be told apart from a spike that cannot inject.",
                Margin = new Thickness(24),
                TextWrapping = TextWrapping.Wrap,
            },
        };

        // The witness is a keyboard HOOK, not the window, and that correction is worth keeping. The window was the
        // obvious instrument and it is the wrong one: a WPF window receives keys only while it is focused, and
        // focusing a window this process owns is exactly what the foreground lock refuses - the first run reported
        // "keys reaching it: NONE" purely because the control never came to the front, which proves nothing at all.
        // A WH_KEYBOARD_LL hook sees every key on the machine whatever has focus, which is the whole point of the
        // API and the reason PasteJump uses it. So:
        //
        //   our hook sees nothing  -> this process cannot inject. The run says NOTHING about PasteJump.
        //   our hook sees the keys -> injection works, and an overlay that never appeared is a real finding.
        var arrived = new List<string>();
        var witness = InstallWitnessHook(arrived);

        control.Show();
        Wait(200);

        var controlHandle = new WindowInteropHelper(control).Handle;
        var controlFocused = Focus(controlHandle, out var focusFailure);
        Wait(200);

        var controlWhere = string.Empty;
        var controlOpened = DriveGesture(out controlWhere);
        var sawKeys = arrived.Count > 0;

        Say($"   {"(this spike's own window)",-22} "
            + (controlOpened ? "gesture OPENED  " + controlWhere : "gesture did NOT open")
            + $"  [focused={controlFocused}"
            + (controlFocused ? string.Empty : " (" + focusFailure + ")")
            + $", our own hook saw: {(sawKeys ? string.Join(" ", arrived) : "NOTHING")}]");

        control.Close();
        Wait(150);

        foreach (var target in targets)
        {
            if (!Focus(target.Handle))
            {
                continue;
            }

            var sawIt = DriveGesture(out var where);

            if (sawIt)
            {
                opened++;
            }
            else
            {
                refused++;
            }

            Say($"   {target.Process,-22} {(sawIt ? "gesture OPENED  " + where : "gesture did NOT open")}");
        }

        Say("");
        Say($"gesture opened in {opened} application(s), did not open in {refused}");

        // The verdict is read off the CONTROL, not off the count of failures. Injected input can be refused
        // outright - by the session, by the desktop, by another hook earlier in the chain - and a run where
        // nothing opened anywhere says only that this process could not type.
        Say("");

        if (controlOpened && refused > 0)
        {
            Say($"  MEANINGFUL: the control opened, so injection works and the hook is alive. The {refused}");
            Say("  application(s) above that did not open the gesture are therefore a real finding.");
        }
        else if (controlOpened)
        {
            Say("  The gesture opened everywhere it was tried, the control included.");
        }
        else if (!sawKeys)
        {
            Say("  INCONCLUSIVE: this spike's OWN keyboard hook saw none of the keys it injected, so nothing was");
            Say("  injected at all and NOTHING above is a statement about PasteJump. On this machine a process");
            Say("  started by the Task Scheduler service is refused input injection, so run the spike as");
            Say("  yourself - double-click the exe - whenever the gesture half is the half you want answered.");
        }
        else
        {
            Say($"  PasteJump did NOT REACT: injection works (our own hook saw {string.Join(" ", arrived)}) and no");
            Say("  overlay appeared in any application above. PasteJump is not running, is disabled, or has lost");
            Say("  its keyboard hook - none of which the placement half can tell you, since that draws the");
            Say("  overlay itself rather than asking PasteJump to.");
        }

        if (witness != IntPtr.Zero)
        {
            UnhookWindowsHookEx(witness);
        }

        File.WriteAllText(Path.Combine(outDir, "spike-report.txt"), report.ToString());
        Console.WriteLine($"\nreport written to {Path.Combine(outDir, "spike-report.txt")}");

        overlay.Close();
        return invisible == 0 ? 0 : 2;
    }

    /// <summary>
    /// Holds Ctrl, taps the trigger, and watches for PasteJump's own overlay window to appear.
    /// </summary>
    /// <remarks>
    /// The half that cannot be unit tested: it needs the real low-level hook in the real running application,
    /// reacting to a real keystroke. Escape is sent before releasing Ctrl so the session is cancelled rather than
    /// committed - the clipboard is restored and nothing is pasted into whatever window happens to be focused,
    /// which matters when the sweep walks somebody's open documents.
    /// <para>
    /// A negative result here means nothing unless a positive one appears somewhere in the same run: injection can
    /// be refused outright, and "no application opened the gesture" would then mean the spike could not type,
    /// rather than that PasteJump is broken.
    /// </para>
    /// </remarks>
    private static bool DriveGesture(out string where)
    {
        where = string.Empty;

        Send(0x11, false);
        Wait(120);
        Send(0x56, false);
        Wait(60);
        Send(0x56, true);

        var seen = false;

        for (var attempt = 0; attempt < 12 && !seen; attempt++)
        {
            Wait(60);

            var pasteJumpOverlay = FindByTitle("PasteJump Overlay");

            if (pasteJumpOverlay != IntPtr.Zero && IsWindowVisible(pasteJumpOverlay))
            {
                GetWindowRect(pasteJumpOverlay, out var r);
                where = $"at ({r.Left},{r.Top})-({r.Right},{r.Bottom})";
                seen = true;
            }
        }

        Send(0x1B, false);
        Wait(40);
        Send(0x1B, true);
        Wait(40);
        Send(0x11, true);
        Wait(250);

        return seen;
    }

    private static IntPtr FindByTitle(string title)
    {
        var found = IntPtr.Zero;

        EnumWindows(
            (handle, _) =>
            {
                var text = new StringBuilder(64);
                GetWindowTextW(handle, text, text.Capacity);

                if (text.ToString() == title)
                {
                    found = handle;
                    return false;
                }

                return true;
            },
            IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// Installs this spike's own low-level keyboard hook, recording every key it sees.
    /// </summary>
    /// <remarks>
    /// It exists to answer one question - did our SendInput actually reach the input stream - and it answers it
    /// without needing focus, which is what the window it replaced could not do. Presence is conclusive and
    /// absence is not quite: a hook earlier in the chain can suppress an event before it reaches ours, so "we saw
    /// the keys" proves injection works while "we saw nothing" means only that nothing arrived HERE. In practice
    /// nothing suppresses a key nobody has claimed, and PasteJump only ever suppresses the trigger.
    /// <para>
    /// The delegate is held in a static field deliberately: the CLR would otherwise collect it while Windows still
    /// holds the pointer, and the process would die inside the first callback.
    /// </para>
    /// </remarks>
    private static IntPtr InstallWitnessHook(List<string> seen)
    {
        _witnessCallback = (code, wParam, lParam) =>
        {
            if (code >= 0 && (wParam == 0x100 || wParam == 0x104))
            {
                var virtualKey = Marshal.ReadInt32(lParam);

                seen.Add(virtualKey switch
                {
                    0x11 or 0xA2 or 0xA3 => "Ctrl",
                    0x56 => "V",
                    0x1B => "Esc",
                    _ => $"vk{virtualKey:X2}",
                });
            }

            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };

        return SetWindowsHookEx(13, _witnessCallback, IntPtr.Zero, 0);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private static HookProc? _witnessCallback;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    private static void Send(ushort virtualKey, bool up)
    {
        var input = new INPUT
        {
            type = 1,
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = (ushort)MapVirtualKey(virtualKey, 0),
                dwFlags = up ? 2u : 0u,
            },
        };

        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
        public int pad1;
        public int pad2;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    private static PasteOverlayModel Frame(string process, PopupPosition mode) => new()
    {
        Position = 1,
        Total = 9,
        Kind = ClipKind.Text,
        PreviewText = $"spike: {process} / {mode}",
        Pinned = false,
        FormatterName = "Original",
        CommitMode = PasteCommitMode.Paste,
        IsSearching = false,
        MatchCount = 9,
        PopOnPaste = false,
        IsEmpty = false,
    };

    /// <summary>
    /// Whether the overlay is really on the screen where it says it is, by photographing that rectangle and
    /// comparing it against the overlay's own rendering.
    /// </summary>
    /// <remarks>
    /// The only honest witness. A z-order walk reports the overlay on top while the Start menu covers it - measured
    /// - and the window's own Left/Top say nothing about what the compositor put in front. Samples the interior
    /// only: DWM draws the rounded corners and the drop shadow, so the edges legitimately differ.
    /// </remarks>
    private static bool OverlayIsOnScreen(Window overlay, out int matched, out int sampled)
    {
        matched = 0;
        sampled = 0;

        var scale = VisualTreeHelper.GetDpi(overlay).DpiScaleX;

        var width = (int)(overlay.ActualWidth * scale);
        var height = (int)(overlay.ActualHeight * scale);

        if (width <= SampleInset * 2 || height <= SampleInset * 2)
        {
            return false;
        }

        var rendered = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rendered.Render(overlay);

        var expected = ToArgb(rendered, width, height);

        using var shot = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(shot))
        {
            g.CopyFromScreen(
                (int)(overlay.Left * scale),
                (int)(overlay.Top * scale),
                0,
                0,
                new System.Drawing.Size(width, height));
        }

        for (var x = SampleInset; x < width - SampleInset; x += 7)
        {
            for (var y = SampleInset; y < height - SampleInset; y += 7)
            {
                sampled++;

                var want = expected[(y * width) + x];
                var got = shot.GetPixel(x, y);

                if (Math.Abs(((want >> 16) & 0xFF) - got.R) <= 24
                    && Math.Abs(((want >> 8) & 0xFF) - got.G) <= 24
                    && Math.Abs((want & 0xFF) - got.B) <= 24)
                {
                    matched++;
                }
            }
        }

        // Sixty per cent, and the number is measured rather than chosen. A plainly visible overlay scored 475 of
        // 531 sampled pixels - 89% - because a clip preview renders antialiased text over whatever backdrop the
        // screenshot caught, and DWM rounds the corners and draws a shadow. A genuinely covered overlay scores a
        // small fraction of that, so the gap is wide and the threshold sits in the middle of it rather than at the
        // top. The first run used 0.9 and reported every visible overlay as hidden.
        return sampled > 0 && matched >= sampled * 0.6;
    }

    private static int[] ToArgb(RenderTargetBitmap bitmap, int width, int height)
    {
        var pixels = new int[width * height];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static void Capture(Window overlay, string path)
    {
        var scale = VisualTreeHelper.GetDpi(overlay).DpiScaleX;
        var width = Math.Max(1, (int)(overlay.ActualWidth * scale));
        var height = Math.Max(1, (int)(overlay.ActualHeight * scale));

        using var shot = new System.Drawing.Bitmap(width, height);
        using var g = System.Drawing.Graphics.FromImage(shot);

        g.CopyFromScreen((int)(overlay.Left * scale), (int)(overlay.Top * scale), 0, 0, new System.Drawing.Size(width, height));
        shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>Drains the dispatcher so a Render actually reaches the screen before it is photographed.</summary>
    private static void Wait(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);

        while (DateTime.UtcNow < until)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { },
                System.Windows.Threading.DispatcherPriority.Background);

            Thread.Sleep(15);
        }
    }

    private static bool IsFullScreen((int L, int T, int R, int B) rect)
    {
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var bounds = screen.Bounds;

            // Covers the monitor including whatever the taskbar occupies, which a maximised window does not.
            if (rect.L <= bounds.Left && rect.T <= bounds.Top
                && rect.R >= bounds.Right && rect.B >= bounds.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Brings a window to the front, attaching to the foreground thread first.
    /// </summary>
    /// <remarks>
    /// SetForegroundWindow alone only works for the process that already owns the foreground, so a bare loop
    /// focuses the first window and is refused for every one after it - which is exactly what the first run of this
    /// spike reported, and it read as a permissions problem rather than as the foreground lock. Attaching our input
    /// queue to the current foreground thread lifts the restriction; PasteJump does the same in
    /// WindowInterop.BringToFrontAndFocus, where it was measured succeeding four times out of four against
    /// Activate()'s three.
    /// <para>
    /// Detached in a finally: two input queues left attached share keyboard state indefinitely, in both processes.
    /// </para>
    /// </remarks>
    private static bool Focus(IntPtr window) => Focus(window, out _);

    /// <summary>
    /// Brings a window to the front, reporting what held the foreground when it could not.
    /// </summary>
    /// <remarks>
    /// The reason is returned rather than merely logged because "no foreground rights" is the wrong conclusion
    /// most of the time: naming the window that actually held the foreground is what separates the foreground
    /// lock from a handle that was never realised, and a run of this spike is expensive to repeat.
    /// </remarks>
    private static bool Focus(IntPtr window, out string failure)
    {
        failure = string.Empty;

        if (window == IntPtr.Zero)
        {
            failure = "no window handle";
            return false;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var foreground = GetForegroundWindow();

            if (foreground == window)
            {
                return true;
            }

            var theirs = GetWindowThreadProcessId(foreground, out _);
            var ours = GetCurrentThreadId();
            var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);

            try
            {
                SetForegroundWindow(window);
                BringWindowToTop(window);
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(ours, theirs, false);
                }
            }

            Wait(300);

            if (GetForegroundWindow() == window)
            {
                return true;
            }
        }

        var held = GetForegroundWindow();
        failure = held == IntPtr.Zero
            ? "nothing holds the foreground - no interactive input desktop"
            : $"the foreground is held by {ProcessOf(held)} 0x{held:X}";

        return false;
    }

    private static string Describe((int L, int T, int R, int B) r)
        => $"({r.L},{r.T})-({r.R},{r.B}) {r.R - r.L}x{r.B - r.T}";

    private static ResourceDictionary Load(string relative) => new()
    {
        Source = new Uri($"pack://application:,,,/PasteJump;component/{relative}", UriKind.Absolute),
    };

    private static string ProcessOf(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var pid);

        try
        {
            return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch (ArgumentException)
        {
            return "an exited process";
        }
    }

    private static List<(IntPtr Handle, string Process, string Title, (int L, int T, int R, int B) Rect, uint ExStyle)> Windows()
    {
        var found = new List<(IntPtr, string, string, (int, int, int, int), uint)>();

        EnumWindows(
            (handle, _) =>
            {
                if (!IsWindowVisible(handle))
                {
                    return true;
                }

                GetWindowRect(handle, out var rect);

                if (rect.Right - rect.Left < 500 || rect.Bottom - rect.Top < 300)
                {
                    return true;
                }

                GetWindowThreadProcessId(handle, out var pid);

                var name = "?";

                try
                {
                    name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                }
                catch (ArgumentException)
                {
                }

                if (name is "PasteJump.OverlaySpike" or "PasteJump")
                {
                    return true;
                }

                var title = new StringBuilder(120);
                GetWindowTextW(handle, title, title.Capacity);

                found.Add((
                    handle,
                    name,
                    Trim(title.ToString()),
                    (rect.Left, rect.Top, rect.Right, rect.Bottom),
                    (uint)GetWindowLong(handle, -20)));

                return true;
            },
            IntPtr.Zero);

        return found
            .Select(f => (f.Item1, f.Item2, f.Item3, f.Item4, f.Item5))
            .ToList();
    }

    private static string Trim(string text) => text.Length <= 54 ? text : text[..51] + "...";

    private delegate bool EnumProc(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out RECT rect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr handle, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
