using System.Windows;
using PasteJump.App.Services;
using PasteJump.App.Views;
using PasteJump.Core;
using PasteJump.Core.Capture;
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

        _settingsStore = new SettingsStore(_paths);
        _settings = _settingsStore.Load();

        // Before any window is constructed, so the first one painted is already the right colour
        // rather than flashing light and then re-rendering.
        _theme = new ThemeManager(this);
        _theme.Apply(_settings.Theme);

        _store = new ClipStore(_paths);

        // Retention runs at startup rather than on a timer: this is a logon-resident app, so
        // startup happens at least daily, and a timer would be a wakeup for no user benefit.
        _store.PruneHistoryOlderThan(_settings.HistoryRetentionDays);
        _store.EvictBeyond(_settings.MaxClips);

        // After eviction, so blobs that are about to be discarded are not compressed first. Bounded
        // internally, and a no-op once the store has been converted.
        _store.CompactBlobs();

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
        _pasteHost.Paster.SetSettleDelay(_settings.PasteSettleDelayMs);
        _pasteHost.Paster.SetPasteKeystroke(_settings.PasteKeystroke);

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

        _triggerVirtualKey = TriggerKey.ToVirtualKey(TriggerKey.Normalise(_settings.PasteModeTriggerKey));

        _keyboardHook = new LowLevelKeyboardHook(OnKeyEvent);
        _keyboardHook.Install();

        _historyHotkey = new GlobalHotkey(_messageWindow);
        _historyHotkey.Pressed += ShowHistory;
        ApplyHistoryHotkey(announceFailure: true);

        _trayIcon = new TrayIcon(BuildTrayTooltip(), _messageWindow);
        _trayIcon.Activated += ShowHistory;
        _trayIcon.ContextMenuRequested += ShowTrayMenu;

        // Before Show(), so the shell receives the crisply-sized icon from the start rather than the
        // fixed 16x16 TrayIcon extracts from the executable as its fallback.
        ApplyTrayIcon();
        _trayIcon.Show();

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

        return false;
    }

    private void OnClipCaptured(Clip clip)
    {
        _historyWindow?.QueueRefresh();

        // Before the notification checks below, and independent of them: the beep exists precisely for the
        // case where the toast is off or on a monitor you are not looking at.
        if (_settings.BeepOnCopy)
        {
            CopyBeep.Play(_settings.BeepFrequencyHz);
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
            SingleLine(clip.Preview),
            TimeSpan.FromMilliseconds(_settings.CopyNotificationMs));
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
        // Greyed while disabled, which is Windows' own convention for an inactive icon and the only signal
        // visible without hovering for the tooltip. Worth having precisely because Disable is not persisted:
        // the state has to be obvious at a glance or it is easy to forget the app is switched off.
        var name = _keyboardHook is { IsInstalled: false }
            ? "pastejump-disabled.ico"
            : "pastejump.ico";

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
                _store, _clipboard, _selfWrites, _formatters, _settings.GridDensity));
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

    private void ShowShortcutHelp()
    {
        if (_helpWindow is null)
        {
            _helpWindow = Themed(new ShortcutHelpWindow(
                TriggerKey.Normalise(_settings.PasteModeTriggerKey)));
            _helpWindow.Closed += (_, _) => _helpWindow = null;
            _helpWindow.Show();
        }
        else
        {
            _helpWindow.Activate();
        }
    }

    private void OnSettingsApplied(PasteJumpSettings updated)
    {
        _settings = updated;
        _settingsStore.Save(_settings);

        _theme.Apply(_settings.Theme);
        _pasteHost.Paster.SetSettleDelay(_settings.PasteSettleDelayMs);
        _pasteHost.Paster.SetPasteKeystroke(_settings.PasteKeystroke);

        _triggerVirtualKey = TriggerKey.ToVirtualKey(TriggerKey.Normalise(_settings.PasteModeTriggerKey));
        ApplyHistoryHotkey(announceFailure: true);

        // The help window lists the trigger key by name, so a change to it makes an open copy wrong.
        _helpWindow?.Close();

        // An open history window follows the new density rather than needing to be reopened.
        _historyWindow?.ApplyDensity(_settings.GridDensity);

        StartupShortcut.Apply(_settings.RunAtLogon);

        _store.PruneHistoryOlderThan(_settings.HistoryRetentionDays);
        _store.EvictBeyond(_settings.MaxClips);

        // Paste-mode options are captured at construction, so the controller is rebuilt rather
        // than mutated. Cheap, and it avoids a half-applied configuration.
        _controller = new PasteModeController(
            new ClipStoreCatalog(_store),
            _pasteHost,
            _formatters,
            _settings.PasteModeOptions);

        _recognizer = new PasteGestureRecognizer(_controller);
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
        var text = $"PasteJump {AppVersion.Display}";

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

    private void RunLegacyImport(string candidate, Window? owner)
    {
        try
        {
            var report = Import.LegacyClipjumpImporter.ImportHistory(candidate, _store);

            MessageDialog.Show(
                $"Imported {report.Imported} entries.\nSkipped {report.Skipped}." +
                (report.Errors.Count > 0 ? $"\n\nProblems:\n{string.Join('\n', report.Errors.Take(5))}" : string.Empty),
                headline: "Import complete",
                title: "PasteJump - import history",
                owner: owner);

            _historyWindow?.QueueRefresh();
        }
        catch (Exception ex)
        {
            // Reading someone else's database can fail in ways we do not control - a schema we do not
            // recognise, a file held open by a running Clipjump. Reported rather than allowed to reach the
            // dispatcher's unhandled handler and take the app down.
            MessageDialog.Warn(ex.Message, headline: "Import failed", owner: owner);
        }
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
