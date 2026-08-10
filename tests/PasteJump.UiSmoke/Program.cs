// System.IO explicitly: the WPF implicit-usings set omits it, unlike the plain library set.
using System.IO;
using System.Text;
using System.Windows;
using PasteJump.App.Views;
using PasteJump.Core;
using PasteJump.Core.Abstractions;
using PasteJump.Core.Capture;
using PasteJump.Core.Formatting;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;
using PasteJump.Core.Storage;

namespace PasteJump.UiSmoke;

/// <summary>
/// Shows every window in both themes and reports failures. Exit code 0 when all opened cleanly.
/// <para>
/// The value is in <em>showing</em> rather than constructing. Constructing a Window parses its XAML,
/// but a control template is only applied - and only able to throw - once the control is measured and
/// arranged. A template with a bad property or a missing resource compiles and builds without
/// complaint, so nothing before this point would notice.
/// </para>
/// </summary>
internal static class Program
{
    private static int _failures;

    private static string? _shotDirectory;

    /// <param name="args">
    /// <c>--shot &lt;dir&gt;</c> also renders each window to a PNG. Useful for eyeballing the themes
    /// without opening the app, and for reviewing a UI change without having to describe it.
    /// </param>
    [STAThread]
    private static int Main(string[] args)
    {
        var shotIndex = Array.IndexOf(args, "--shot");

        if (shotIndex >= 0 && shotIndex + 1 < args.Length)
        {
            _shotDirectory = args[shotIndex + 1];
            Directory.CreateDirectory(_shotDirectory);
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // Mirrors App.xaml: palette in slot 0, then the theme-independent dictionaries. Composed here
        // rather than by instantiating PasteJump.App.App, whose OnStartup installs a global keyboard
        // hook and a clipboard listener - not things a test should do to the machine.
        app.Resources.MergedDictionaries.Add(Load("Themes/Light.xaml"));
        app.Resources.MergedDictionaries.Add(Load("Themes/Controls.xaml"));
        app.Resources.MergedDictionaries.Add(Load("Themes/Shared.xaml"));

        var root = Path.Combine(Path.GetTempPath(), "pastejump-uismoke", Guid.NewGuid().ToString("n"));
        var paths = AppPaths.At(root);
        paths.EnsureCreated();

        using (var store = new ClipStore(paths))
        {
            Seed(store);

            var formatters = new FormatterRegistry();
            // Deliberately not all defaults. The Advanced page's "changed from default" banding, its accented
            // value and its per-row Reset button only appear on a modified row, so a pristine settings object
            // would leave all three unrendered - and the reset hook below would never reach the reflection
            // branch that writes a default back into a property.
            var settings = new PasteJumpSettings
            {
                MaxClips = 500,
                Theme = AppTheme.Dark,
                OverlayPreviewMaxWidth = 800,
                BeepOnCopy = true,
                IgnoredProcesses = ["keepass.exe", "1password.exe"],
            };

            foreach (var theme in new[] { "Light", "Dark" })
            {
                Console.WriteLine($"===== {theme} =====");
                app.Resources.MergedDictionaries[0] = Load($"Themes/{theme}.xaml");
                _theme = theme;

                Check("HistoryWindow", () =>
                {
                    var window = new HistoryWindow(store, new NullClipboard(), new SelfWriteGuard(), formatters);

                    // Row 0 is the seeded image FILE, so the screenshot shows the shape with the most going on:
                    // path above, thumbnail below, dimensions and size in the footer.
                    window.Loaded += (_, _) => window.SelectRowForSmokeTest(0);
                    return window;
                });

                // The same window showing the clip stack instead. A separate shot because the two views differ
                // in their columns, their buttons and their status line, so one proves nothing about the other.
                Check("HistoryWindow-Clips", () =>
                {
                    var window = new HistoryWindow(store, new NullClipboard(), new SelfWriteGuard(), formatters);

                    window.Loaded += (_, _) =>
                    {
                        window.ShowClipsForSmokeTest();
                        window.SelectRowForSmokeTest(0);
                    };

                    return window;
                });

                // Cycles every tab. TabControl only realises the SELECTED tab's content, so without
                // this the other tabs' templates are never instantiated and a broken one goes unseen -
                // which is exactly the class of failure this harness exists to catch.
                Check(
                    "SettingsWindow",
                    () => new SettingsWindow(settings, formatters, DataLocation.UserProfile),
                    (window, name) =>
                    {
                        CycleTabs(window, name);

                        // Then the Advanced page's Reset buttons, which write back into every other tab's
                        // controls by reflection. Nothing is saved - the dialog is closed, never accepted -
                        // but a key that no longer names a property, or a control missing from the reload,
                        // throws here rather than in front of the user.
                        ((SettingsWindow)window).ExerciseResetsForSmokeTest();
                        Drain();
                    });
                // The same dialog with both halves on a custom folder, which is the only way the path box and
                // its Browse button are realised at all - a collapsed row's template is never applied, so the
                // ordinary case above proves nothing about it.
                Check(
                    "SettingsWindow-CustomFolder",
                    () => new SettingsWindow(
                        settings,
                        formatters,
                        DataLocation.CustomFolder,
                        DataLocation.CustomFolder,
                        @"D:\PasteJumpData",
                        @"D:\PasteJumpData"),
                    CycleTabs);

                Check("ShortcutHelpWindow", () => new ShortcutHelpWindow());
                Check("AboutWindow", () => new AboutWindow());

                // MessageDialog is normally shown modally, which would block this harness for ever - so it is
                // constructed through a test hook and shown non-modally instead. Worth including: it is the
                // one window whose content is built entirely in code, so a broken template here would not
                // show up until the app had something to tell the user.
                Check("MessageDialog", () => MessageDialog.CreateForSmokeTest(
                    @"An existing Clipjump installation was found at:" + "\n\n"
                    + @"D:\Lokesh\DoNotMove\Clipjump_x64" + "\n\n"
                    + "Both the history archive and the clip stack are imported, and nothing in the Clipjump "
                    + "folder is modified.\n\nYou can also do this later from Settings, History.",
                    "Import from Clipjump?",
                    DialogKind.Question));
                // Seeded with a folder that does not exist, so the invalid branch of the validation is the one
                // rendered - that is the state with the extra status line and the disabled Import button.
                Check("ImportDialog", () => new ImportDialog(@"D:\Lokesh\DoNotMove\Clipjump_x64"));

                // Enumerates the machine's real windowed processes, which is fine for a harness - it only
                // reads. Constructed through a test hook because Choose is modal.
                Check("RunningAppPicker", RunningAppPicker.CreateForSmokeTest);

                // Constructed through a test hook rather than Run, which is modal and drives a worker to
                // completion - neither of which a smoke harness can wait on.
                Check("ImportProgressDialog", () => ImportProgressDialog.CreateForSmokeTest(1_240, 3_400));

                Check("TagEditorWindow", () => new TagEditorWindow(["alpha", "beta"]));

                Check("ToastWindow", () =>
                {
                    var toast = new ToastWindow();
                    toast.Notify("Copied - 3 clips", "some preview text", TimeSpan.FromSeconds(30));
                    return toast;
                });

                // The other shape the toast takes: a message about the application rather than about a clip,
                // which is what a second launch answers with. Worth its own case because it exercises the two
                // things that differ - corner placement and a proportional detail line. The monospace default
                // is right for clip text and reads as a code listing for a sentence.
                Check("ToastWindow-Message", () =>
                {
                    var toast = new ToastWindow();

                    toast.Notify(
                        "PasteJump is already running",
                        "Hold Ctrl and tap V to paste. The icon is in the notification area, by the clock.",
                        TimeSpan.FromSeconds(30),
                        ToastPlacement.BottomRight,
                        detailIsProse: true);

                    return toast;
                });

                // Three real frames rather than one empty window. The empty overlay renders none of
                // RenderBody's interesting paths - no preview, no chips, no banner - so it proved almost
                // nothing, and it made a useless picture for the help file. These are also the shots
                // docs/help uses, via tools/update-help-images.ps1.
                Check("OverlayWindow", () => RenderOverlay(TextFrame()));
                Check("OverlayWindow-Search", () => RenderOverlay(SearchFrame()));
                Check("OverlayWindow-DeleteAll", () => RenderOverlay(DeleteAllFrame()));
            }
        }

        TryDelete(root);

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "RESULT: all windows opened cleanly"
            : $"RESULT: {_failures} FAILURE(S)");

        return _failures == 0 ? 0 : 2;
    }

