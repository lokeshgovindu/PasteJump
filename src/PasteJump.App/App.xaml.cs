using System.Windows;
using PasteJump.App.Services;
using PasteJump.App.Views;
using PasteJump.Core;
using PasteJump.Core.Capture;
using PasteJump.Core.Diagnostics;
using PasteJump.Core.Formatting;
using PasteJump.Core.Imaging;
using PasteJump.Core.Model;
using PasteJump.Core.Paste;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;
using PasteJump.Core.Storage;
using PasteJump.Interop;

namespace PasteJump.App;

/// <summary>
/// Application bootstrap. Composition happens here by hand rather than through a DI container:
/// there are about a dozen objects with a fixed lifetime and no configuration-time variation, so a
/// container would add indirection without removing any.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\PasteJump.SingleInstance.9F2C41A6";

    private Mutex? _singleInstanceMutex;

    private AppPaths _paths = null!;
    private SettingsStore _settingsStore = null!;
    private PasteJumpSettings _settings = null!;
    private ClipStore _store = null!;

    private MessageOnlyWindow _messageWindow = null!;
    private ClipboardMonitor _clipboardMonitor = null!;
    private Win32ClipboardAccess _clipboard = null!;
    private LowLevelKeyboardHook _keyboardHook = null!;
    private GlobalHotkey _historyHotkey = null!;
    private TrayIcon _trayIcon = null!;

    /// <summary>
    /// Virtual key of the configured paste-mode trigger, resolved once per settings change rather than on
    /// every keystroke. <see cref="OnKeyEvent"/> runs inside the hook callback, which blocks all keyboard
    /// input machine-wide, so it does no parsing.
    /// </summary>
    private int _triggerVirtualKey = TriggerKey.ToVirtualKey(TriggerKey.Default);

    private SelfWriteGuard _selfWrites = null!;
    private FormatterRegistry _formatters = null!;
    private PasteModeController _controller = null!;
    private PasteGestureRecognizer _recognizer = null!;
    private PasteJumpPasteHost _pasteHost = null!;
    private CaptureService _capture = null!;

    private ThemeManager _theme = null!;

    private HistoryWindow? _historyWindow;
    private SettingsWindow? _settingsWindow;
    private ShortcutHelpWindow? _helpWindow;
    private AboutWindow? _aboutWindow;
    private ToastWindow? _toast;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Debug only, and compiled out of Release entirely - see DebugConsole. First thing in start-up, so the
        // console exists before anything has something to say.
        DebugConsole.Attach("PasteJump - debug log");
        DebugConsole.Log($"PasteJump {AppVersion.Current} debug build starting");

        StartupTrace.Mark("WPF startup to OnStartup");

        // No main window, so WPF must not decide to exit when a transient window closes.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!TryAcquireSingleInstance())
        {
            // A second copy would install a second keyboard hook and fight the first over the
            // clipboard. Exit quietly rather than explaining; the user almost certainly just
            // double-clicked twice.
            Shutdown();
            return;
        }

        try
        {
            Compose();

            // Reported after Compose rather than at idle, because the question this answers is "why was there
            // a delay before the tray icon appeared", and Compose finishing is when it appears.
            DebugConsole.LogBlock("Start-up timings:", StartupTrace.Format());
        }
        catch (Exception ex)
        {
            // Safe even this early: App.xaml merges the palette declaratively at slot 0, so the themed
            // dialog has its brushes before Compose has run.
            MessageDialog.Show(
                ex.Message,
                headline: "PasteJump could not start",
                kind: DialogKind.Error);

            Shutdown();
        }
    }

    private void Compose()
    {
        _paths = AppPaths.Resolve();

        // Both before the store is opened. A pending move has to copy the database file itself, which is
        // impossible once SQLite has it open, and the legacy rename would otherwise create an empty
        // database beside the old one and make the user's history look like it had vanished.
        RunPendingDataMove();

        _paths.EnsureCreated();
        _paths.TryMigrateLegacyDatabase();

        // Before SettingsStore reads, or the rename would look like every setting reverting to its default.
        _paths.TryMigrateLegacySettings();

        WarnIfDataDirectoryIsReadOnly();

        // As soon as there is somewhere to put it. Everything logged before now was buffered, so nothing from
        // the earliest phases is lost - which matters, because those are the interesting ones.
        DebugConsole.SetLogDirectory(_paths.ClipsDirectory);

        StartupTrace.Mark("paths and migrations");

        _settingsStore = new SettingsStore(_paths);
        _settings = _settingsStore.Load();

        StartupTrace.Mark("settings load");

        // Before any window is constructed, so the first one painted is already the right colour
        // rather than flashing light and then re-rendering.
        _theme = new ThemeManager(this);
        _theme.Apply(_settings.Theme);

        StartupTrace.Mark("theme");

        _store = new ClipStore(_paths);

        // Before the first capture, and before retention runs - it is what new previews are truncated to.
        _store.PreviewMaxChars = _settings.PreviewMaxChars;

        StartupTrace.Mark("open database");
        DebugConsole.Log($"  store: {_store.Count} clips, {_store.HistoryCount} history entries");

        // Retention runs at startup rather than on a timer: this is a logon-resident app, so
        // startup happens at least daily, and a timer would be a wakeup for no user benefit.
        var pruned = _store.PruneHistoryOlderThan(_settings.HistoryRetentionDays);

        StartupTrace.Mark($"prune history older than {_settings.HistoryRetentionDays} days (removed {pruned})");

        // Before eviction, so junk clips do not occupy slots that push real ones out. A no-op on a store
        // captured after BookkeepingFormats started filtering them; on an older one it clears the 8-byte OLE
        // markers that were being promoted to the front of the stack on every screenshot.
        var purged = _store.PurgeContentlessClips();

        StartupTrace.Mark($"purge contentless clips (removed {purged})");

        var evicted = _store.EvictBeyond(_settings.EffectiveMaxClips);

        StartupTrace.Mark($"evict beyond {_settings.EffectiveMaxClips} clips (removed {evicted})");

        // After eviction, so blobs that are about to be discarded are not compressed first. Bounded
        // internally, and a no-op once the store has been converted.
        var compacted = _store.CompactBlobs();

        StartupTrace.Mark($"compact blobs (converted {compacted})");

        var foreground = new ForegroundWindowInfo();
        _clipboard = new Win32ClipboardAccess(foreground);
        _selfWrites = new SelfWriteGuard();
        _formatters = new FormatterRegistry();

        _messageWindow = new MessageOnlyWindow();
        _clipboardMonitor = new ClipboardMonitor(_messageWindow);

        _pasteHost = new PasteJumpPasteHost(
            _store,
            _clipboard,
            new InputSender(),
            _selfWrites,
            Dispatcher,
            () => new OverlayWindow());

        _pasteHost.TagEditorRequested += OnTagEditorRequested;
        _pasteHost.ClipEditorRequested += OnClipEditorRequested;
        _pasteHost.ExportRequested += OnExportRequested;
        _pasteHost.HelpRequested += ShowShortcutHelp;
        _pasteHost.TransientMessage += OnTransientMessage;
        _pasteHost.DeleteAllConfirmationRequested += OnDeleteAllConfirmationRequested;
        _pasteHost.Paster.SetSettleDelay(_settings.PasteSettleDelayMs);
        _pasteHost.Paster.SetPasteKeystroke(_settings.PasteKeystroke);
        _pasteHost.SetPreviewSize(_settings.OverlayPreviewMaxWidth, _settings.OverlayPreviewMaxHeight);
        _pasteHost.SetOverlayAnchor(_settings.OverlayX, _settings.OverlayY);
        _pasteHost.SetKeyHint(_settings.ShowOverlayKeyHint, TriggerKey.Normalise(_settings.PasteModeTriggerKey));

        _controller = new PasteModeController(
            new ClipStoreCatalog(_store),
            _pasteHost,
            _formatters,
            _settings.PasteModeOptions);

        _recognizer = new PasteGestureRecognizer(_controller);

        _capture = new CaptureService(
            _clipboard,
            _store,
            _selfWrites,
            foreground,
            () => _settings,
            clock: null,

            // Deferred retries run on a dispatcher timer, so a failed clipboard read is retried on
            // the UI thread rather than from a pool thread that might race the next notification.
            schedule: (delay, action) =>
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = delay };

                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    action();
                };

                timer.Start();
            });

        _capture.ClipCaptured += OnClipCaptured;
        _capture.CaptureObserved += OnDuplicateCaptureObserved;
        _capture.Prime();

        _clipboardMonitor.ClipboardChanged += _capture.OnClipboardChanged;
        _clipboardMonitor.Start();

        StartupTrace.Mark("services, capture and clipboard monitor");

        _triggerVirtualKey = TriggerKey.ToVirtualKey(TriggerKey.Normalise(_settings.PasteModeTriggerKey));

        _keyboardHook = new LowLevelKeyboardHook(OnKeyEvent);
        _keyboardHook.Install();

        _historyHotkey = new GlobalHotkey(_messageWindow);
        _historyHotkey.Pressed += ShowHistory;
        ApplyHistoryHotkey(announceFailure: true);

        StartupTrace.Mark("keyboard hook and hotkey");

        _trayIcon = new TrayIcon(BuildTrayTooltip(), _messageWindow);
        _trayIcon.Activated += ShowHistory;
        _trayIcon.ContextMenuRequested += ShowTrayMenu;

        // Before Show(), so the shell receives the crisply-sized icon from the start rather than the
        // fixed 16x16 TrayIcon extracts from the executable as its fallback.
        ApplyTrayIcon();
        _trayIcon.Show();

        StartupTrace.Mark("tray icon visible");

        // Both deferred to idle rather than run inline. A modal dialog here would own the UI thread with its
        // own Win32 message loop, which does not drain the Dispatcher - so every side effect PasteJumpPasteHost
        // queues would sit unprocessed and the gesture would appear dead for as long as the prompt was up. On
        // a first run against an existing Clipjump install that prompt is guaranteed, so this was the ordinary
        // first-launch experience rather than an edge case.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                MaybeOfferLegacyImport();
                HintAboutRivalManagers();
            }));
    }

    /// <summary>
    /// Mentions a detected rival clipboard manager as a passing toast, and does nothing else.
    /// <para>
    /// This used to be a modal dialog offering to switch the paste chord, and it was wrong to be one. Rivals
    /// are detected by process name, which cannot tell whether the other manager's paste hotkey is actually
    /// enabled - Clipjump has its own disable toggle and keeps running while switched off - so the dialog
    /// interrogated the user about a conflict that frequently did not exist. Reported as exactly that.
    /// </para>
    /// <para>
    /// A toast is the right weight for a guess: it informs without blocking, it cannot be answered wrongly,
    /// and it costs nothing when the guess is bad. The two actual remedies live in Settings, Paste mode,
    /// which is where someone who has a problem will go looking.
    /// </para>
    /// </summary>
    private void HintAboutRivalManagers()
    {
        if (!_settings.WarnAboutClipboardManagerConflict
            || _settings.PasteKeystroke != PasteKeystroke.CtrlV)
        {
            return;
        }

        var rivals = RivalClipboardManagers.Detect(RunningProcessNames());

        if (rivals.Count == 0)
        {
            return;
        }

        // Longer than a copy notification: this is a sentence to read, not an acknowledgement to glance at.
        Toast().Notify(
            "Another clipboard manager",
            RivalClipboardManagers.DescribeConflict(rivals),
            TimeSpan.FromSeconds(8));
    }

    /// <summary>
    /// Names of the processes currently running, for conflict detection.
    /// <para>
    /// Failures yield an empty list rather than propagating. Enumerating processes can throw when one
    /// exits mid-enumeration or when a protected process refuses to be queried, and neither is worth
    /// failing start-up over - the cost of a missed detection is one prompt not shown.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> RunningProcessNames()
    {
        try
        {
            return [.. System.Diagnostics.Process.GetProcesses().Select(static p => p.ProcessName)];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Carries out a data move the settings dialog asked for, then clears the request.
    /// <para>
    /// Deferred to startup rather than done when the user clicks OK because the database is open at that
    /// point and cannot be copied. The request is cleared whatever the outcome: a move that failed will
    /// fail again on the next launch, and retrying it silently at every start would hide the problem
    /// while making startup slow.
    /// </para>
    /// </summary>
    private void RunPendingDataMove()
    {
        var pointer = DataLocationPointer.Read(AppPaths.ApplicationDirectory);

        if (pointer.PendingClipsMove is null && pointer.PendingSettingsMove is null)
        {
            return;
        }

        var problems = new List<string>();

        if (pointer.PendingClipsMove is { } clipsFrom)
        {
            Report(DataMigrator.AdoptClips(clipsFrom, _paths.ClipsRoot), "clips", clipsFrom);
        }

        if (pointer.PendingSettingsMove is { } settingsFrom)
        {
            Report(DataMigrator.AdoptSettings(settingsFrom, _paths.SettingsRoot), "settings", settingsFrom);
        }

        // Cleared whatever the outcome. A move that failed will fail again on the next launch, and
        // retrying it silently at every start would hide the problem while slowing startup.
        pointer.MigrateClipsFrom = null;
        pointer.MigrateSettingsFrom = null;
        pointer.LegacyMigrateFrom = null;
        pointer.TryWrite(AppPaths.ApplicationDirectory);

        if (problems.Count > 0)
        {
            MessageDialog.Warn(
                string.Join("\n\n", problems) + "\n\nNothing was removed from the old location.",
                headline: "Could not finish moving the data");
        }

        void Report(DataMigrationReport report, string what, string from)
        {
            if (report.Error is { } error)
            {
                problems.Add($"Moving {what} from {Path.Combine(from, "data")} failed: {error}");
            }
        }
    }

    /// <summary>
    /// Tells the user when a data directory cannot be written to, rather than letting it surface as an
    /// opaque database error. The overwhelmingly common cause is a portable folder unzipped somewhere only
    /// an administrator can write, and switching that half's location is the fix.
    /// <para>
    /// Reports the two halves separately, because they can be in different places and the advice differs:
    /// unwritable clips means nothing is saved at all, while unwritable settings only means changes do not
    /// stick.
    /// </para>
    /// </summary>
    private void WarnIfDataDirectoryIsReadOnly()
    {
        var (clipsOk, settingsOk) = _paths.CheckWritable();

        if (clipsOk && settingsOk)
        {
            return;
        }

        var message = !clipsOk
            ? $"PasteJump cannot write to:\n\n{_paths.ClipsDirectory}\n\nClips will not be saved."
            : $"PasteJump cannot write to:\n\n{_paths.SettingsDirectory}\n\nSettings changes will not be kept.";

        MessageDialog.Warn(
            message + "\n\nOpen Settings and store that data in your user profile instead, or move " +
            "PasteJump to a folder you can write to.",
            headline: "Cannot write to the data folder");
    }

    /// <summary>
    /// The keyboard hook handler. Runs on the UI thread and blocks all keyboard input until it
    /// returns, so it does translation and state-machine work only - every side effect is queued
    /// onto the dispatcher by <see cref="PasteJumpPasteHost"/>.
    /// </summary>
    private bool OnKeyEvent(KeyboardHookEvent e)
    {
        // Our own synthesised keystrokes. Without this check, sending Ctrl+V to paste would
        // immediately re-enter paste mode and never stop.
        //
        // Matched on our dwExtraInfo signature rather than on LLKHF_INJECTED, which is set by every
        // process that calls SendInput. Filtering on the flag alone meant the gesture did nothing at
        // all under Remote Desktop, inside VM guest windows, or for anyone using a macro keyboard or
        // on-screen keyboard - all of which deliver genuine user intent as injected input.
        if (e.IsOwnInjection)
        {
            return false;
        }

        var key = VirtualKeyTranslator.ToGestureKey(e.VirtualKey, _triggerVirtualKey);

        if (key != GestureKey.None && _recognizer.Handle(key, e.IsKeyDown))
        {
            return true;
        }

        // Only ask the layout for a character when a session is actually open. Calling
        // ToUnicodeEx on every keystroke machine-wide would be both wasteful and a needless
        // interaction with the kernel's dead-key state.
        if (e.IsKeyDown && _recognizer.IsSessionActive)
        {
            var character = VirtualKeyTranslator.ToCharacter(e.VirtualKey);

            if (character is { } ch && _recognizer.HandleCharacter(ch))
            {
                return true;
            }
        }

        // Nothing claimed it. While the overlay is open it still must not reach the application underneath:
        // the user is holding Ctrl, and almost every Ctrl+key out there is a command - Ctrl+0 and Ctrl+= zoom
        // VS Code, Ctrl+W closes a tab, Ctrl+S saves. Letting them through meant browsing clips quietly zoomed
        // or closed whatever was behind the overlay.
        //
        // Modifiers are exempt because the application tracks them, and Alt or Win chords are exempt so the
        // shell keeps working - Alt+Tab must still switch away, which is also the way out if a session ever
        // failed to close.
        if (!VirtualKeyTranslator.IsModifier(e.VirtualKey)
            && _recognizer.ShouldSwallowUnhandled(VirtualKeyTranslator.IsAltDown(), VirtualKeyTranslator.IsWinDown()))
        {
            return true;
        }

        return false;
    }

    private void OnClipCaptured(Clip clip)
    {
        _historyWindow?.QueueRefresh();

        // Before the notification checks below, and independent of them: the beep exists precisely for the
        // case where the toast is off or on a monitor you are not looking at.
        if (_settings.BeepOnCopy)
        {
            CopyBeep.Play(_settings.BeepFrequencyHz, _settings.BeepDurationMs);
        }

        // A new copy makes the next Ctrl+V open on the newest clip. Without this the remembered
        // position persisted for ever, so after browsing to the fifth clip once, every subsequent
        // paste offered that same clip however much had been copied since.
        _controller.NotifyClipCaptured();

        if (!_settings.ShowCopyNotification)
        {
            return;
        }

        // Suppressed while paste mode is open. The overlay is already showing the user the stack, and
        // a second floating window appearing beside it during the gesture is just noise.
        if (_recognizer.IsSessionActive)
        {
            return;
        }

        var total = _store.Count;

        Toast().Notify(
            total == 1 ? "Copied - 1 clip" : $"Copied - {total} clips",
            ToastDetail(clip),
            TimeSpan.FromMilliseconds(_settings.CopyNotificationMs));
    }

    /// <summary>
    /// The toast's second line. Text is flattened; a file list keeps its header on its own line and has its
    /// names joined back onto one.
    /// <para>
    /// The stored preview puts one name per line, which is right for the history preview pane where there is
    /// room to scan a list. The toast has about two lines, so one name per line would show two of them and
    /// clip the rest - commas fit far more in the same space. Same principle as the length clamp: the record
    /// is complete, the display abbreviates.
    /// </para>
    /// <para>
    /// Text clips are flattened entirely: their line breaks are the author's, arbitrary in number, and a
    /// toast is not the place to honour them.
    /// </para>
    /// </summary>
    private static string ToastDetail(Clip clip)
    {
        if (clip.Kind != ClipKind.Files || string.IsNullOrWhiteSpace(clip.Preview))
        {
            return SingleLine(clip.Preview);
        }

        var lines = clip.Preview.ReplaceLineEndings("\n").Split('\n');

        if (lines.Length < 2)
        {
            return SingleLine(clip.Preview);
        }

        // Only the names are clamped. The header is one short line and is the part worth guaranteeing.
        var names = string.Join(", ", lines.Skip(1).Where(static l => l.Length > 0));

        return lines[0] + Environment.NewLine + SingleLine(names);
    }

    /// <summary>
    /// Points the tray icon at the application icon, so the notification area shows the same mark as
    /// Explorer, the taskbar and every window title bar.
    /// <para>
    /// This replaced a pair of monochrome glyphs chosen at runtime from the taskbar colour. A coloured
    /// tile does not need that: it reads against a light or a dark taskbar equally, which is the whole
    /// reason the two-variant scheme existed. Dropping it also removed the app's only dependency on
    /// <c>SystemUsesLightTheme</c>, and with it the third-party glyph artwork whose licence did not
    /// cover redistribution.
    /// </para>
    /// <para>
    /// If the file is missing, <c>TrayIcon</c> keeps the icon it extracted from the executable - a
    /// portable folder can be copied incompletely, and no tray icon at all would leave the app running
    /// with no reachable menu.
    /// </para>
    /// </summary>
    private void ApplyTrayIcon()
    {
        // Three states, and each earns a distinct icon because the tooltip is the only other signal and it
        // needs a hover. Greyed-while-disabled is Windows' own convention for an inactive icon, and matters
        // most because Disable is not persisted - the state has to be obvious or it is easy to forget the app
        // is switched off. Amber-while-paused exists because "Pause" and "Disable" were reported as feeling
        // like the same command: their only visible difference is whether Ctrl+V still works, which is
        // invisible until you try it.
        //
        // Disabled is tested first: disabling also stops capture, so both conditions hold at once, and the
        // stronger state is the one worth showing.
        var name = _keyboardHook switch
        {
            { IsInstalled: false } => "pastejump-disabled.ico",
            _ when !_settings.MonitorClipboard => "pastejump-paused.ico",
            _ => "pastejump.ico",
        };

        // Through AppPaths, so this resolves off Environment.ProcessPath like every other path in the
        // app. AppContext.BaseDirectory would look correct and then break under a single-file publish,
        // where it points at the extraction directory rather than the folder holding the exe.
        _trayIcon.SetIconFromFile(Path.Combine(_paths.AssetsDirectory, name));
    }

    /// <summary>
    /// A repeat copy that was suppressed rather than stored. Still acknowledged, and labelled so the
    /// user can see why the clip count did not move.
    /// </summary>
    private void OnDuplicateCaptureObserved()
    {
        if (!_settings.ShowCopyNotification || _recognizer.IsSessionActive)
        {
            return;
        }

        var total = _store.Count;

        Toast().Notify(
            total == 1 ? "Copied - 1 clip" : $"Copied - {total} clips",
            "Same as the last copy, so not added again",
            TimeSpan.FromMilliseconds(_settings.CopyNotificationMs));
    }

    private void OnTransientMessage(string message)
        // Longer than a copy notification: these report a failure the user may need to act on.
        => Toast().Notify(message, null, TimeSpan.FromMilliseconds(Math.Max(2500, _settings.CopyNotificationMs)));

    /// <summary>
    /// Confirms the paste-mode DELETE ALL before it happens. Runs on the Dispatcher, well after the keyboard hook
    /// has returned - the controller hands this over as a request precisely so nothing modal can run in the hook.
    /// </summary>
    private void OnDeleteAllConfirmationRequested(int unpinnedCount, Action confirmed)
    {
        if (unpinnedCount == 0)
        {
            // Nothing to lose, so nothing to ask. A prompt here would be pure noise.
            return;
        }

        // The toast is hidden on entering paste mode and the overlay is already down by now, so there is nothing
        // for this to appear behind.
        var accepted = MessageDialog.Show(
            "Pinned clips are kept. This cannot be undone - the clips are removed from the stack, though history "
                + "keeps its own record.",
            headline: $"Delete all {unpinnedCount} unpinned clip{(unpinnedCount == 1 ? string.Empty : "s")}?",
            kind: DialogKind.Warning,
            buttons: DialogButtons.OkCancel) == DialogResultKind.Accepted;

        if (!accepted)
        {
            return;
        }

        confirmed();
        OnTransientMessage($"Deleted {unpinnedCount} clip{(unpinnedCount == 1 ? string.Empty : "s")}.");
    }

    private ToastWindow Toast()
    {
        if (_toast is null)
        {
            _toast = new ToastWindow();
            _toast.Closed += (_, _) => _toast = null;
        }

        return _toast;
    }

    /// <summary>Collapses a preview to one line, so a multi-line clip cannot stretch the toast.</summary>
    private static string SingleLine(string? preview)
    {
        if (string.IsNullOrWhiteSpace(preview))
        {
            return string.Empty;
        }

        var flattened = preview.ReplaceLineEndings(" ").Trim();

        return flattened.Length > 160 ? flattened[..160] + "…" : flattened;
    }

    // ------------------------------------------------------------- tray and windows

    private void ShowTrayMenu(int x, int y)
    {
        var menu = TrayMenuBuilder.Build(
            onAbout: ShowAbout,
            onHistory: ShowHistory,
            onSettings: ShowSettings,
            onHelp: ShowShortcutHelp,
            onPauseToggle: TogglePaused,
            onDisableToggle: ToggleDisabled,
            onRestart: RestartFromMenu,
            onExit: ExitApplication,
            isPaused: !_settings.MonitorClipboard,
            isDisabled: !_keyboardHook.IsInstalled);

        TrayMenuBuilder.ShowAt(menu, x, y);
    }

    /// <summary>
    /// Matches a window's OS title bar to the theme. The frame is drawn by DWM, not WPF, so it does
    /// not follow the palette - without this a dark window keeps a white title bar.
    /// </summary>
    private T Themed<T>(T window)
        where T : Window
    {
        window.SourceInitialized += (_, _) => _theme.ApplyTitleBar(window);
        return window;
    }

    private void ShowHistory()
    {
        if (_historyWindow is null)
        {
            _historyWindow = Themed(new HistoryWindow(
                _store,
                _clipboard,
                _selfWrites,
                _formatters,
                _settings.GridDensity,
                _settings.HistoryLoadLimit,
                _settings.HistoryPreviewMaxWidth));
            _historyWindow.DensityChanged += OnHistoryDensityChanged;
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }
        else
        {
            _historyWindow.Activate();
        }
    }

    private void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = Themed(new SettingsWindow(
                _settings, _formatters, _paths.ClipsLocation, _paths.SettingsLocation));
            _settingsWindow.SettingsApplied += OnSettingsApplied;
            _settingsWindow.DataLocationChangeRequested += OnDataLocationChangeRequested;
            _settingsWindow.LegacyImportRequested += OnLegacyImportRequested;
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }
    }

    /// <summary>
    /// Persists a density chosen in the history window, and keeps an open settings dialog honest about it.
    /// <para>
    /// The dialog must be told, not left to notice: it holds its own copy of the settings and writes the whole
    /// object back on OK, so without this it would quietly restore the old density - the same trap
    /// <see cref="SettingsWindow.ReloadRetention"/> exists for.
    /// </para>
    /// </summary>
    private void OnHistoryDensityChanged(GridDensity density)
    {
        if (_settings.GridDensity == density)
        {
            return;
        }

        _settings.GridDensity = density;
        _settingsStore.Save(_settings);

        _settingsWindow?.ReloadDensity(density);
    }

    private void ShowAbout()
    {
        if (_aboutWindow is null)
        {
            _aboutWindow = Themed(new AboutWindow());
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
            _aboutWindow.Show();
        }
        else
        {
            _aboutWindow.Activate();
        }
    }

    /// <summary>
    /// Applies the configured history hotkey.
    /// <para>
    /// A refused registration is reported rather than swallowed. The only realistic cause is another
    /// application already owning the chord, and the symptom otherwise is a hotkey that simply does
    /// nothing - indistinguishable from a setting that failed to save.
    /// </para>
    /// </summary>
    private void ApplyHistoryHotkey(bool announceFailure)
    {
        var spec = HotkeySpec.ParseOrNone(_settings.HistoryHotkey);

        if (_historyHotkey.TryRegister(spec) || !announceFailure)
        {
            return;
        }

        MessageDialog.Warn(
            $"Windows would not give PasteJump the hotkey {spec}. Another program has already claimed it.\n\n" +
            "Choose a different combination under Settings, Paste mode.",
            headline: "Hotkey unavailable");
    }

    /// <summary>
    /// Shows the paste-mode key list. Reachable from the tray menu and from <c>F1</c> during the gesture.
    /// <para>
    /// It must not take focus, which is why the window sets <c>ShowActivated="False"</c> and why this does not
    /// call <c>Activate</c>. <c>F1</c> is pressed <em>mid-gesture</em>, with Ctrl still held and the target
    /// application still expecting the paste - activating a window there moves focus off that application, so
    /// the paste would land in the help window instead of the document. The user can still click it to focus
    /// it; nothing here refuses that.
    /// </para>
    /// </summary>
    private void ShowShortcutHelp()
    {
        if (_helpWindow is null)
        {
            _helpWindow = Themed(new ShortcutHelpWindow(
                TriggerKey.Normalise(_settings.PasteModeTriggerKey)));
            _helpWindow.Closed += (_, _) => _helpWindow = null;
            _helpWindow.Show();
            return;
        }

        // Already open. Brought to the front without activating, for the reason above - Activate() was what
        // this used to do, and during a gesture that is the bug.
        _helpWindow.Topmost = true;
        _helpWindow.Topmost = false;
    }

    private void OnSettingsApplied(PasteJumpSettings updated)
    {
        _settings = updated;
        _settingsStore.Save(_settings);

        _theme.Apply(_settings.Theme);
        _pasteHost.Paster.SetSettleDelay(_settings.PasteSettleDelayMs);
        _pasteHost.Paster.SetPasteKeystroke(_settings.PasteKeystroke);
        _pasteHost.SetPreviewSize(_settings.OverlayPreviewMaxWidth, _settings.OverlayPreviewMaxHeight);
        _pasteHost.SetOverlayAnchor(_settings.OverlayX, _settings.OverlayY);
        _pasteHost.SetKeyHint(_settings.ShowOverlayKeyHint, TriggerKey.Normalise(_settings.PasteModeTriggerKey));

        _triggerVirtualKey = TriggerKey.ToVirtualKey(TriggerKey.Normalise(_settings.PasteModeTriggerKey));
        ApplyHistoryHotkey(announceFailure: true);

        // The help window lists the trigger key by name, so a change to it makes an open copy wrong.
        _helpWindow?.Close();

        // An open history window follows the new density rather than needing to be reopened.
        _historyWindow?.ApplyDensity(_settings.GridDensity);
        _historyWindow?.ApplyLimits(_settings.HistoryLoadLimit, _settings.HistoryPreviewMaxWidth);

        // Set on the store rather than passed per call, because it decides what gets written and every write
        // path would otherwise have to remember to thread it through.
        _store.PreviewMaxChars = _settings.PreviewMaxChars;

        StartupShortcut.Apply(_settings.RunAtLogon);

        _store.PruneHistoryOlderThan(_settings.HistoryRetentionDays);
        _store.EvictBeyond(_settings.EffectiveMaxClips);

        // Paste-mode options are captured at construction, so the controller is rebuilt rather
        // than mutated. Cheap, and it avoids a half-applied configuration.
        _controller = new PasteModeController(
            new ClipStoreCatalog(_store),
            _pasteHost,
            _formatters,
            _settings.PasteModeOptions);

        _recognizer = new PasteGestureRecognizer(_controller);

        // "Watch the clipboard" is editable here as well as from the tray, and it is what the paused icon
        // reflects - so both routes into it have to refresh the tray or the icon disagrees with the setting.
        _trayIcon.SetTooltip(BuildTrayTooltip());
        ApplyTrayIcon();
    }

    /// <summary>
    /// Records a new data location and restarts, which is what actually performs the move.
    /// <para>
    /// A restart rather than an in-process rebuild. The database, the blob store and the settings file are
    /// all open and referenced by half the object graph; tearing that down and rebuilding it against a new
    /// root is a great deal of code to get subtly wrong for a setting changed once. Restarting reuses the
    /// startup path that is already exercised on every launch.
    /// </para>
    /// </summary>
    private void OnDataLocationChangeRequested(DataLocation clips, DataLocation settings)
    {
        var clipsChanged = clips != _paths.ClipsLocation;
        var settingsChanged = settings != _paths.SettingsLocation;

        var moves = new List<string>();

        if (clipsChanged)
        {
            moves.Add($"Clips  →  {Path.Combine(AppPaths.RootFor(clips), "data")}");
        }

        if (settingsChanged)
        {
            moves.Add($"Settings  →  {Path.Combine(AppPaths.RootFor(settings), "data")}");
        }

        var accepted = MessageDialog.Show(
            "PasteJump will restart and copy to the new location:\n\n" +
            string.Join("\n", moves) +
            "\n\nThe existing copy is left where it is. Delete it yourself once you are happy the move " +
            "worked.",
            headline: "Move PasteJump's data?",
            title: "PasteJump - move data",
            kind: DialogKind.Question,
            buttons: DialogButtons.OkCancel,
            owner: _settingsWindow) == DialogResultKind.Accepted;

        if (!accepted)
        {
            return;
        }

        var pointer = new DataLocationPointer
        {
            ClipsLocation = clips,
            SettingsLocation = settings,

            // Recorded now, while we still know where each half currently is. After the restart the app
            // resolves the new roots and has no other way to find the old ones. Only the half that
            // actually moved is recorded, so an unchanged half is not needlessly re-examined.
            MigrateClipsFrom = clipsChanged ? _paths.ClipsRoot : null,
            MigrateSettingsFrom = settingsChanged ? _paths.SettingsRoot : null,
        };

        if (!pointer.TryWrite(AppPaths.ApplicationDirectory))
        {
            MessageDialog.Warn(
                "The folder holding PasteJump.exe is not writable, so the choice would not survive a " +
                "restart.\n\nNothing was changed.",
                headline: "Could not save the new data location",
                owner: _settingsWindow);

            return;
        }

        Restart();
    }

    /// <summary>
    /// Restart from the tray menu.
    /// <para>
    /// Unconfirmed on purpose. It is a menu item the user chose deliberately, it loses nothing - the store
    /// is checkpointed on the way out by <see cref="OnExit"/> - and a confirmation prompt on a
    /// two-second operation is friction rather than safety.
    /// </para>
    /// </summary>
    private void RestartFromMenu() => Restart();

    /// <summary>
    /// Relaunches and exits. The new process has to wait for this one to release the single-instance
    /// mutex, which is what the short retry loop in the launched copy would otherwise be for - here it is
    /// handled by starting the replacement only after <see cref="OnExit"/> has run.
    /// </summary>
    private void Restart()
    {
        var exePath = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exePath))
        {
            MessageDialog.Show(
                "PasteJump could not work out its own path to restart. Close and reopen it to finish " +
                "moving the data.");

            return;
        }

        Exit += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                });
            }
            catch (Exception)
            {
                // Nothing useful left to say: the UI is gone by the time this runs. The move still
                // happens the next time the user starts PasteJump, because the pointer file is written.
            }
        };

        Shutdown();
    }

    private void TogglePaused()
    {
        _settings.MonitorClipboard = !_settings.MonitorClipboard;
        _settingsStore.Save(_settings);

        _trayIcon.SetTooltip(BuildTrayTooltip());

        // Without this the only sign of being paused was the tooltip, which is why Pause and Disable were
        // reported as indistinguishable - Disable greyed the icon and Pause changed nothing on screen.
        ApplyTrayIcon();
    }

    /// <summary>
    /// Makes PasteJump wholly inert, or brings it back.
    /// <para>
    /// Goes further than pausing: the keyboard hook is uninstalled and the global hotkey released, so
    /// <c>Ctrl+V</c> reaches applications exactly as it would if PasteJump were not running. That is what
    /// makes it useful - it is the way to hand the chord to another clipboard manager, and the way to rule
    /// PasteJump out when something else on the machine is behaving oddly.
    /// </para>
    /// <para>
    /// Deliberately not persisted. "Disabled" is a temporary state you enter to get the app out of the way,
    /// and a clipboard manager that silently starts up dead - weeks later, with no memory of having switched
    /// it off - would look thoroughly broken. Pausing persists because it is a preference; this is not.
    /// </para>
    /// </summary>
    private void ToggleDisabled()
    {
        if (_keyboardHook.IsInstalled)
        {
            // The session is closed first, so the overlay cannot be left on screen with no way to dismiss
            // it once the keys that would dismiss it are no longer being received.
            _recognizer.Reset();

            _keyboardHook.Uninstall();
            _historyHotkey.Unregister();
            _clipboardMonitor.Stop();
        }
        else
        {
            _keyboardHook.Install();
            ApplyHistoryHotkey(announceFailure: false);
            _clipboardMonitor.Start();

            // Re-primed, so the clipboard change that happened while we were not listening is treated as the
            // baseline rather than captured as a brand new clip the moment monitoring resumes.
            _capture.Prime();
        }

        _trayIcon.SetTooltip(BuildTrayTooltip());
        ApplyTrayIcon();
    }

    /// <summary>
    /// Tray tooltip: name, version, and the paused state when it applies.
    /// <para>
    /// Built in one place so the version cannot go missing from the paused variant - which is what
    /// happened when the two strings were written out inline at their call sites.
    /// </para>
    /// </summary>
    private string BuildTrayTooltip()
    {
        // Full four-part version, not the shortened form. This is the fastest place to read the build number
        // from without opening a window, so it should match what a bug report needs verbatim.
        var text = $"PasteJump {AppVersion.Current}";

        // Disabled outranks paused, because it is the stronger statement: a disabled PasteJump is not
        // watching the clipboard either, so reporting "paused" would understate what is switched off.
        if (_keyboardHook is { IsInstalled: false })
        {
            return text + " (disabled)";
        }

        return _settings.MonitorClipboard ? text : text + " (paused)";
    }

    private void OnTagEditorRequested(Clip clip)
    {
        var dialog = Themed(new TagEditorWindow(clip.Tags));

        if (dialog.ShowDialog() == true)
        {
            _store.SetTags(clip.Id, dialog.Tags);
            _historyWindow?.QueueRefresh();
        }
    }

    /// <summary>
    /// Opens a clip in an external editor, picking the editor from what the clip actually holds.
    /// <para>
    /// Text is preferred over the image when a clip carries both, which is common - copying a table from a
    /// browser publishes HTML, plain text and a bitmap together, and the text is what someone pressing
    /// "edit" almost always means.
    /// </para>
    /// </summary>
    private void OnClipEditorRequested(Clip clip)
    {
        try
        {
            var payloads = _store.GetPayloads(clip.Id);

            if (Win32ClipboardAccess.ExtractText(payloads) is { } text)
            {
                LaunchEditor(_settings.TextEditor, $"pastejump-{clip.Id}.txt", File.WriteAllBytes, Encode(text));
                return;
            }

            // CF_DIB or CF_DIBV5. Rendered to a real .bmp file with the header an image editor expects -
            // the clipboard's DIB has no BITMAPFILEHEADER, so writing the raw bytes out would produce a
            // file nothing can open. DibConverter already does this for the export path.
            var dib = payloads.FirstOrDefault(static p => p.FormatId is 8 or 17);
            var bitmap = dib is null ? null : DibConverter.TryCreateBitmapFile(dib.Data);

            if (bitmap is null)
            {
                MessageDialog.Show("This clip has nothing that can be edited.");
                return;
            }

            LaunchEditor(_settings.ImageEditor, $"pastejump-{clip.Id}.bmp", File.WriteAllBytes, bitmap);
        }
        catch (Exception ex)
        {
            MessageDialog.Warn(ex.Message, headline: "Could not open the editor");
        }

        static byte[] Encode(string text) => System.Text.Encoding.UTF8.GetBytes(text);

        static void LaunchEditor(string editor, string fileName, Action<string, byte[]> write, byte[] content)
        {
            var tempPath = Path.Combine(Path.GetTempPath(), fileName);
            write(tempPath, content);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = editor,
                Arguments = $"\"{tempPath}\"",
                UseShellExecute = true,
            });
        }
    }

    private void OnExportRequested(Clip clip)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"clip-{clip.Id}",
            DefaultExt = clip.Kind == ClipKind.Image ? ".bmp" : ".txt",
            Filter = clip.Kind == ClipKind.Image
                ? "Bitmap|*.bmp|All files|*.*"
                : "Text|*.txt|All files|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var payloads = _store.GetPayloads(clip.Id);

            if (clip.Kind == ClipKind.Image)
            {
                var dib = payloads.FirstOrDefault(static p => p.FormatId is 8 or 17);
                var bitmap = dib is null ? null : DibConverter.TryCreateBitmapFile(dib.Data);

                if (bitmap is null)
                {
                    MessageDialog.Warn("This image could not be exported.");
                    return;
                }

                File.WriteAllBytes(dialog.FileName, bitmap);
            }
            else
            {
                File.WriteAllText(dialog.FileName, Win32ClipboardAccess.ExtractText(payloads) ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            MessageDialog.Warn(ex.Message, headline: "Export failed");
        }
    }

    /// <summary>
    /// Offers the Clipjump import once, on the first run that finds an installation.
    /// <para>
    /// Answered either way, it never asks again - the import is also reachable on demand from Settings,
    /// History, so declining it here is not a decision the user is locked out of reversing. That is what makes
    /// a single, skippable prompt acceptable rather than something that has to nag.
    /// </para>
    /// </summary>
    private void MaybeOfferLegacyImport()
    {
        if (_settings.LegacyImportCompleted)
        {
            return;
        }

        var candidate = Import.LegacyClipjumpLocator.FindLikelyInstallation();

        // Remembered either way, including when nothing was found, so this costs one locator sweep per
        // installation rather than one per launch.
        _settings.LegacyImportCompleted = true;
        _settingsStore.Save(_settings);

        // Only offered unprompted when something was actually found. With nothing detected there is no
        // question worth interrupting a first launch with - Settings, History has the button for anyone who
        // knows they have a Clipjump somewhere.
        if (candidate is not null)
        {
            ShowImportDialog(candidate, owner: null);
        }
    }

    /// <summary>Imports on demand, from the button in Settings.</summary>
    private void OnLegacyImportRequested()
        => ShowImportDialog(Import.LegacyClipjumpLocator.FindLikelyInstallation(), _settingsWindow);

    /// <summary>
    /// Shows the import dialog, seeded with whatever the locator found.
    /// <para>
    /// The detected folder is a starting point rather than a verdict. Clipjump ships as a portable folder with
    /// no installer and no registry footprint, so detection is a depth-limited search of plausible locations -
    /// it can pick the wrong copy when there are several, and it can find nothing at all when the real one is
    /// somewhere unusual. Both cases are handled by letting the user browse.
    /// </para>
    /// </summary>
    private void ShowImportDialog(string? detected, Window? owner)
    {
        var dialog = Themed(new ImportDialog(detected));

        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        if (dialog.ShowDialog() == true)
        {
            RunLegacyImport(dialog.SelectedFolder, owner);
        }
    }

    /// <summary>
    /// Runs the import behind a cancellable progress dialog.
    /// <para>
    /// Not inline. A Clipjump folder can live in OneDrive, where every file is a cloud placeholder until
    /// something opens it - so the database copy and each image row can block on a download. Run on the UI
    /// thread that presented as PasteJump hanging, with no way out but killing the process.
    /// </para>
    /// </summary>
    private void RunLegacyImport(string candidate, Window? owner)
    {
        // The clip limit is passed rather than assumed, so the import can never fill the stack past what the
        // store keeps - which would evict the excess at once and take the user's own recent clips with it.
        var report = ImportProgressDialog.Run(
            (progress, token) => Import.LegacyClipjumpImporter.ImportHistory(
                candidate, _store, progress, token, maxClips: _settings.EffectiveMaxClips),
            owner);

        // Refreshed before the summary, so the numbers on screen already match what the dialog is about to
        // claim - and it happens for a cancelled run too, which still imported everything up to the stop.
        _historyWindow?.QueueRefresh();

        // Duplicates are called out separately from skips, and phrased as the import having been run before
        // rather than as a failure - which is what they are, now that the import checks.
        var duplicates = report.Duplicates > 0
            ? $"\nAlready present, so left alone: {report.Duplicates}."
            : string.Empty;

        var summary = $"Imported {report.Imported} history entries.\nSkipped {report.Skipped}.{duplicates}\n\n"
            + $"Imported {report.ClipsImported} clips into the Ctrl+V stack"
            + (report.ClipsSkipped > 0
                ? $", skipping {report.ClipsSkipped} that held no replayable format."
                : ".")
            + (report.ClipsImported > 0
                ? "\nImported clips keep their text, images and file lists; rich formatting cannot be "
                    + "recovered, because Clipjump recorded those formats by a number that is only "
                    + "meaningful within the Windows session that wrote it."
                : string.Empty) +
            (report.Errors.Count > 0
                ? $"\n\nProblems:\n{string.Join('\n', report.Errors.Take(5))}"
                : string.Empty);

        if (report.Cancelled)
        {
            MessageDialog.Show(
                summary + "\n\nWhatever was imported has been kept. Running the import again picks up where " +
                "this left off - entries already imported are skipped.",
                headline: "Import stopped",
                title: "PasteJump - import history",
                owner: owner);

            return;
        }

        // Errors with nothing imported means the source was unusable rather than partly awkward, so it is
        // reported as a failure rather than as a completed run that happens to have problems.
        if (report is { Imported: 0, Errors.Count: > 0 })
        {
            MessageDialog.Warn(
                string.Join('\n', report.Errors.Take(5)),
                headline: "Import failed",
                owner: owner);

            return;
        }

        MessageDialog.Show(
            summary,
            headline: "Import complete",
            title: "PasteJump - import history",
            owner: owner);

        OfferToKeepImportedHistory(report, owner);
    }

    /// <summary>
    /// Warns when history retention is about to delete part of what was just imported, and offers to switch
    /// retention off.
    /// <para>
    /// Necessary because the two settings express contradictory intentions and the app cannot guess which
    /// wins. Retention means "do not keep history older than N days"; importing three years of Clipjump
    /// history means "keep this". Left alone, retention wins silently at the next start-up - the import
    /// reports success, and thousands of entries are gone by the next launch with nothing said. This was
    /// reported as an import that appeared not to have worked.
    /// </para>
    /// </summary>
    private void OfferToKeepImportedHistory(Import.ImportReport report, Window? owner)
    {
        if (_settings.HistoryRetentionDays <= 0 || report.OldestImported is not { } oldest)
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_settings.HistoryRetentionDays);

        if (oldest >= cutoff)
        {
            return;
        }

        var keep = MessageDialog.Confirm(
            $"Some of what was just imported is older than the {_settings.HistoryRetentionDays} days of " +
            $"history you have chosen to keep — the oldest entry is from {oldest.ToLocalTime():d}.\n\n" +
            "Those entries will be deleted the next time PasteJump starts.\n\n" +
            "Keep all history instead? This sets \"days of history to keep\" to 0, which keeps everything " +
            "for ever. You can change it back on the History tab.",
            headline: "Keep the older entries?",
            title: "PasteJump - import history",
            owner: owner);

        if (!keep)
        {
            return;
        }

        _settings.HistoryRetentionDays = 0;
        _settingsStore.Save(_settings);

        // The open settings dialog is holding the previous value and would write it back on OK, undoing this.
        _settingsWindow?.ReloadRetention(_settings.HistoryRetentionDays);
    }

    private void ExitApplication() => Shutdown();

    // ------------------------------------------------------------- lifetime

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but belongs to a session we cannot touch. Treat as "already running".
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Ordered teardown: stop taking input first, then stop capturing, then release the store.
        _recognizer?.Reset();
        _keyboardHook?.Dispose();
        _historyHotkey?.Dispose();

        _toast?.HideNow();

        // Unsubscribes from SystemEvents, which is static and would otherwise hold this alive.
        _theme?.Dispose();

        if (_clipboardMonitor is not null && _capture is not null)
        {
            _clipboardMonitor.ClipboardChanged -= _capture.OnClipboardChanged;
        }

        _clipboardMonitor?.Dispose();
        _trayIcon?.Dispose();
        _messageWindow?.Dispose();

        if (_store is not null)
        {
            _store.CollectGarbage();
            _store.Checkpoint();
            _store.Dispose();
        }

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owner - happens on the second-instance path, where we never acquired it.
            }

            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
