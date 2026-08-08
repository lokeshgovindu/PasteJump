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
    private TrayIcon _trayIcon = null!;

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
            MessageBox.Show(
                $"PasteJump could not start.\n\n{ex.Message}",
                "PasteJump",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

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

        _keyboardHook = new LowLevelKeyboardHook(OnKeyEvent);
        _keyboardHook.Install();

        _trayIcon = new TrayIcon(BuildTrayTooltip(), _messageWindow);
        _trayIcon.Activated += ShowHistory;
        _trayIcon.ContextMenuRequested += ShowTrayMenu;

        // Before Show(), so the shell receives the crisply-sized icon from the start rather than the
        // fixed 16x16 TrayIcon extracts from the executable as its fallback.
        ApplyTrayIcon();
        _trayIcon.Show();

        MaybeOfferLegacyImport();
        MaybeOfferShiftInsert();
    }

    /// <summary>
    /// Offers Shift+Insert when another clipboard manager is running and we are still sending Ctrl+V.
    /// <para>
    /// A prompt rather than a silent switch. Shift+Insert is not universally identical to Ctrl+V - a few
    /// applications bind it to something else, and terminals historically used it for the X-style primary
    /// selection - so changing the paste chord behind the user's back would trade one confusing failure
    /// for another.
    /// </para>
    /// <para>
    /// Asked at every start-up while the conflict persists, not once and then remembered. The app is
    /// genuinely unable to paste in this state, and a one-time notice dismissed months ago is no help to
    /// someone who has just installed the other manager. Accepting the offer settles it permanently,
    /// since the condition includes the chord still being Ctrl+V.
    /// </para>
    /// </summary>
    private void MaybeOfferShiftInsert()
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

        var answer = MessageBox.Show(
            RivalClipboardManagers.DescribeConflict(rivals),
            "PasteJump - another clipboard manager is running",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _settings.PasteKeystroke = PasteKeystroke.ShiftInsert;
        _settingsStore.Save(_settings);
        _pasteHost.Paster.SetPasteKeystroke(_settings.PasteKeystroke);
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
            MessageBox.Show(
                "PasteJump could not finish moving its data.\n\n" +
                string.Join("\n\n", problems) +
                "\n\nNothing was removed from the old location.",
                "PasteJump",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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

        MessageBox.Show(
            message + "\n\nOpen Settings and store that data in your user profile instead, or move " +
            "PasteJump to a folder you can write to.",
            "PasteJump",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
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

        var key = VirtualKeyTranslator.ToGestureKey(e.VirtualKey);

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
        // Through AppPaths, so this resolves off Environment.ProcessPath like every other path in the
        // app. AppContext.BaseDirectory would look correct and then break under a single-file publish,
        // where it points at the extraction directory rather than the folder holding the exe.
        => _trayIcon.SetIconFromFile(Path.Combine(_paths.AssetsDirectory, "pastejump.ico"));

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
            onRestart: RestartFromMenu,
            onExit: ExitApplication,
            isPaused: !_settings.MonitorClipboard);

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

    private void ShowShortcutHelp()
    {
        if (_helpWindow is null)
        {
            _helpWindow = Themed(new ShortcutHelpWindow());
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

        var answer = MessageBox.Show(
            "PasteJump will restart and copy to the new location:\n\n" +
            string.Join("\n", moves) +
            "\n\nThe existing copy is left where it is. Delete it yourself once you are happy the move " +
            "worked.",
            "PasteJump - move data",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
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
            MessageBox.Show(
                "PasteJump could not save the new data location. The folder holding PasteJump.exe is " +
                "not writable, so the choice would not survive a restart.\n\nNothing was changed.",
                "PasteJump",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

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
            MessageBox.Show(
                "PasteJump could not work out its own path to restart. Close and reopen it to finish " +
                "moving the data.",
                "PasteJump",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

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
    /// Tray tooltip: name, version, and the paused state when it applies.
    /// <para>
    /// Built in one place so the version cannot go missing from the paused variant - which is what
    /// happened when the two strings were written out inline at their call sites.
    /// </para>
    /// </summary>
    private string BuildTrayTooltip()
    {
        var text = $"PasteJump {AppVersion.Display}";

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

    private void OnClipEditorRequested(Clip clip)
    {
        try
        {
            var payloads = _store.GetPayloads(clip.Id);
            var text = Win32ClipboardAccess.ExtractText(payloads);

            if (text is null)
            {
                MessageBox.Show("This clip has no text to edit.", "PasteJump");
                return;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"pastejump-{clip.Id}.txt");
            File.WriteAllText(tempPath, text);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _settings.TextEditor,
                Arguments = $"\"{tempPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the editor.\n\n{ex.Message}", "PasteJump");
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
                    MessageBox.Show("This image could not be exported.", "PasteJump");
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
            MessageBox.Show($"Export failed.\n\n{ex.Message}", "PasteJump");
        }
    }

    private void MaybeOfferLegacyImport()
    {
        if (_settings.LegacyImportCompleted)
        {
            return;
        }

        var candidate = Import.LegacyClipjumpLocator.FindLikelyInstallation();

        if (candidate is null)
        {
            // Nothing found, so do not ask again on every launch.
            _settings.LegacyImportCompleted = true;
            _settingsStore.Save(_settings);
            return;
        }

        var answer = MessageBox.Show(
            $"An existing Clipjump installation was found at:\n\n{candidate}\n\n" +
            "Import its clipboard history into PasteJump?\n\n" +
            "Only history is imported. Clip stacks are left alone, and nothing in the " +
            "Clipjump folder is modified.",
            "PasteJump - import history",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            var report = Import.LegacyClipjumpImporter.ImportHistory(candidate, _store);

            MessageBox.Show(
                $"Imported {report.Imported} entries.\n" +
                $"Skipped {report.Skipped}.\n" +
                (report.Errors.Count > 0 ? $"\nProblems:\n{string.Join('\n', report.Errors.Take(5))}" : string.Empty),
                "PasteJump - import complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            _historyWindow?.QueueRefresh();
        }

        _settings.LegacyImportCompleted = true;
        _settingsStore.Save(_settings);
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