    /// <summary>
    /// Shows the overlay and renders one frame into it.
    /// <para>
    /// Rendered on Loaded rather than before Show, because Render measures itself and positions the window,
    /// and both need a live HWND. The anchor is a fixed point rather than the caret: there is no caret in a
    /// harness, and a repeatable position is what makes the screenshots comparable between runs.
    /// </para>
    /// </summary>
    private static OverlayWindow RenderOverlay(PasteOverlayModel frame)
    {
        var overlay = new OverlayWindow();

        overlay.Loaded += (_, _) =>
        {
            // On, as it is by default, so the help screenshots show what a user sees.
            overlay.ApplyKeyHint(show: true, triggerKey: 'V');
            overlay.Render(frame, anchorX: 200, anchorY: 200);
        };

        return overlay;
    }

    /// <summary>A text clip mid-gesture, with tags, a source application and a pop pending.</summary>
    private static PasteOverlayModel TextFrame() => new()
    {
        Position = 3,
        Total = 41,
        PreviewText =
            "SELECT c.id, c.preview, c.total_bytes" + "\n" +
            "  FROM clip c" + "\n" +
            " WHERE c.pinned = 0" + "\n" +
            " ORDER BY c.sort_key DESC;",
        Kind = ClipKind.Text,
        Pinned = true,
        Tags = ["sql", "reporting"],
        FormatterName = "Original",
        CommitMode = PasteCommitMode.Paste,
        IsSearching = false,
        MatchCount = 0,
        PopOnPaste = true,
        IsEmpty = false,
        SourceExecutable = "devenv.exe",
    };

