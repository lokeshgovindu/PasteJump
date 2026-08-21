using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
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
using PasteJump.Core.Theming;
using PasteJump.Core.Storage;
using PasteJump.Core.Updates;
using PasteJump.Interop;

namespace PasteJump.App;

/// <summary>
/// Application bootstrap. Composition happens here by hand rather than through a DI container:
/// there are about a dozen objects with a fixed lifetime and no configuration-time variation, so a
/// container would add indirection without removing any.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Name of the mutex that makes this a single-instance application.
    /// <para>
    /// <c>Local\</c>, which means once per logon session, not once per machine. It was <c>Global\</c>, and that
    /// was wrong: a global mutex is shared across every Terminal Services session, so a second user signing in
    /// - by fast user switching, or while the first session is merely disconnected and still running - found
    /// PasteJump refusing to start with no explanation, permanently. Nothing about the app is machine-wide:
    /// each session has its own clipboard, its own keyboard hook and its own data folder, so two users' copies
    /// cannot conflict.
    /// </para>
    /// <para>
    /// Keep this in step with <c>AppMutex</c> in <c>packaging/PasteJump.iss</c>, which is how setup detects a
    /// running copy instead of failing on a locked executable. A bare name there is session-local, matching.
    /// </para>
    /// </summary>
    private const string SingleInstanceMutexName = @"Local\PasteJump.SingleInstance.9F2C41A6";

    private Mutex? _singleInstanceMutex;

    private AppPaths _paths = null!;
    private SettingsStore _settingsStore = null!;
    private PasteJumpSettings _settings = null!;
    private ClipStore _store = null!;

    private MessageOnlyWindow _messageWindow = null!;
    private ClipboardMonitor _clipboardMonitor = null!;
    private Win32ClipboardAccess _clipboard = null!;
    private LowLevelKeyboardHook _keyboardHook = null!;

    /// <summary>
    /// Whether the user has switched the gesture off from the tray. Distinct from whether the hook is installed:
    /// Windows can drop the hook on its own, and the watchdog must be able to tell "gone by accident" from "gone
    /// because you asked".
    /// </summary>
    private bool _gestureDisabled;

    private long _lastHookEventAt = Stopwatch.GetTimestamp();
    private long? _ctrlDownSince;

    private long? _ctrlUpSince;

    /// <summary>
    /// The command line, kept because <see cref="Compose"/> needs it and only <c>OnStartup</c> is given it.
    /// </summary>
    private string[] _startupArguments = [];

    private readonly KeyRepeatFilter _traceRepeats = new();
    private bool _announcedHookRecovery;

    /// <summary>
    /// Watches for the hook going deaf in one application while working in the others. Not a health problem and
    /// not fixable here - see the type - but silent until this existed, which made PasteJump look broken.
    /// </summary>
    private readonly ForegroundDeafnessTracker _deafness = new();
    private GlobalHotkey _historyHotkey = null!;
    private TrayIcon _trayIcon = null!;

    /// <summary>
    /// Virtual key of the configured paste-mode trigger, resolved once per settings change rather than on
    /// every keystroke. <see cref="OnKeyEvent"/> runs inside the hook callback, which blocks all keyboard
    /// input machine-wide, so it does no parsing.
    /// </summary>
    private int _triggerVirtualKey = TriggerKey.ToVirtualKey(TriggerKey.Default);

    /// <summary>
    /// The configured letter bindings, parsed once per settings change for the same reason as
    /// <see cref="_triggerVirtualKey"/>: <see cref="OnKeyEvent"/> runs in the hook callback and must not parse
    /// anything. Lookup inside it is one array index - see <see cref="PasteKeyMap"/>.
    /// </summary>
    private PasteKeyMap _keyMap = PasteKeyMap.Default;

    /// <summary>Identifies the window being pasted into. Used for the per-application paste delay.</summary>
    private ForegroundWindowInfo _foreground = new();

    private SelfWriteGuard _selfWrites = null!;
    private FormatterRegistry _formatters = null!;
    private PasteModeController _controller = null!;
    private PasteGestureRecognizer _recognizer = null!;
    private PasteJumpPasteHost _pasteHost = null!;
    private CaptureService _capture = null!;
    private CaptureTraceLog _captureTrace = null!;
    private GestureTraceLog _gestureTrace = null!;

    private IntPtr _lastForegroundHwnd;

    private long _lastForegroundSince;

    private string _lastForegroundName = "(none)";

    private long _keysHeard;

    private long _keysHeardAtForegroundChange;
    private long _otherKeysSeen;

    private ThemeManager _theme = null!;

    /// <summary>
    /// The themes on offer beyond Light, Dark and System - shipped and user-authored. Read once at start-up and
    /// again whenever the settings dialog opens, so a file added while the app was running needs no restart.
    /// </summary>
    private ThemeCatalog _themes = null!;

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

        _startupArguments = e.Args;

        // Before the mutex, which is the entire point: an elevated restart starts this copy while the old one
        // is still running and still holding it, because the UAC prompt can be refused and shutting down first
        // would leave the user with nothing. Without the wait we would find the mutex held, conclude we were a
        // second launch, surface the old copy and exit - which looks exactly like the restart doing nothing.
        WaitForTheCopyWeAreReplacing(e.Args);

        if (!TryAcquireSingleInstance())
        {
            // A second copy must not keep running: it would install a second keyboard hook and fight the
            // first over the clipboard and the database. But it should not vanish without a word either -
            // PasteJump has no window and its tray icon is often hidden in the notification-area overflow,
            // so a launch that does nothing at all is indistinguishable from a crash.
            //
            // So ask the running instance to show its history window, and exit. Only if it cannot be reached
            // do we say anything - which, now that the mutex is session-local, means the other copy is running
            // elevated: UIPI blocks a post from a lower integrity level, so it can be found and not spoken to.
            if (!SingleInstanceSignal.TryNotifyRunningInstance())
            {
                MessageDialog.Show(
                    "PasteJump is already running, but this copy cannot reach it - which usually means the "
                        + "other one was started as administrator.\n\nLook for the PasteJump icon in the "
                        + "notification area, next to the clock.",
                    headline: "PasteJump is already running",
                    kind: DialogKind.Information);
            }

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

    /// <summary>
    /// Waits for the instance this one was started to replace, when it was started that way.
    /// </summary>
    /// <remarks>
    /// Bounded by <see cref="RelaunchRequest.MaxWait"/>, and carrying on regardless when it expires: the
    /// predecessor may already be gone, or may be wedged, and a replacement that never appears is worse than
    /// the mutex collision this avoids - which has a sane outcome of its own.
    /// <para>
    /// Every failure here is silent and harmless. The process may have exited between being named and being
    /// looked up (<see cref="ArgumentException"/>), or be one we cannot open, and in both cases there is
    /// nothing to wait for. Waiting for our own id is refused rather than deadlocking on ourselves.
    /// </para>
    /// </remarks>
    private static void WaitForTheCopyWeAreReplacing(string[]? arguments)
    {
        if (RelaunchRequest.TryParseReplacedProcessId(arguments) is not { } processId
            || processId == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(processId);

            DebugConsole.Log($"  waiting up to {RelaunchRequest.MaxWait.TotalSeconds:0}s for pid {processId} to exit");

            if (!previous.WaitForExit(RelaunchRequest.MaxWait))
            {
                DebugConsole.Log("  it did not exit in time - starting anyway");
            }
        }
        catch (ArgumentException)
        {
            // Already gone, which is the happy case.
        }
        catch (Exception ex)
        {
            DebugConsole.Log($"  could not wait for pid {processId}: {ex.GetType().Name}");
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
        // The catalogue is read before the first Apply, so a theme chosen last session is in force from the first
        // window rather than after the settings dialog has been opened once.
        _themes = new ThemeCatalog(_paths);
        _themes.Refresh();

        _theme = new ThemeManager(this, _themes);
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

        // Field, not a local: OnSettingsApplied re-resolves the per-app paste delays and needs the same
        // provider. Stateless, so one instance for the process is right.
        _foreground = new ForegroundWindowInfo();
        var foreground = _foreground;
        _clipboard = new Win32ClipboardAccess(foreground);
        _selfWrites = new SelfWriteGuard();
        _formatters = new FormatterRegistry();

        // Named, so a second instance can find it with FindWindowEx and ask us to surface ourselves. The name
        // has to be the window title rather than the class, whose name is unique per instance by design.
        _messageWindow = new MessageOnlyWindow(windowName: SingleInstanceSignal.WindowName);

        _messageWindow.MessageReceived += OnMessageWindowMessage;

        // Without this an ELEVATED PasteJump is unreachable: Windows blocks messages sent up an integrity
        // boundary, so a second launch could not ask it to show itself and the deployment script could not ask
        // it to exit - both would silently do nothing. Called unconditionally because it is harmless when not
        // elevated, and a privilege check here is one more thing to get wrong.
        SingleInstanceSignal.AllowRequestsFromLowerIntegrity(_messageWindow.Handle);
        _clipboardMonitor = new ClipboardMonitor(_messageWindow);

        _pasteHost = new PasteJumpPasteHost(
            _store,
            _clipboard,
            new InputSender(),
            _selfWrites,
            Dispatcher,
            () => new OverlayWindow(),
            trace: message => _captureTrace.Write(message));

        _pasteHost.TagEditorRequested += OnTagEditorRequested;
        _pasteHost.ClipEditorRequested += OnClipEditorRequested;
        _pasteHost.ExportRequested += OnExportRequested;
        _pasteHost.HelpRequested += ShowShortcutHelp;
        _pasteHost.HistoryRequested += ShowHistory;
        _pasteHost.TransientMessage += OnTransientMessage;
        _pasteHost.DeleteAllConfirmationRequested += OnDeleteAllConfirmationRequested;
        _pasteHost.Paster.SetSettleDelay(_settings.PasteSettleDelayMs);
        _pasteHost.Paster.SetPerAppSettleDelays(PerAppSettleDelays.Parse(_settings.PasteSettleDelayPerApp), foreground);
        _pasteHost.Paster.SetPasteKeystroke(_settings.PasteKeystroke);
        _pasteHost.SetPreviewSize(_settings.OverlayPreviewMaxWidth, _settings.OverlayPreviewMaxHeight);
        _pasteHost.SetOverlayFont(_settings.OverlayFontFamily, _settings.OverlayFontSize);
        _pasteHost.SetJoinSeparator(_settings.ClipJoinSeparator);
        _pasteHost.SetOverlayParts(_settings.OverlayParts);
        _pasteHost.SetDeletedFlash(_settings.OverlayDeletedFlashMs);
        _pasteHost.SetOverlayAnchor(_settings.OverlayX, _settings.OverlayY, _settings.OverlayPosition);
        _pasteHost.SetKeyHint(
            _settings.ShowOverlayKeyHint,
            TriggerKey.Normalise(_settings.PasteModeTriggerKey),
            PasteKeyMap.Parse(_settings.PasteModeKeys));

        _controller = new PasteModeController(
            new ClipStoreCatalog(_store),
            _pasteHost,
            _formatters,
            _settings.PasteModeOptions);

        _recognizer = new PasteGestureRecognizer(_controller);

        // Beside the database, so it follows a custom data folder rather than living wherever the exe is.
        _captureTrace = new CaptureTraceLog(_paths.ClipsDirectory);
        _captureTrace.Write($"---- PasteJump {AppVersion.Current} started, settle={_settings.ClipboardSettleMs}ms ----");

        _gestureTrace = new GestureTraceLog(_paths.ClipsDirectory);
        _gestureTrace.Note(
            $"---- PasteJump {AppVersion.Current} started, trigger={_settings.PasteModeTriggerKey} ----");

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
            },

            // Every decision, one line each, into logs\capture.log beside the database. On by default and
            // metadata only - see CaptureTraceLog for why it exists and what it deliberately does not record.
            trace: _captureTrace.Write);

        _capture.ClipCaptured += OnClipCaptured;
        _capture.CaptureObserved += OnDuplicateCaptureObserved;
        _capture.Prime();

        _clipboardMonitor.ClipboardChanged += _capture.OnClipboardChanged;
        _clipboardMonitor.Start();

        StartupTrace.Mark("services, capture and clipboard monitor");

        _triggerVirtualKey = TriggerKey.ToVirtualKey(TriggerKey.Normalise(_settings.PasteModeTriggerKey));
        _keyMap = PasteKeyMap.Parse(_settings.PasteModeKeys);

        _keyboardHook = new LowLevelKeyboardHook(OnKeyEvent);
        _keyboardHook.Install();

        // After the hook, so its first tick cannot see a hook that does not exist yet and conclude we are deaf.
        StartHookWatchdog();

        _historyHotkey = new GlobalHotkey(_messageWindow);
        _historyHotkey.Pressed += ShowHistory;
        ApplyHistoryHotkey(announceFailure: true);

        StartupTrace.Mark("keyboard hook and hotkey");

        _trayIcon = new TrayIcon(BuildTrayTooltip(), _messageWindow);
        _trayIcon.Activated += OnTrayLeftClick;
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

                // Deferred with the other start-up prompts, and for the same reason: this can show a dialog on
                // failure, and anything modal inside Compose owns the UI thread with its own message loop -
                // which does not drain the Dispatcher, so every side effect the paste host queues would sit
                // unprocessed and the gesture would look dead for as long as it was up.
                if (RelaunchRequest.WantsElevatedLogonTask(_startupArguments) && IsRunningElevated())
                {
                    EnableElevatedLogonTask();
                }

                // Last, and at idle, so it delays nothing the user can see. A tray-only application shows no
                // window at startup, which leaves WPF's window stack cold until the first click - and that
                // first Window.Show() measured 1.1-1.4 SECONDS, most of the delay behind "the tray menu feels
                // slow". Paying it here costs nobody anything: the tray icon is already up.
                WpfWarmUp.Run();
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
    /// <summary>
    /// Watches for Windows having silently dropped the keyboard hook, and puts it back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reported 2026-08-20: at 100% CPU, a paste into the Run dialog left the overlay stuck, and from then on
    /// Ctrl+V pasted straight through with no overlay. That is what a discarded hook looks like from outside -
    /// Windows drops a hook whose callback exceeded <c>LowLevelHooksTimeout</c> and notifies nobody - and the only
    /// recovery was restarting the application.
    /// </para>
    /// <para>
    /// A quarter of a second, which is far more often than the failure happens and far cheaper than it sounds:
    /// each tick is one <c>GetAsyncKeyState</c> and some arithmetic, and it does nothing at all unless the
    /// evidence says something is wrong. The decision itself lives in <see cref="HookHealthPolicy"/> so the
    /// thresholds are testable; this only gathers the inputs and carries out the verdict.
    /// </para>
    /// </remarks>
    private void StartHookWatchdog()
    {
        var watchdog = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };

        watchdog.Tick += (_, _) => CheckHookHealth();
        watchdog.Start();
    }

    /// <summary>
    /// Records a change of foreground window, with how many keys the hook heard while the previous one held it.
    /// </summary>
    /// <remarks>
    /// Driven from the watchdog timer rather than from key events, and that is the entire point. "The overlay does
    /// not appear in application X" has two very different causes that the key lines alone cannot separate: the
    /// application was never in the foreground when the keys were pressed, or it was and <b>the hook heard
    /// nothing</b> - which is what a hook earlier in the chain suppressing our chord looks like, and is not our
    /// bug. A line written only when a key arrives cannot distinguish them, because the silent case produces no
    /// key to write it from.
    /// <para>
    /// <c>GetForegroundWindow</c> is a bare user32 call and is compared by handle; the process name is resolved
    /// only when the handle changes, because that opens a process handle and this runs four times a second.
    /// </para>
    /// </remarks>
    private void NoteForegroundChange()
    {
        var hwnd = ForegroundWindowInfo.GetForegroundWindowHandle();

        if (hwnd == _lastForegroundHwnd)
        {
            return;
        }

        var heldFor = _lastForegroundHwnd == IntPtr.Zero
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(_lastForegroundSince);

        var keysHeard = _keysHeard - _keysHeardAtForegroundChange;

        _gestureTrace.Note(
            $"focus: {ForegroundWindowInfo.DescribeForegroundForTrace()}"
            + (_lastForegroundHwnd == IntPtr.Zero
                ? string.Empty
                : $" | previous={_lastForegroundName} held it {heldFor.TotalSeconds:F1}s, hook heard {keysHeard} key(s)"));

        // The same two numbers the line above reports, handed to the thing that can draw a conclusion from
        // them. A name in brackets is one of the placeholders GetForegroundProcessNameForTrace returns when
        // there is no foreground window or it could not be read; it is not an application.
        if (_lastForegroundHwnd != IntPtr.Zero && !_lastForegroundName.StartsWith('('))
        {
            // Clamped rather than cast: the counter is a long because it runs for the life of the process,
            // while one focus spell cannot plausibly overflow an int - and the tracker only compares it to zero.
            _deafness.NoteFocusSpell(_lastForegroundName, heldFor, (int)Math.Min(int.MaxValue, keysHeard));
        }

        _lastForegroundHwnd = hwnd;
        _lastForegroundSince = Stopwatch.GetTimestamp();
        _lastForegroundName = ForegroundWindowInfo.GetForegroundProcessNameForTrace();
        _keysHeardAtForegroundChange = _keysHeard;
    }

    /// <summary>
    /// Says once, per application, that the hook is hearing nothing there while hearing everything elsewhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a repair, because there is nothing to repair: measured on a managed machine, four independent
    /// mechanisms were equally deaf while one application held the foreground - the low-level hook, raw input
    /// with <c>RIDEV_INPUTSINK</c>, <c>RegisterHotKey</c> and even <c>GetLastInputInfo</c> - while
    /// <c>SendInput</c> reported success for every event it injected. Nothing in user mode can see the keyboard
    /// there, so this exists purely so the user is told rather than left concluding that PasteJump is broken.
    /// </para>
    /// <para>
    /// A toast rather than a dialog, and once per application per run, for the same reason
    /// <c>HintAboutRivalManagers</c> is: the conclusion is a guess from ambiguous evidence, and a guess that
    /// blocks is worse than the fault it describes. <c>ForegroundDeafnessTracker.Describe</c> owns the wording,
    /// hedge included.
    /// </para>
    /// <para>
    /// Note the <b>watchdog cannot see this failure</b> and never could: <c>HookHealthPolicy</c> asks whether
    /// anything has been heard since Ctrl went down, and keys from every other application keep answering yes.
    /// A hook deaf to one application looks perfectly healthy to it, indefinitely - which is exactly why this
    /// is a separate rule rather than another branch of that one.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Whether this process is elevated. Never throws: the notice is worth showing even if the token cannot be
    /// read, and a wrong answer only changes which remedy is suggested.
    /// </summary>
    private static bool IsRunningElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();

            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ReportFilteredKeyboardOnce()
    {
        if (!_settings.WarnAboutFilteredKeyboard || _gestureDisabled)
        {
            return;
        }

        if (_deafness.TryClaimNotice() is not { } deaf)
        {
            return;
        }

        // Whether we are elevated decides whether there is a remedy to offer - see Describe. Read here rather
        // than cached: it cannot change within a run, but reading it at the one place it is used keeps the fact
        // next to the decision it informs.
        var elevated = IsRunningElevated();
        var detail = ForegroundDeafnessTracker.Describe(deaf, elevated);

        // Into the gesture log as well as the screen: the toast is transient, and this is precisely the finding
        // somebody will want to read back an hour later when they report it.
        _gestureTrace.Note(
            $"KEYBOARD FILTERED in {deaf.Application}: {deaf.FocusedFor.TotalSeconds:F0}s of foreground over "
            + $"{deaf.Spells} spell(s) with nothing heard, corroboratedByACopy={deaf.Corroborated}, "
            + $"applications heard from={_deafness.ApplicationsHeard}, elevated={elevated}");

        // Prose, so the detail line is not set in the font a clip preview wants - the same reason the
        // single-instance toast passes detailIsProse.
        // Bottom-right, like the second-launch toast: this is a message about the application itself, and that
        // is where Windows puts its own, so it is where people already look for one.
        Toast().Notify(
            $"PasteJump cannot see the keyboard in {deaf.Application}",
            detail,
            TimeSpan.FromSeconds(12),
            ToastPlacement.BottomRight,
            detailIsProse: true);
    }

    private void CheckHookHealth()
    {
        NoteForegroundChange();
        ReportFilteredKeyboardOnce();

        var ctrlHeld = VirtualKeyTranslator.IsCtrlDown();

        // Tracked here rather than from key events on purpose: the whole question is what happens when key events
        // stop arriving, so the only usable clock is one that does not depend on them.
        if (ctrlHeld)
        {
            _ctrlDownSince ??= Stopwatch.GetTimestamp();
            _ctrlUpSince = null;
        }
        else
        {
            _ctrlDownSince = null;
            _ctrlUpSince ??= Stopwatch.GetTimestamp();
        }

        var decision = HookHealthPolicy.Decide(
            gestureEnabled: !_gestureDisabled,
            hookInstalled: _keyboardHook.IsInstalled,
            sessionActive: _recognizer.IsSessionActive,
            ctrlHeld: ctrlHeld,
            msSinceLastHookEvent: Stopwatch.GetElapsedTime(_lastHookEventAt).TotalMilliseconds,
            msCtrlHeldFor: _ctrlDownSince is { } since
                ? Stopwatch.GetElapsedTime(since).TotalMilliseconds
                : 0,

            // Zero while Ctrl is held. Not "infinity when unknown": at start-up, with no session open, the
            // stranded rule cannot fire anyway, and claiming Ctrl had been up for ever would make the first
            // tick after a commit the very false positive this exists to stop.
            msCtrlUpFor: _ctrlUpSince is { } up
                ? Stopwatch.GetElapsedTime(up).TotalMilliseconds
                : 0);

        if (!decision.AnythingToDo)
        {
            return;
        }

        if (decision.AbandonStuckSession)
        {
            // Reset rather than a bare Abort: it also clears the tracked modifier flags, which a session stranded
            // by missing key events is guaranteed to have left wrong. Restores the clipboard and takes the overlay
            // down, which is the visible half of the recovery.
            _recognizer.Reset();
        }

        if (decision.ReinstallHook)
        {
            try
            {
                _keyboardHook.Reinstall();

                // Every event in the gap the reinstall closes was lost, so a key held across it must not
                // have its next press read as auto-repeat and swallowed from the trace.
                _traceRepeats.Reset();
            }
            catch (InvalidOperationException ex)
            {
                // SetWindowsHookEx refused. Nothing useful to do about it here, and throwing from a timer tick
                // would take the application down over a diagnosis that may itself have been wrong.
                _captureTrace.Write($"keyboard hook could not be reinstalled: {ex.Message}");
                return;
            }
        }

        NoteHookEvent();

        _captureTrace.Write(
            $"keyboard hook watchdog: reinstalled={decision.ReinstallHook} "
            + $"abandonedStuckSession={decision.AbandonStuckSession} "
            + $"(reinstalls so far {_keyboardHook.ReinstallCount})");

        // Said once per run, not once per recovery. Under sustained load this can fire repeatedly, and a toast on
        // every one would be worse than the fault; but saying nothing the first time leaves the user believing
        // Ctrl+V simply broke, which is the reading that sends somebody hunting for a bug that is not there.
        if (!_announcedHookRecovery && decision.ReinstallHook)
        {
            _announcedHookRecovery = true;

            Toast().Notify(
                "PasteJump reconnected its keyboard",
                "Windows had stopped sending it keystrokes, which happens when the machine is very busy. "
                    + "Ctrl+V works again.",
                TimeSpan.FromSeconds(6),
                ToastPlacement.BottomRight,
                detailIsProse: true);
        }
    }

    /// <summary>Records that the hook is alive, which is the clock the watchdog measures silence against.</summary>
    private void NoteHookEvent()
    {
        _lastHookEventAt = Stopwatch.GetTimestamp();
    }

    /// <summary>Names the trigger and the modifiers for the gesture trace; nothing else, ever.</summary>
    private string DescribeKeyForTrace(int virtualKey)
    {
        if (virtualKey == _triggerVirtualKey)
        {
            return $"TRIGGER(0x{virtualKey:X2})";
        }

        return virtualKey switch
        {
            0x10 or 0xA0 or 0xA1 => "Shift",
            0x11 or 0xA2 or 0xA3 => "Ctrl",
            0x12 or 0xA4 or 0xA5 => "Alt",
            0x5B or 0x5C => "Win",
            _ => "modifier",
        };
    }

    /// <summary>
    /// The foreground process name, which is the whole point of the exercise - "the overlay does not appear in
    /// THIS application" is unanswerable without it - and the reason it is only asked for named keys: it opens a
    /// process handle, which is far too much work to do on every keystroke machine-wide inside a hook callback.
    /// </summary>
    private static string ForegroundNameForTrace()
    {
        try
        {
            return new ForegroundWindowInfo().GetForegroundProcessName() ?? "(none)";
        }
        catch (Exception ex)
        {
            return "(failed: " + ex.GetType().Name + ")";
        }
    }

    private bool OnKeyEvent(KeyboardHookEvent e)
    {
        NoteHookEvent();

        // Counted for the focus lines, which report how many keys the hook heard while each window held the
        // foreground. A count is all that is kept: the keys themselves are never identified unless they are the
        // trigger or a modifier.
        _keysHeard++;

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

        // Read live rather than tracked from transitions, because a missed key-up leaves a tracked flag stuck. For
        // Alt that only refuses the gesture; for Ctrl it does the opposite and opens one on an unmodified key, which
        // is why all four are read here. Four cheap user-mode reads per keystroke; the hook's budget is
        // LowLevelHooksTimeout, which this is nowhere near.
        // Ctrl first, because it is the one the entry test reads: everything below only ever refuses the gesture,
        // while a stuck Ctrl offers it on a bare keystroke.
        _recognizer.CtrlHeld = VirtualKeyTranslator.IsCtrlDown();
        _recognizer.AltHeld = VirtualKeyTranslator.IsAltDown();
        _recognizer.WinHeld = VirtualKeyTranslator.IsWinDown();
        _recognizer.ShiftHeld = VirtualKeyTranslator.IsShiftDown();

        var key = VirtualKeyTranslator.ToGestureKey(e.VirtualKey, _triggerVirtualKey, _keyMap);

        // Named keys are the trigger and the modifiers; everything else is counted, never identified, so this
        // cannot reconstruct what was typed. See GestureTraceLog for why that is structural rather than a promise.
        var isNamed = e.VirtualKey == _triggerVirtualKey || VirtualKeyTranslator.IsModifier(e.VirtualKey);

        // Windows auto-repeats a held key, so holding Ctrl to read the overlay wrote a line every ~30 ms - two,
        // counting the verdict beneath it - and buried the events that matter. The first press is written and the
        // release carries how many repeats were swallowed. Note this collapses the LOG only: the recognizer below
        // still receives every event, because a repeated trigger key genuinely steps to another clip.
        var repeats = 0;
        var writeTrace = isNamed && _traceRepeats.ShouldWrite(e.VirtualKey, e.IsKeyDown, out repeats);

        if (writeTrace)
        {
            var wasActive = _recognizer.IsSessionActive;

            _gestureTrace.Note(
                $"key={DescribeKeyForTrace(e.VirtualKey)} {(e.IsKeyDown ? "down" : "up  ")} " +
                $"gesture={key} live[ctrl={(_recognizer.CtrlHeld ? 1 : 0)} alt={(_recognizer.AltHeld ? 1 : 0)} " +
                $"win={(_recognizer.WinHeld ? 1 : 0)} shift={(_recognizer.ShiftHeld ? 1 : 0)}] " +
                $"sessionBefore={wasActive} fg={ForegroundNameForTrace()} otherKeysSoFar={_otherKeysSeen}"
                + (repeats > 0 ? $" heldFor={repeats} repeat(s)" : string.Empty));
        }
        else if (!isNamed && e.IsKeyDown)
        {
            _otherKeysSeen++;
        }

        if (key != GestureKey.None && _recognizer.Handle(key, e.IsKeyDown))
        {
            if (writeTrace)
            {
                _gestureTrace.Note($"   -> recognizer HANDLED it (swallowed). sessionNow={_recognizer.IsSessionActive}");
            }

            return true;
        }

        if (writeTrace)
        {
            _gestureTrace.Note($"   -> recognizer declined. sessionNow={_recognizer.IsSessionActive}");
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
        if (!VirtualKeyTranslator.IsModifier(e.VirtualKey) && _recognizer.ShouldSwallowUnhandled())
        {
            return true;
        }

        return false;
    }

    private void OnClipCaptured(Clip clip)
    {
        _historyWindow?.QueueRefresh();

        // A copy in an application we have never heard a key from is the strongest evidence available that the
        // keyboard is being filtered there rather than merely idle: capture rides WM_CLIPBOARDUPDATE, which no
        // hook can suppress, so this arrives even when every keystroke is taken. See ForegroundDeafnessTracker.
        _deafness.NoteClipboardActivity(clip.SourceExecutable);

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
            TimeSpan.FromMilliseconds(_settings.CopyNotificationMs),
            CopyNotificationAnchor());
    }

    /// <summary>
    /// Where the copy notification goes, resolved the same way the overlay's position is.
    /// </summary>
    /// <remarks>
    /// Read at the moment of the copy rather than cached, for the reason the paste settle delay is: the caret, the
    /// window in front and the pointer are all different by the next copy, and that is the whole point of asking.
    /// </remarks>
    private OverlayAnchor CopyNotificationAnchor()
    {
        var pinned = _settings.OverlayX is { } x && _settings.OverlayY is { } y
            ? (x, y)
            : ((int X, int Y)?)null;

        return ForegroundWindowInfo.GetPreferredOverlayAnchor(_settings.CopyNotificationPosition, pinned);
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
    /// If an icon cannot be read, <c>TrayIcon</c> keeps the one it extracted from the executable - no tray
    /// icon at all would leave the app running with no reachable menu, which is the worst failure it has.
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
            _ when _gestureDisabled => TrayIconArt.Disabled,
            _ when !_settings.MonitorClipboard => TrayIconArt.Paused,
            _ => TrayIconArt.Normal,
        };

        // Embedded, not a path. These were loose files beside the exe until 2026-08-12, which meant a portable
        // copy unzipped without its Assets folder started with no tray icon - and with no main window, no way
        // to reach the application at all.
        _trayIcon.SetIcon(TrayIconArt.Read(name));
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
            TimeSpan.FromMilliseconds(_settings.CopyNotificationMs),
            CopyNotificationAnchor());
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

    /// <summary>
    /// The configurable left click. Right click is not routed through here - it always opens the menu, which is
    /// the one convention every tray application shares and the way back from any choice made here.
    /// </summary>
    private void OnTrayLeftClick(int x, int y)
    {
        switch (_settings.TrayLeftClick)
        {
            case TrayClickAction.Menu:
                ShowTrayMenu(x, y);
                break;

            case TrayClickAction.Settings:
                ShowSettings();
                break;

            case TrayClickAction.Nothing:
                break;

            default:
                ShowHistory();
                break;
        }
    }

    private void ShowTrayMenu(int x, int y)
    {
        // Timed in Debug because "the menu feels slow" is not something to guess at, and the two halves have
        // very different costs - see the numbers in CLAUDE.md.
        var started = System.Diagnostics.Stopwatch.StartNew();

        var menu = TrayMenuBuilder.Build(
            TrayMenu.Items(
                new TrayCommands(
                    About: ShowAbout,
                    History: ShowHistory,
                    Settings: ShowSettings,
                    Manual: OpenUserManual,
                    Keys: ShowShortcutHelp,
                    CheckForUpdates: CheckForUpdates,
                    PauseToggle: TogglePaused,
                    DisableToggle: ToggleDisabled,
                    Restart: RestartFromMenu,
                    RunAtStartupToggle: ToggleRunAtStartup,
                    AlwaysElevatedToggle: ToggleAlwaysRunAsAdministrator,
                    Exit: ExitApplication),
                isPaused: !_settings.MonitorClipboard,
                isDisabled: _gestureDisabled,

                // Read from the machine, not from settings. The shortcut or the task can be removed behind our
                // back - by the user, by another tool, by policy - and a tick reporting an intention rather
                // than a fact would be exactly as misleading as no tick at all.
                runsAtStartup: StartupShortcut.Exists || ElevatedLogonTask.Exists,
                alwaysElevated: ElevatedLogonTask.Exists));

        var built = started.Elapsed.TotalMilliseconds;

        TrayMenuBuilder.ShowAt(menu, x, y);

        DebugConsole.Log(
            $"tray menu: build {built:0.0} ms, show {started.Elapsed.TotalMilliseconds - built:0.0} ms, "
                + $"total {started.Elapsed.TotalMilliseconds:0.0} ms");
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
                _settings.HistoryPreviewMaxWidth,
                _settings.ClipJoinSeparator));
            _historyWindow.DensityChanged += OnHistoryDensityChanged;

            // Size restored before the first show, so the window appears where it was rather than jumping.
            var (width, height) = WindowGeometry.FitTo(
                _settings.HistoryWindowWidth,
                _settings.HistoryWindowHeight,
                SystemParameters.WorkArea.Width,
                SystemParameters.WorkArea.Height,
                _historyWindow.MinWidth,
                _historyWindow.MinHeight);

            _historyWindow.Width = width;
            _historyWindow.Height = height;

            if (_settings.HistoryWindowMaximised)
            {
                _historyWindow.WindowState = WindowState.Maximized;
            }

            // The split needs a laid-out window to fit against, so it waits for Loaded - see ApplyListWidth.
            _historyWindow.Loaded += (_, _) => _historyWindow?.ApplyListWidth(_settings.HistoryListWidth);

            _historyWindow.Closed += (_, _) =>
            {
                RememberHistoryWindowSize(_historyWindow);
                _historyWindow = null;
            };

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
            // The custom folders are passed as the roots already in force rather than re-read from the pointer:
            // AppPaths has resolved them once, and reading the file again is a second answer that could differ.
            _settingsWindow = Themed(new SettingsWindow(
                _settings,
                _formatters,
                _paths.ClipsLocation,
                _paths.SettingsLocation,
                _paths.ClipsLocation == DataLocation.CustomFolder ? _paths.ClipsRoot : null,
                _paths.SettingsLocation == DataLocation.CustomFolder ? _paths.SettingsRoot : null,
                _themes));
            _settingsWindow.SettingsApplied += OnSettingsApplied;
            _settingsWindow.DataLocationChangeRequested += OnDataLocationChangeRequested;
            _settingsWindow.LegacyImportRequested += OnLegacyImportRequested;
            _settingsWindow.ThemeCreationRequested += OnThemeCreationRequested;
            _settingsWindow.ThemesFolderRequested += OnThemesFolderRequested;

            // Preview: applied, not saved. Nothing writes settings here.
            _settingsWindow.ThemePreviewRequested += theme => _theme.Apply(theme);
            _settingsWindow.ThemeEditRequested += OnThemeEditRequested;

            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;

                // Undo a preview. Comparing against the saved setting rather than tracking whether the dialog was
                // accepted: OK and Apply have already written the new theme into _settings, so this is a no-op for
                // them and a revert for Cancel, Esc and the close button alike. One rule, no state to get wrong.
                if (!string.Equals(_theme.Requested, _settings.Theme, StringComparison.OrdinalIgnoreCase))
                {
                    _theme.Apply(_settings.Theme);
                }
            };
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }
    }

    /// <summary>
    /// Writes the palette currently in force out as a theme file, then shows it in Explorer.
    /// <para>
    /// The palette comes from <see cref="ThemeManager.CurrentPalette"/> rather than from the theme's own definition,
    /// so what lands in the file is what is on screen - a partial theme's inherited keys included. That is the whole
    /// point of "from this one": a starting point with every key filled in, whatever the theme it came from named.
    /// </para>
    /// </summary>
    private void OnThemeCreationRequested(string suggestedName)
    {
        try
        {
            var path = _themes.WriteStartingPoint(suggestedName, _theme.IsDark, _theme.CurrentPalette);

            // Selected in Explorer rather than merely opening the folder: a themes folder with several files in it
            // would otherwise leave the user hunting for the one just written.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageDialog.Warn(
                $"The theme file could not be written to {_themes.Folder}.\n\n{exception.Message}",
                "Could not create the theme",
                _settingsWindow);
        }
    }

    /// <summary>
    /// Opens a theme's file in the user's text editor, writing it out first when it has none.
    /// <para>
    /// The editor is the one from settings - the same program the clip editor uses - rather than a hard-coded
    /// Notepad, and the file is opened through the shell if that fails, so a machine whose .json files are
    /// associated with something better still gets it.
    /// </para>
    /// </summary>
    private void OnThemeEditRequested(string name)
    {
        try
        {
            var existing = _themes.FileFor(name);

            if (existing is null)
            {
                // No file: either a shipped theme, written out under its own name so editing it replaces it, or one
                // of the three built-in names, which the parser refuses - those become a copy instead.
                var reserved = ThemeNames.IsBuiltIn(name);

                existing = _themes.WriteStartingPoint(
                    reserved ? $"{name} copy" : name,
                    _theme.IsDark,
                    _theme.CurrentPalette,
                    overwrite: !reserved);

                // Re-read so the new file is in the catalogue, and the dialog's list with it - otherwise a second
                // Edit would write the file again and discard whatever had just been typed into it.
                _settingsWindow?.ReloadThemesForHost();
            }

            OpenInTextEditor(existing);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageDialog.Warn(
                $"The theme file could not be opened.\n\n{exception.Message}",
                "Could not edit the theme",
                _settingsWindow);
        }
    }

    /// <summary>
    /// Opens a file in the configured text editor, falling back to whatever the shell associates with it.
    /// <para>
    /// The fallback matters more here than for a clip: a <c>.json</c> file is very often associated with a proper
    /// editor, while <c>TextEditor</c> defaults to Notepad - which is a poor place to edit JSON but the only thing
    /// guaranteed to exist.
    /// </para>
    /// </summary>
    private void OpenInTextEditor(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_settings.TextEditor, $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    private void OnThemesFolderRequested()
    {
        try
        {
            // Created first: the folder does not exist until a theme has been written, and opening Explorer on a
            // path that is not there fails with a dialog of its own that explains nothing.
            Directory.CreateDirectory(_themes.Folder);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_themes.Folder)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageDialog.Warn(
                $"The themes folder could not be opened.\n\n{exception.Message}",
                "Could not open the folder",
                _settingsWindow);
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
    /// <summary>
    /// Saves the history window's size as it closes, so the next opening matches the last one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RestoreBounds</c> rather than <c>ActualWidth</c>: while a window is maximised those two report the screen,
    /// so remembering them would lose the size the user had chosen and leave Restore with nowhere to go. Maximised
    /// is remembered as the state it is, separately.
    /// </para>
    /// <para>
    /// Written only when something changed, because this runs on every close and the settings file is on disk.
    /// </para>
    /// </remarks>
    private void RememberHistoryWindowSize(Window? window)
    {
        if (window is null)
        {
            return;
        }

        var maximised = window.WindowState == WindowState.Maximized;
        var bounds = window.RestoreBounds;
        var listWidth = window is HistoryWindow history && history.CurrentListWidth > 0
            ? history.CurrentListWidth
            : _settings.HistoryListWidth;

        // An empty RestoreBounds means the window never really laid out - nothing worth saving over what we have.
        var width = bounds.Width > 0 ? (int)Math.Round(bounds.Width) : _settings.HistoryWindowWidth;
        var height = bounds.Height > 0 ? (int)Math.Round(bounds.Height) : _settings.HistoryWindowHeight;

        if (width == _settings.HistoryWindowWidth
            && height == _settings.HistoryWindowHeight
            && maximised == _settings.HistoryWindowMaximised
            && listWidth == _settings.HistoryListWidth)
        {
            return;
        }

        _settings.HistoryWindowWidth = width;
        _settings.HistoryWindowHeight = height;
        _settings.HistoryWindowMaximised = maximised;
        _settings.HistoryListWidth = listWidth;
        _settings.Normalise();
        _settingsStore.Save(_settings);
    }

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
    /// Watches the message-only window for another instance asking us to show ourselves.
    /// <para>
    /// Returns null for everything else, which leaves the message to Windows and to the other subscribers -
    /// the clipboard listener and the tray icon are on this same window.
    /// </para>
    /// </summary>
    private IntPtr? OnMessageWindowMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        // A deployment asking us to make way. Queued rather than shutting down inside the window procedure,
        // which is reached from the message pump and must return - and shut down through the ordinary Exit
        // path, so settings are saved and the database closed cleanly rather than cut off mid-write.
        if (SingleInstanceSignal.IsExitRequest(message))
        {
            DebugConsole.Log("exit requested by another process - shutting down");
            Dispatcher.BeginInvoke(new Action(Shutdown));
            return IntPtr.Zero;
        }

        if (!SingleInstanceSignal.IsShowRequest(message))
        {
            return null;
        }

        // Queued rather than shown inline. This runs inside the window procedure, and showing a window from
        // there re-enters WPF's message pumping while Windows is still waiting for DefWindowProc.
        Dispatcher.BeginInvoke(ShowAlreadyRunningToast);

        return IntPtr.Zero;
    }

    /// <summary>
    /// Answers a second launch with a notification in the corner rather than by opening the history window.
    /// <para>
    /// Opening a window was the first attempt and it overreached: someone who double-clicked the shortcut by
    /// habit got a window they had not asked for, on top of whatever they were doing. A toast says the same
    /// thing - it is running, here is how to reach it - and then goes away on its own.
    /// </para>
    /// <para>
    /// Bottom-right rather than near the cursor, unlike the copy notification: this is a message about the
    /// application, and the corner is where Windows puts those, so it is where people look. Deliberately our
    /// own toast rather than a tray balloon, which Focus Assist can suppress silently - and being silent is
    /// the entire failure this replaces.
    /// </para>
    /// </summary>
    private void ShowAlreadyRunningToast()
    {
        var trigger = TriggerKey.Normalise(_settings.PasteModeTriggerKey);

        Toast().Notify(
            "PasteJump is already running",
            $"Hold Ctrl and tap {trigger} to paste. The icon is in the notification area, by the clock.",
            TimeSpan.FromMilliseconds(Math.Max(4000, _settings.CopyNotificationMs)),
            ToastPlacement.BottomRight,
            detailIsProse: true);
    }

    /// <summary>
    /// Asks GitHub whether a newer release exists, and reports what it finds.
    /// <para>
    /// <c>async void</c> because it is an event handler, with the whole body guarded - an unobserved exception
    /// from one of these takes the process down, and a failed update check has no business doing that. It runs
    /// only when the menu item is clicked; nothing checks at start-up.
    /// </para>
    /// </summary>
    private async void CheckForUpdates()
    {
        try
        {
            // Said before the wait rather than after, because a check can take up to ten seconds and a menu
            // that closes with nothing happening reads as a dead command. In the corner, like the other
            // messages about the application itself.
            Toast().Notify(
                "Checking for updates…",
                "Asking GitHub about the latest release.",
                TimeSpan.FromSeconds(10),
                ToastPlacement.BottomRight,
                detailIsProse: true);

            var result = await UpdateChecker.CheckAsync().ConfigureAwait(true);

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.Release is { } release:
                    if (MessageDialog.Confirm(
                            $"PasteJump {release.Tag} is available. You have {AppVersion.Current}."
                                + "\n\nOpen the release page to download it?",
                            headline: "An update is available",
                            title: "PasteJump - check for updates",
                            owner: null))
                    {
                        OpenInBrowser(
                            string.IsNullOrEmpty(release.PageUrl)
                                ? AppVersion.RepositoryUrl + "/releases"
                                : release.PageUrl);
                    }

                    break;

                case UpdateCheckStatus.UpToDate:
                    MessageDialog.Show(
                        UpdateCheck.DescribeUpToDate(AppVersion.Current),
                        headline: "You are up to date",
                        title: "PasteJump - check for updates");

                    break;

                case UpdateCheckStatus.NoReleases:
                    // Not an error, and worded so as not to look like one: this is exactly the state of the
                    // project until a first release is published.
                    MessageDialog.Show(
                        "No releases have been published yet, so there is nothing newer than the copy you are "
                            + $"running ({AppVersion.Current}).",
                        headline: "No releases published",
                        title: "PasteJump - check for updates");

                    break;

                default:
                    MessageDialog.Warn(
                        result.Detail.Length > 0 ? result.Detail : "The check could not be completed.",
                        headline: "Could not check for updates");

                    break;
            }
        }
        catch (Exception ex)
        {
            // Deliberately broad. See the async void note above: nothing about failing to check for an update
            // justifies ending the session.
            DebugConsole.Log($"update check failed: {ex}");

            MessageDialog.Warn(ex.Message, headline: "Could not check for updates");
        }
    }

    /// <summary>
    /// Opens a URL in the default browser. <c>UseShellExecute</c> is required - without it .NET treats the URI
    /// as an executable path and throws.
    /// </summary>
    private static void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageDialog.Warn(ex.Message, headline: "Could not open the link");
        }
    }

    /// <summary>
    /// Opens the compiled manual, or says why it cannot.
    /// <para>
    /// Both failure paths are reported rather than swallowed. A menu item that does nothing at all is the worst
    /// outcome here: the user has just asked for help, so silence is the one answer guaranteed to be unhelpful.
    /// </para>
    /// </summary>
    private static void OpenUserManual()
    {
        var path = HelpDocument.Locate();

        if (path is null)
        {
            MessageDialog.Warn(
                $"{HelpDocument.FileName} is not in the PasteJump folder. It ships with the release download; "
                    + "a build made from source does not include it.",
                headline: "The manual is not installed");
            return;
        }

        // Warned before opening, not after. Once hh.exe is up with an empty topic pane the user has no reason
        // to suspect the file is merely blocked, and every page will look broken.
        if (HelpDocument.IsBlockedByZoneIdentifier(path))
        {
            MessageDialog.Warn(
                "Windows has marked the manual as downloaded from the internet, so its pages may open blank. "
                    + $"To fix it: right-click {HelpDocument.FileName} in the PasteJump folder, choose "
                    + "Properties, and tick Unblock.",
                headline: "The manual may open blank");
        }

        try
        {
            HelpDocument.Open(path);
        }
        catch (Exception ex)
        {
            MessageDialog.Warn(ex.Message, headline: "Could not open the manual");
        }
    }

    /// <summary>
    /// Shows the paste-mode key list, with the keyboard. Reachable from the tray menu and from <c>F1</c> during
    /// the gesture.
    /// <para>
    /// It <em>used</em> to be shown deliberately without focus - <c>ShowActivated="False"</c> and no
    /// <c>Activate</c> - because F1 was pressed mid-gesture with Ctrl still held, and taking focus there would
    /// have sent the paste into the card instead of the document. That reasoning was already obsolete when it was
    /// written down: <see cref="PasteMode.PasteModeController"/> routes F1 through <c>EndAndOpenWindow</c>, which
    /// restores the clipboard and ends the session <em>before</em> the host is asked for a window, precisely so
    /// that the window can have the keyboard. There is no pending paste to misdirect by the time this runs.
    /// </para>
    /// <para>
    /// What the stale comment cost: every F1 and every tray-menu open produced a window that would not take a
    /// keypress until it was clicked, which is how it was reported. Note that fixing it needs more than dropping
    /// the attribute - see <see cref="WindowInterop.BringToFrontAndFocus"/> for why <c>Activate</c> alone is not
    /// reliable from a process that is not in the foreground, and for the measurements.
    /// </para>
    /// </summary>
    private void ShowShortcutHelp()
    {
        if (_helpWindow is null)
        {
            // Null when there is no .chm to open, which hides the button. Decided here rather than inside the
            // window so the window stays ignorant of where the manual lives.
            _helpWindow = Themed(new ShortcutHelpWindow(
                TriggerKey.Normalise(_settings.PasteModeTriggerKey),
                HelpDocument.Locate() is null ? null : OpenUserManual,
                _keyMap));
            _helpWindow.Closed += (_, _) => _helpWindow = null;
            _helpWindow.Show();
            WindowInterop.BringToFrontAndFocus(_helpWindow);
            return;
        }

        // Already open, and the same treatment: a second F1 should put it in front of whatever has covered it and
        // leave it ready for Esc. The Topmost blip this used to do raised the window without focusing it, which is
        // the same half-open state by another route.
        WindowInterop.BringToFrontAndFocus(_helpWindow);
    }

    private void OnSettingsApplied(PasteJumpSettings updated)
    {
        _settings = updated;
        _settingsStore.Save(_settings);

        _theme.Apply(_settings.Theme);
        _pasteHost.Paster.SetSettleDelay(_settings.PasteSettleDelayMs);
        _pasteHost.Paster.SetPerAppSettleDelays(PerAppSettleDelays.Parse(_settings.PasteSettleDelayPerApp), _foreground);
        _pasteHost.Paster.SetPasteKeystroke(_settings.PasteKeystroke);
        _pasteHost.SetPreviewSize(_settings.OverlayPreviewMaxWidth, _settings.OverlayPreviewMaxHeight);
        _pasteHost.SetOverlayFont(_settings.OverlayFontFamily, _settings.OverlayFontSize);
        _pasteHost.SetJoinSeparator(_settings.ClipJoinSeparator);
        _pasteHost.SetOverlayParts(_settings.OverlayParts);
        _pasteHost.SetDeletedFlash(_settings.OverlayDeletedFlashMs);
        _pasteHost.SetOverlayAnchor(_settings.OverlayX, _settings.OverlayY, _settings.OverlayPosition);
        _pasteHost.SetKeyHint(
            _settings.ShowOverlayKeyHint,
            TriggerKey.Normalise(_settings.PasteModeTriggerKey),
            PasteKeyMap.Parse(_settings.PasteModeKeys));

        _triggerVirtualKey = TriggerKey.ToVirtualKey(TriggerKey.Normalise(_settings.PasteModeTriggerKey));
        _keyMap = PasteKeyMap.Parse(_settings.PasteModeKeys);
        ApplyHistoryHotkey(announceFailure: true);

        // The help window lists the trigger key by name, so a change to it makes an open copy wrong.
        _helpWindow?.Close();

        // An open history window follows the new density rather than needing to be reopened.
        _historyWindow?.ApplyDensity(_settings.GridDensity);
        _historyWindow?.ApplyLimits(_settings.HistoryLoadLimit, _settings.HistoryPreviewMaxWidth, _settings.ClipJoinSeparator);

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
    private void OnDataLocationChangeRequested(DataLocationChoice clips, DataLocationChoice settings)
    {
        // By resolved root rather than by choice: moving from one custom folder to another leaves the choice
        // unchanged and still has to copy the data.
        var clipsChanged = !clips.SameRootAs(_paths.ClipsRoot);
        var settingsChanged = !settings.SameRootAs(_paths.SettingsRoot);

        var moves = new List<string>();

        if (clipsChanged)
        {
            moves.Add($"Clips  →  {Path.Combine(clips.Root, "data")}");
        }

        if (settingsChanged)
        {
            moves.Add($"Settings  →  {Path.Combine(settings.Root, "data")}");
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
            ClipsLocation = clips.Location,
            SettingsLocation = settings.Location,
            ClipsPath = clips.Path,
            SettingsPath = settings.Path,

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
    /// Turns starting-with-Windows on or off, from the tray.
    /// </summary>
    /// <remarks>
    /// The same setting the Settings dialog owns, reachable in one click because it is one of the two things
    /// anybody checks about a resident application. Turning it off also removes the elevated logon task,
    /// because that task <em>is</em> a logon entry - leaving it behind would mean "do not start at logon"
    /// quietly starting at logon, elevated.
    /// </remarks>
    private void ToggleRunAtStartup()
    {
        var on = StartupShortcut.Exists || ElevatedLogonTask.Exists;

        if (on)
        {
            _settings.RunAtLogon = false;
            _settingsStore.Save(_settings);
            StartupShortcut.Apply(false);

            var (removed, message) = ElevatedLogonTask.TryRemove();

            Toast().Notify(
                "PasteJump will not start with Windows",
                removed
                    ? "Both the startup shortcut and the elevated logon task are gone."
                    : "The startup shortcut is gone, but the elevated task could not be removed. " + message,
                TimeSpan.FromSeconds(6),
                ToastPlacement.BottomRight,
                detailIsProse: true);

            return;
        }

        _settings.RunAtLogon = true;
        _settingsStore.Save(_settings);
        StartupShortcut.Apply(true);

        Toast().Notify(
            "PasteJump will start with Windows",
            "Not elevated. Switch on Always Run as Administrator too if the gesture has to work in an "
                + "application whose keyboard input is intercepted by security software.",
            TimeSpan.FromSeconds(6),
            ToastPlacement.BottomRight,
            detailIsProse: true);
    }

    /// <summary>
    /// Turns "always run as administrator" on or off - a state, not a one-off restart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This was built first as a one-shot "Restart as Administrator" and that was the wrong shape: elevation
    /// is not something you do once, it is the state the application should come back in every time. The tick
    /// is also the only thing anywhere that answers "am I elevated right now".
    /// </para>
    /// <para>
    /// <b>A scheduled task is the mechanism, and it has to be:</b> Windows offers no way to mark a shortcut
    /// "run as administrator" without a UAC prompt on every start, which is unusable for something that starts
    /// at logon. A task registered with the highest privileges starts elevated silently.
    /// </para>
    /// <para>
    /// Registering that task needs the privileges it grants, so when PasteJump is not already elevated it
    /// relaunches itself under UAC and lets the elevated copy register it - one prompt for both halves. See
    /// <see cref="RelaunchRequest"/>.
    /// </para>
    /// </remarks>
    private void ToggleAlwaysRunAsAdministrator()
    {
        if (ElevatedLogonTask.Exists)
        {
            var (removed, message) = ElevatedLogonTask.TryRemove();

            if (!removed)
            {
                MessageDialog.Show("The elevated logon task could not be removed. " + message);
                return;
            }

            // The task WAS the logon entry, so put the ordinary one back if starting at logon is still wanted.
            // Without this, switching elevation off would silently switch auto-start off with it.
            if (_settings.RunAtLogon)
            {
                StartupShortcut.Apply(true);
            }

            Toast().Notify(
                "PasteJump will no longer start as administrator",
                "This copy keeps the rights it already has until you restart it. Note the gesture may stop "
                    + "working in applications whose keyboard input is intercepted by security software.",
                TimeSpan.FromSeconds(8),
                ToastPlacement.BottomRight,
                detailIsProse: true);

            return;
        }

        if (IsRunningElevated())
        {
            EnableElevatedLogonTask();
            return;
        }

        // Not elevated: relaunch under UAC and let that copy register the task. The launch comes before the
        // shutdown because UAC can be refused, and shutting down first would leave the user with nothing over
        // a dialog they merely dismissed.
        RelaunchElevated(enableElevatedLogonTask: true);
    }

    /// <summary>Registers the logon task and reports what happened. Only meaningful while elevated.</summary>
    private void EnableElevatedLogonTask()
    {
        var (registered, message) = ElevatedLogonTask.TryRegister();

        if (!registered)
        {
            MessageDialog.Show("The elevated logon task could not be registered. " + message);
            return;
        }

        // One logon entry, not two. Both would start two copies, and the second would find the first through
        // the single-instance mutex and merely surface it - which reads as a duplicate that does nothing.
        StartupShortcut.Apply(false);
        _settings.RunAtLogon = true;
        _settingsStore.Save(_settings);

        Toast().Notify(
            "PasteJump will always run as administrator",
            "It starts elevated at logon from now on, which is what lets the gesture work in applications "
                + "whose keyboard input is intercepted by security software.",
            TimeSpan.FromSeconds(8),
            ToastPlacement.BottomRight,
            detailIsProse: true);
    }

    /// <summary>
    /// Relaunches elevated, so the keyboard hook is not excluded from input whose owner outranks us.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why this exists rather than a manifest asking for administrator: PasteJump starts at logon and runs all
    /// day, and <c>requireAdministrator</c> would mean a UAC prompt on every single start. This asks once, when
    /// the user chooses to. <c>tools/install-elevated-task.ps1</c> is the permanent form, through a logon task.
    /// </para>
    /// <para>
    /// <b>The order is the opposite of <see cref="Restart"/>'s, and it has to be.</b> That one shuts down first
    /// and starts the replacement from its own <c>Exit</c> handler, which is what releases the mutex in time.
    /// Here the launch can be <em>refused</em> - UAC is a prompt the user may cancel - and shutting down first
    /// would leave them with no PasteJump at all over a dialog they simply dismissed. So the replacement is
    /// started while this instance is still alive, and told to wait for it: see <see cref="RelaunchRequest"/>.
    /// </para>
    /// </remarks>
    private void RelaunchElevated(bool enableElevatedLogonTask = false)
    {
        var exePath = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exePath))
        {
            MessageDialog.Show("PasteJump could not work out its own path, so it cannot restart itself.");
            return;
        }

        try
        {
            var elevated = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = RelaunchRequest.Arguments(Environment.ProcessId, enableElevatedLogonTask),
                UseShellExecute = true,

                // The whole point. ShellExecute is the only way to ask for elevation from a running process.
                Verb = "runas",
            });

            if (elevated is null)
            {
                MessageDialog.Show("Windows did not start the elevated copy of PasteJump.");
                return;
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt. Nothing to report - they know what they just
            // did - and nothing to change. Staying alive is the whole reason the launch comes before the exit.
            return;
        }
        catch (Exception ex)
        {
            MessageDialog.Show($"PasteJump could not restart as administrator: {ex.Message}");
            return;
        }

        // Only now, with an elevated copy known to be starting and waiting for this process id.
        Shutdown();
    }

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
        // Tested against the user's intention rather than against whether the hook happens to be installed. Those
        // were the same thing until the watchdog arrived; now the hook can be absent because Windows dropped it,
        // and reading IsInstalled here would make one accidental drop look like the user having switched the
        // application off - after which the watchdog would refuse to put it back.
        if (!_gestureDisabled)
        {
            _gestureDisabled = true;
            // The session is closed first, so the overlay cannot be left on screen with no way to dismiss
            // it once the keys that would dismiss it are no longer being received.
            _recognizer.Reset();

            _keyboardHook.Uninstall();
            _historyHotkey.Unregister();
            _clipboardMonitor.Stop();
        }
        else
        {
            _gestureDisabled = false;
            _keyboardHook.Install();
            NoteHookEvent();
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
        if (_gestureDisabled)
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
            // The mutex exists but we cannot open it. With a session-local name that no longer means another
            // user - it means another copy in THIS session that we have no access to, which in practice is one
            // running elevated. Still "already running", so still refuse; and the message the caller shows on
            // the unreachable path is worded to cover it.
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
