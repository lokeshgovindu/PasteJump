// System.IO explicitly: the WPF implicit-usings set omits it, unlike the plain library set.
using System.IO;
using System.Text;
using System.Windows;
using PasteJump.App.Services;
using PasteJump.App.Views;
using PasteJump.Core;
using PasteJump.Core.Abstractions;
using PasteJump.Core.Capture;
using PasteJump.Core.Formatting;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;
using PasteJump.Core.Storage;
using PasteJump.Interop;

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
                Check(
                    "HistoryWindow-Clips",
                    () =>
                    {
                        var window = new HistoryWindow(store, new NullClipboard(), new SelfWriteGuard(), formatters);

                        window.Loaded += (_, _) =>
                        {
                            window.ShowClipsForSmokeTest();
                            window.SelectRowForSmokeTest(0);
                        };

                        return window;
                    },
                    static (window, _) => VerifyToolTipWrapping(window));

                // Several rows selected, then joined. Two shots because they are two different states and each
                // is only reachable here: the Copy button relabels itself to "Copy Joined" with more than one row
                // selected, and the status line a join produces is written nowhere else. They cannot be one shot,
                // because the reload after a join drops the multi-selection and the label follows it back.
                Check(
                    "HistoryWindow-Joining",
                    () =>
                    {
                        var window = new HistoryWindow(store, new NullClipboard(), new SelfWriteGuard(), formatters);

                        // After Loaded, so there are rows to select - Refresh runs there.
                        window.Loaded += (_, _) => window.SelectFirstRowsForSmokeTest(3);

                        return window;
                    },
                    (window, name) =>
                    {
                        // Its own shot, taken after the join, because Check's is taken before this callback runs.
                        ((HistoryWindow)window).JoinSelectionForSmokeTest();
                        window.UpdateLayout();
                        Drain();

                        if (_shotDirectory is not null)
                        {
                            Capture(window, Path.Combine(_shotDirectory, $"{_theme}-HistoryWindow-Joined.png"));
                        }
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

                        VerifySearchIndex((SettingsWindow)window);
                    });
                // The dialog with a query in the search box. Its own case because the clear cross and the match
                // count only exist once there is text, so the empty dialog above proves nothing about either.
                Check(
                    "SettingsWindow-Searching",
                    () => new SettingsWindow(settings, formatters, DataLocation.UserProfile),
                    (window, name) =>
                    {
                        ((SettingsWindow)window).TypeInSearchForSmokeTest("theme");
                        window.UpdateLayout();
                        Drain();

                        // Captured here rather than relying on Check's own shot: that one is taken before this
                        // callback runs, so it would show an empty box - which is exactly what this case exists to
                        // avoid. Same reason CycleTabs saves its own per-tab shots.
                        if (_shotDirectory is not null)
                        {
                            Capture(window, Path.Combine(_shotDirectory, $"{_theme}-{name}.png"));
                        }
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

                // A no-op manual action rather than null, so the shot shows the window the way a release build
                // shows it. The button is hidden when there is no .chm to open, and there never is one beside
                // this harness - the manual is compiled separately by tools/build-help.ps1.
                Check("ShortcutHelpWindow", () => new ShortcutHelpWindow(TriggerKey.Default, static () => { }));
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

                // Again with the optional check box, which only one prompt in the app uses - so it would otherwise
                // never be rendered by anything but that one code path. This is also the shot docs/help publishes
                // for Remove duplicates, so the wording is kept in step with HistoryWindow.OnDeduplicateClicked.
                Check("MessageDialogOption", () => MessageDialog.CreateForSmokeTest(
                    "Entries that are an exact duplicate of another are removed, keeping one of each. Nothing that "
                    + "differs in any way is touched.\n\n"
                    + "An entry is judged by its timestamp, its kind, its text and its image, so two screenshots "
                    + "taken in the same second are not mistaken for one. The oldest of each set is kept."
                    + "\n\nThis cannot be undone.",
                    "Remove duplicate history entries?",
                    DialogKind.Warning,
                    DialogButtons.OkCancel,
                    optionText: "Ignore the _time it was copied",
                    optionHelp: "Judges an entry by its kind, its text and its image only, so the same thing copied "
                        + "on different days counts as one and the most recent is kept. This removes far more than "
                        + "the sweep described above."));
                // Seeded with a folder that does not exist, so the invalid branch of the validation is the one
                // rendered - that is the state with the extra status line and the disabled Import button.
                Check("ImportDialog", () => new ImportDialog(@"D:\Lokesh\DoNotMove\Clipjump_x64"));

                // Twice, deliberately, because this harness has two jobs that want opposite things here.
                //
                // The first enumerates the machine's real windowed processes, which is what a smoke test should
                // do - it exercises Populate and the icon extraction behind it, on real executables.
                //
                // The second is the one docs/help publishes, and it is seeded. The real list had been shipping
                // in the .chm complete with the author's Outlook and Teams window titles, their user name in
                // every path, and directories from unrelated private projects. A screenshot of a live machine is
                // a screenshot of whoever built it.
                Check("RunningAppPicker-Live", () => RunningAppPicker.CreateForSmokeTest());
                Check("RunningAppPicker", () => RunningAppPicker.CreateForSmokeTest(SampleRunningApps()));

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

                // The kind filter's chip. Worth its own frame because the chip is the only thing that tells the
                // user why most of their stack has gone, so a version of it that failed to render would be the
                // one defect this feature could have.
                Check("OverlayWindow-KindFilter", () => RenderOverlay(KindFilterFrame()));
                Check("OverlayWindow-TextFacts", () => RenderOverlay(TextFactsFrame()));

                // A copied TEXT FILE, whose contents are read off disk. Written as a real file because that is
                // the branch being exercised - FileTextPreviewCache opens it - and seeding a path that does not
                // exist would prove nothing and look identical to the feature being broken. Same reasoning as the
                // image file the history preview uses.
                //
                // Deliberately NOT in update-help-images.ps1's list. The frame necessarily shows a real path, so
                // the shot would carry the build machine's user name into a published .chm - the same leak the
                // running-app picker had. This exists to prove the read works, not to be published.
                Check("OverlayWindow-TextFile", () => RenderOverlay(TextFileFrame(root)));
            }
        }

        TryDelete(root);

        // Outside the theme loop: the tray icons are three application states, not a light/dark pair, so
        // running this twice would only assert the same thing again.
        Console.WriteLine();
        VerifyTrayIcons();

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

    /// <summary>
    /// Checks the settings search reaches every tab, and says what it found.
    /// <para>
    /// This is the one assumption the search rests on and the one that cannot be checked from a unit test: a
    /// TabControl applies the template for the selected tab only, so the index is built by walking the LOGICAL
    /// tree. If that ever stopped reaching unselected tabs, the search would quietly cover the first tab and
    /// nothing else - and still look like it worked, because the tab you were on would always be found.
    /// </para>
    /// <para>
    /// Advanced is excluded from the requirement: it holds a grid rather than labelled rows, so it has no
    /// searchable settings of its own by design - everything on it is reachable through the tab that owns it.
    /// </para>
    /// </summary>
    private static void VerifySearchIndex(SettingsWindow window)
    {
        var index = window.SearchIndexForSmokeTest();
        var byTab = index.GroupBy(static entry => entry.TabName).ToDictionary(static g => g.Key, static g => g.Count());

        Console.WriteLine($"  search index: {index.Count} settings across {byTab.Count} tabs");

        foreach (var pair in byTab.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"      {pair.Key,-16} {pair.Value}");
        }

        var expected = new[] { "Capture", "History", "Paste Mode", "Keys", "Appearance", "System" };
        var missing = expected.Where(t => !byTab.ContainsKey(t)).ToList();

        if (missing.Count > 0)
        {
            _failures++;
            Console.WriteLine($"  FAIL  search index found nothing on: {string.Join(", ", missing)}");
        }

        // Three queries covering the three ways a match can happen. "electron" is the interesting one: it appears
        // nowhere in a label, only in the paste-delay explanation, so it proves the inline help is searched -
        // which is most of why this search is worth having over reading the tab titles.
        ExpectSearchHit(window, "tray", "Left-Clicking the Tray Icon");
        ExpectSearchHit(window, "store images", "Store Images");
        ExpectSearchHit(window, "electron", "Pause Before Pasting (ms)");
    }

    /// <summary>
    /// Proves a long tooltip wraps inside a bounded width, which nothing else here can.
    /// <para>
    /// A tooltip is a popup shown on hover, so it is realised by neither constructing a window nor showing one -
    /// it was the widest unexercised template in the application, and it was reported: the Remove Duplicates
    /// explanation rendered as one ~1,000px line running off past the window edge.
    /// </para>
    /// <para>
    /// Measured rather than eyeballed, and the two assertions are different claims. The width proves MaxWidth is
    /// in force; the inner TextBlock's TextWrapping proves the wrapping actually reached the generated TextBlock,
    /// which is the part that is easy to get wrong - with MaxWidth alone the text is clipped at the limit instead
    /// of wrapping, and a screenshot of that looks almost right.
    /// </para>
    /// </summary>
    private static void VerifyToolTipWrapping(Window window)
    {
        // The real string from HistoryWindow.xaml, not a lorem ipsum: this is the tooltip that was reported, so
        // it is the one worth measuring. A copy rather than a reference because reaching the button would mean
        // walking the tree for it, and a wrong turn there would silently measure something shorter.
        const string longest =
            "Collapse entries that are exact duplicates, keeping one of each. Acts on whichever of the two "
            + "stores is shown. Imports before this version were not idempotent, so importing Clipjump more "
            + "than once left a copy per run.";

        var tip = new System.Windows.Controls.ToolTip
        {
            Content = longest,
            PlacementTarget = window,
            StaysOpen = true,
            IsOpen = true,
        };

        tip.UpdateLayout();
        Drain();

        var limit = tip.MaxWidth;
        var text = FindDescendant<System.Windows.Controls.TextBlock>(tip);
        var wrapping = text?.TextWrapping;

        Console.WriteLine(
            $"  tooltip: {tip.ActualWidth:F0}x{tip.ActualHeight:F0} px, limit {limit:F0}, wrapping {wrapping?.ToString() ?? "no TextBlock found"}");

        if (double.IsInfinity(limit) || tip.ActualWidth > limit + 1)
        {
            _failures++;
            Console.WriteLine($"  FAIL  a long tooltip is {tip.ActualWidth:F0}px wide against a limit of {limit:F0}");
        }

        if (wrapping is not TextWrapping.Wrap)
        {
            _failures++;
            Console.WriteLine("  FAIL  the tooltip's text does not wrap, so a long one is clipped rather than reflowed");
        }

        tip.IsOpen = false;
    }

    /// <summary>
    /// Turns each embedded tray icon into a real HICON at every size the shell asks for, and reports the size it
    /// actually got.
    /// <para>
    /// This is the only check on the whole path: the icons stopped being files on 2026-08-12, so a broken pack://
    /// URI, a resource that did not get embedded, or a frame Windows declines to decode would all present as a
    /// missing tray icon at run time - and with no main window, an unreachable application. None of it is
    /// reachable from Core.Tests, which cannot see the WPF project's resources.
    /// </para>
    /// <para>
    /// Two separate claims per size, because either alone would pass while the icon looked wrong. That Windows
    /// built an icon at all proves it decodes these frames - every one of ours is PNG-compressed, which some
    /// accounts say this API rejects. That the frame chosen is the exact size asked for is what keeps the icon
    /// sharp, and it is a fact about the artwork rather than about the code: IconFileTests proves the selection
    /// rule, but only this notices if a regenerated icon set stops carrying a 24 px frame.
    /// </para>
    /// </summary>
    private static void VerifyTrayIcons()
    {
        // 16 and 32 are the two system sizes; 24 is what the shell asks for at 150% scaling and is the one
        // neither ExtractIconEx nor the PE header can produce. 20 and 40 stand in for 125% and 250%.
        int[] sizes = [16, 20, 24, 32, 40];

        foreach (var name in new[] { TrayIconArt.Normal, TrayIconArt.Disabled, TrayIconArt.Paused })
        {
            var bytes = TrayIconArt.Read(name);

            if (bytes.Length == 0)
            {
                _failures++;
                Console.WriteLine($"  FAIL  {name} is not embedded, or could not be read by pack:// URI");
                continue;
            }

            var results = new List<string>();

            foreach (var size in sizes)
            {
                var (frame, created) = TrayIcon.DescribeIconForSmokeTest(bytes, size);

                results.Add((frame == size, created) switch
                {
                    (true, true) => $"{size}ok",
                    (false, true) => $"{size}<-{frame}px frame",
                    _ => $"{size}FAILED",
                });

                if (frame != size || !created)
                {
                    _failures++;
                }
            }

            Console.WriteLine($"  tray icon {name,-24} {bytes.Length,6} bytes  {string.Join("  ", results)}");
        }
    }

    /// <summary>First descendant of the given type, or null. Visual tree, so it must be called after layout.</summary>
    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is T match)
            {
                return match;
            }

            var found = FindDescendant<T>(child);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static void ExpectSearchHit(SettingsWindow window, string query, string expectedLabel)
    {
        var hits = window.SearchForSmokeTest(query);

        if (hits.Any(h => h.Label.Contains(expectedLabel, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _failures++;

        Console.WriteLine(
            $"  FAIL  searching \"{query}\" did not find \"{expectedLabel}\" "
                + $"(got: {(hits.Count == 0 ? "nothing" : string.Join(", ", hits.Take(3).Select(h => h.Label)))})");
    }

    /// <summary>
    /// Invented programs for the published screenshot. Ordinary Windows applications with plausible titles, and
    /// no icons - the grid's icon column simply stays empty, which is a state the real list can produce anyway
    /// for a packaged app whose exe carries no icon.
    /// <para>
    /// Deliberately nothing personal and no real user name: the point of seeding this is that a manual should not
    /// document whoever happened to build it. Paths use a generic profile name for the same reason.
    /// </para>
    /// </summary>
    private static IReadOnlyList<RunningApp> SampleRunningApps() =>
    [
        new("chrome.exe", "PasteJump - Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", null),
        new("Code.exe", "Program.cs - PasteJump - Visual Studio Code", @"C:\Program Files\Microsoft VS Code\Code.exe", null),
        new("EXCEL.EXE", "Book1 - Excel", @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE", null),
        new("explorer.exe", "Downloads", @"C:\Windows\explorer.exe", null),
        new("keepass.exe", "KeePass", @"C:\Program Files\KeePass Password Safe 2\KeePass.exe", null),
        new("notepad.exe", "notes.txt - Notepad", @"C:\Windows\System32\notepad.exe", null),
        new("SnippingTool.exe", "Snipping Tool", @"C:\Windows\System32\SnippingTool.exe", null),
        new("Teams.exe", "General - Microsoft Teams", @"C:\Program Files\WindowsApps\MSTeams\ms-teams.exe", null),
        new("WindowsTerminal.exe", "Windows PowerShell", @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe", null),
        new("winword.exe", "Report.docx - Word", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", null),
    ];

    /// <summary>An image clip with the stack narrowed to images, which is what the filter is for.</summary>
    private static PasteOverlayModel KindFilterFrame() => new()
    {
        Position = 2,
        Total = 5,
        PreviewText = "[image]",
        Kind = ClipKind.Image,
        Pinned = false,
        FormatterName = "Original",
        CommitMode = PasteCommitMode.Paste,
        IsSearching = false,
        MatchCount = 5,
        PopOnPaste = false,
        IsEmpty = false,
        KindFilter = PasteKindFilter.Images,
        SourceExecutable = "SnippingTool.exe",
    };

    /// <summary>
    /// A multi-line text clip, which is the frame that exercises the facts line for text - lines and characters
    /// on the left, bytes on the right. The ordinary TextFrame is one line and would not show the plural.
    /// </summary>
    private static PasteOverlayModel TextFactsFrame() => new()
    {
        Position = 4,
        Total = 41,
        PreviewText = "SELECT id, preview, captured_utc\nFROM history\nWHERE kind = 0\nORDER BY captured_utc DESC;",
        Kind = ClipKind.Text,
        Pinned = false,
        FormatterName = "Original",
        CommitMode = PasteCommitMode.Paste,
        IsSearching = false,
        MatchCount = 41,
        PopOnPaste = false,
        IsEmpty = false,
        TextFacts = TextMetrics.Describe("SELECT id, preview, captured_utc\nFROM history\nWHERE kind = 0\nORDER BY captured_utc DESC;"),
        TotalBytes = 178,
        SourceExecutable = "devenv.exe",
    };

    /// <summary>
    /// A copied text file: the path above, the file's first lines below, and its line count and size in the facts
    /// row. Writes the file it points at, so the read path is genuinely exercised.
    /// </summary>
    private static PasteOverlayModel TextFileFrame(string root)
    {
        var path = Path.Combine(root, "notes.txt");

        File.WriteAllText(
            path,
            """
            PasteJump - release checklist
            =============================

            1. dotnet build, zero warnings
            2. dotnet test
            3. UI smoke harness, both themes
            4. Rebuild the help and its screenshots
            5. Publish the folder build and deploy
            """);

        var description = FileListPreview.Describe([path]);

        return new PasteOverlayModel
        {
            Position = 7,
            Total = 41,
            PreviewText = description,
            Kind = ClipKind.Files,
            Pinned = false,
            FormatterName = "Original",
            CommitMode = PasteCommitMode.Paste,
            IsSearching = false,
            MatchCount = 41,
            PopOnPaste = false,
            IsEmpty = false,
            SourceExecutable = "explorer.exe",
        };
    }

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
