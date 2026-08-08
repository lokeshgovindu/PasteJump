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
            var settings = new PasteJumpSettings();

            foreach (var theme in new[] { "Light", "Dark" })
            {
                Console.WriteLine($"===== {theme} =====");
                app.Resources.MergedDictionaries[0] = Load($"Themes/{theme}.xaml");
                _theme = theme;

                Check("HistoryWindow", () =>
                {
                    var window = new HistoryWindow(store, new NullClipboard(), new SelfWriteGuard(), formatters);

                    // Select a row that is not the first, so the screenshot shows selection against
                    // both a normal and an alternating row.
                    window.Loaded += (_, _) => window.SelectRowForSmokeTest(2);
                    return window;
                });

                // Cycles every tab. TabControl only realises the SELECTED tab's content, so without
                // this the other tabs' templates are never instantiated and a broken one goes unseen -
                // which is exactly the class of failure this harness exists to catch.
                Check("SettingsWindow", () => new SettingsWindow(settings, formatters, DataLocation.UserProfile), CycleTabs);
                Check("ShortcutHelpWindow", () => new ShortcutHelpWindow());
                Check("AboutWindow", () => new AboutWindow());

                // MessageDialog is normally shown modally, which would block this harness for ever - so it is
                // constructed through a test hook and shown non-modally instead. Worth including: it is the
                // one window whose content is built entirely in code, so a broken template here would not
                // show up until the app had something to tell the user.
                Check("MessageDialog", () => MessageDialog.CreateForSmokeTest(
                    @"An existing Clipjump installation was found at:" + "\n\n"
                    + @"D:\Lokesh\DoNotMove\Clipjump_x64" + "\n\n"
                    + "Only history is imported. Clip stacks are left alone, and nothing in the Clipjump "
                    + "folder is modified.\n\nYou can also do this later from Settings, History.",
                    "Import Clipjump's history?",
                    DialogKind.Question));
                Check("TagEditorWindow", () => new TagEditorWindow(["alpha", "beta"]));

                Check("ToastWindow", () =>
                {
                    var toast = new ToastWindow();
                    toast.Notify("Copied - 3 clips", "some preview text", TimeSpan.FromSeconds(30));
                    return toast;
                });

                Check("OverlayWindow", () => new OverlayWindow());
            }
        }

        TryDelete(root);

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? "RESULT: all windows opened cleanly"
            : $"RESULT: {_failures} FAILURE(S)");

        return _failures == 0 ? 0 : 2;
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

            if (_shotDirectory is not null)
            {
                Capture(window, Path.Combine(_shotDirectory, $"{_theme}-{name}.png"));
            }

            afterShow?.Invoke(window, name);

            window.Close();

            Console.WriteLine($"  ok    {name,-20} {size}");
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