    /// <summary>The same gesture in search mode, which adds the query row above the preview.</summary>
    private static PasteOverlayModel SearchFrame() => new()
    {
        Position = 1,
        Total = 41,
        PreviewText = "https://github.com/lokeshgovindu/PasteJump",
        Kind = ClipKind.Text,
        Pinned = false,
        FormatterName = "Plain text",
        CommitMode = PasteCommitMode.Paste,
        IsSearching = true,
        SearchQuery = "github",
        MatchCount = 4,
        PopOnPaste = false,
        IsEmpty = false,
        SourceExecutable = "chrome.exe",
    };

    /// <summary>The X cycle at its last stop, which is the one worth showing a picture of.</summary>
    private static PasteOverlayModel DeleteAllFrame() => new()
    {
        Position = 3,
        Total = 41,
        PreviewText = "the clip that will NOT be pasted, because X was tapped three times",
        Kind = ClipKind.Text,
        Pinned = false,
        FormatterName = "Original",
        CommitMode = PasteCommitMode.DeleteAll,
        IsSearching = false,
        MatchCount = 0,
        PopOnPaste = false,
        IsEmpty = false,
        SourceExecutable = "notepad.exe",
    };

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    /// <summary>
    /// Checks that a window gets a taskbar button, or deliberately does not, and fails the run when it has the
    /// wrong one.
    /// <para>
    /// Every window must be reachable from the taskbar so it cannot be lost behind another with no way back -
    /// which matters more here than in most applications, because PasteJump has no main window to return to.
    /// The overlay and the toast are the only exceptions: both live for seconds and neither is something anyone
    /// tracks. This was a real defect - About, and every dialog, carried <c>ShowInTaskbar="False"</c>.
    /// </para>
    /// <para>
    /// Read from the live HWND rather than from <c>Window.ShowInTaskbar</c>, which would only prove the
    /// property was set. Measured behaviour: a taskbar window has <c>WS_EX_APPWINDOW</c> and not
    /// <c>WS_EX_TOOLWINDOW</c> (<c>ex=0x00040100</c>); the excluded two are the other way round
    /// (<c>ex=0x080000A8</c>).
    /// </para>
    /// </summary>
    private static string DescribeTaskbarPresence(string name, Window window)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            _failures++;
            return "FAIL: no hwnd to check";
        }

        var exStyle = (long)GetWindowLongPtr(handle, GWL_EXSTYLE);

        var inTaskbar = (exStyle & WS_EX_APPWINDOW) != 0 && (exStyle & WS_EX_TOOLWINDOW) == 0;

        // The transient pair, by name prefix - the overlay has three frames and the toast two shapes.
        var shouldBeExcluded = name.StartsWith("OverlayWindow", StringComparison.Ordinal)
            || name.StartsWith("ToastWindow", StringComparison.Ordinal);

        if (inTaskbar == !shouldBeExcluded)
        {
            return inTaskbar ? "taskbar" : "no taskbar (by design)";
        }

        _failures++;

        return shouldBeExcluded
            ? $"FAIL: should be kept OUT of the taskbar, ex=0x{exStyle:X8}"
            : $"FAIL: should appear in the taskbar, ex=0x{exStyle:X8}";
    }

    private static ResourceDictionary Load(string relative) => new()
    {
        Source = new Uri($"pack://application:,,,/PasteJump;component/{relative}", UriKind.Absolute),
    };

    private static string _theme = "Light";

    /// <summary>
    /// Selects each tab in turn so every tab's content is realised and, if screenshots are on, captured.
    /// </summary>
    private static void CycleTabs(Window window, string name)
    {
        var tabs = FindDescendant<System.Windows.Controls.TabControl>(window);

        if (tabs is null)
        {
            return;
        }

        for (var i = 0; i < tabs.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            window.UpdateLayout();
            Drain();

            var header = (tabs.Items[i] as System.Windows.Controls.TabItem)?.Header?.ToString()
                ?.Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                ?? i.ToString();

            if (_shotDirectory is not null)
            {
                Capture(window, Path.Combine(_shotDirectory, $"{_theme}-{name}-{i}-{header}.png"));
            }
        }

        tabs.SelectedIndex = 0;
        Drain();
    }

    private static T? FindDescendant<T>(System.Windows.Media.Visual root)
        where T : System.Windows.Media.Visual
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(root, i) is not System.Windows.Media.Visual child)
            {
                continue;
            }

            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static void Check(string name, Func<Window> factory, Action<Window, string>? afterShow = null)
    {
        try
        {
            var window = factory();

            // ToastWindow shows itself from Notify.
            if (!window.IsVisible)
            {
                window.Show();
            }

            window.UpdateLayout();

            // Let the dispatcher run so Loaded handlers, data binding and layout all settle. Without
            // this the screenshot catches a half-arranged window.
            Drain();

            var size = $"{window.ActualWidth:0}x{window.ActualHeight:0}";
            var taskbar = DescribeTaskbarPresence(name, window);

            if (_shotDirectory is not null)
            {
                Capture(window, Path.Combine(_shotDirectory, $"{_theme}-{name}.png"));
            }

            afterShow?.Invoke(window, name);

            window.Close();

            Console.WriteLine($"  ok    {name,-20} {size,-10} {taskbar}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"  FAIL  {name,-20} {ex.GetType().Name}: {ex.Message}");

            if (ex.InnerException is { } inner)
            {
                Console.WriteLine($"        inner: {inner.GetType().Name}: {inner.Message}");
            }
        }
    }

    /// <summary>Runs queued dispatcher work to completion, so layout and bindings have settled.</summary>
    private static void Drain()
    {
        for (var i = 0; i < 4; i++)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();

            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));

            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>
    /// Renders a window to a PNG.
    /// <para>
    /// The window's own Background is painted first: <c>RenderTargetBitmap</c> captures the visual
    /// tree, not the desktop, and a Window whose Background is a brush on the Window itself renders
    /// onto transparency otherwise - which makes a light theme look like a dark one.
    /// </para>
    /// </summary>
    private static void Capture(Window window, string path)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);

        var visual = new System.Windows.Media.DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            var background = window.Background ?? System.Windows.Media.Brushes.White;
            dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(window) { Stretch = System.Windows.Media.Stretch.None },
                null,
                new Rect(0, 0, width, height));
        }

        target.Render(visual);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>
    /// Enough rows that the grid builds headers, cells and a selected row. An empty grid exercises
    /// almost none of the templates that matter.
    /// </summary>
    private static void Seed(ClipStore store)
    {
        for (var i = 1; i <= 5; i++)
        {
            var text = i == 3
                ? "a multi-line clip\r\nsecond line\r\nthird line"
                : $"clip number {i} with some text to fill the column";

            var payload = new ClipPayload(13, null, Encoding.Unicode.GetBytes(text));
            store.Add(new ClipboardSnapshot([payload], text, ClipKind.Text, "devenv.exe"), allowDuplicates: true);
            store.AddHistory(DateTimeOffset.UtcNow.AddMinutes(-i), ClipKind.Text, text, null, text.Length * 2);
        }

        // An image row too, so the Kind column and the image preview branch are both reached.
        store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Image, "[image]", null, 1_234_567);

        // A copied image FILE, which is a third preview shape again: the path stays visible and a thumbnail of
        // it appears underneath. Written as a real file on disk because that branch decodes from disk - seeding
        // a path that does not exist would exercise nothing and look identical to the feature being broken.
        var imagePath = Path.Combine(Path.GetTempPath(), "pastejump-smoke-preview.png");
        File.WriteAllBytes(imagePath, SamplePng());

        store.AddHistory(
            DateTimeOffset.UtcNow.AddSeconds(1),
            ClipKind.Files,
            FileListPreview.Describe([imagePath]),
            null,
            new FileInfo(imagePath).Length);
    }

    /// <summary>
    /// A PNG built by hand, so the harness needs no image on the machine and no System.Drawing reference.
    /// <para>
    /// Deliberately larger than the pane: it was 2x2 at first, and the screenshot showed a single dot -
    /// correct, since the preview declines to upscale, but useless for judging the layout. A real photograph
    /// is downscaled, so the test image has to be too if the shot is to represent anything.
    /// </para>
    /// </summary>
    private static byte[] SamplePng()
    {
        const int Width = 480;
        const int Height = 300;

        using var stream = new MemoryStream();

        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BitConverter.GetBytes(Width).Reverse().ToArray().CopyTo(header, 0);
        BitConverter.GetBytes(Height).Reverse().ToArray().CopyTo(header, 4);
        header[8] = 8;      // bit depth
        header[9] = 2;      // truecolour
        WriteChunk(stream, "IHDR", header);

        // A diagonal gradient, so the thumbnail is obviously an image rather than a flat block.
        var raw = new byte[Height * ((Width * 3) + 1)];
        var at = 0;

        for (var y = 0; y < Height; y++)
        {
            raw[at++] = 0;  // filter: none

            for (var x = 0; x < Width; x++)
            {
                raw[at++] = (byte)(x * 255 / Width);
                raw[at++] = (byte)(y * 255 / Height);
                raw[at++] = 0xF0;
            }
        }

        using var deflated = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(
            deflated, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(stream, "IDAT", deflated.ToArray());
        WriteChunk(stream, "IEND", []);

        return stream.ToArray();

        static void WriteChunk(Stream target, string type, byte[] data)
        {
            var length = BitConverter.GetBytes(data.Length);
            Array.Reverse(length);
            target.Write(length);

            var typeAndData = new byte[4 + data.Length];
            Encoding.ASCII.GetBytes(type).CopyTo(typeAndData, 0);
            data.CopyTo(typeAndData, 4);
            target.Write(typeAndData);

            var crc = BitConverter.GetBytes(Crc32(typeAndData));
            Array.Reverse(crc);
            target.Write(crc);
        }

        static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;

            foreach (var b in bytes)
            {
                crc ^= b;

                for (var i = 0; i < 8; i++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }

            return crc ^ 0xFFFFFFFFu;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Never touches the real clipboard: this harness must not disturb the machine.</summary>
    private sealed class NullClipboard : IClipboardAccess
    {
        public uint SequenceNumber => 1;

        public ClipboardSnapshot? TryRead() => null;

        public bool TryWrite(IReadOnlyList<ClipPayload> payloads) => true;
    }
}
