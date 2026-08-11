using System.Windows;
using System.Windows.Controls;
using PasteJump.Interop;

namespace PasteJump.App.Services;

/// <summary>
/// Builds and shows the tray context menu.
/// <para>
/// A WPF <see cref="ContextMenu"/> needs an activated owner to receive keyboard focus and to
/// dismiss itself when the user clicks elsewhere. A tray-only app has no such window, so a
/// throwaway invisible one is created purely to own the menu - otherwise the menu opens and then
/// refuses to close, which is a common bug in hand-rolled tray apps.
/// </para>
/// <para>
/// Both the owner <em>and the menu itself</em> are created once and reused. Rebuilding the menu per click was
/// reported as a visible glitch on repeated right-clicks, and the reason is the same one the owner window was
/// already cached for: a new <see cref="ContextMenu"/> carries a new <see cref="System.Windows.Controls.Primitives.Popup"/>,
/// which means a new HWND with nothing rendered in it yet, so its first frame can appear unpainted. Reuse means
/// the popup is created and rendered exactly once per process.
/// </para>
/// </summary>
internal static class TrayMenuBuilder
{
    /// <summary>
    /// What each item does. Held in a field and replaced on every <see cref="Build"/> call rather than captured
    /// by the click handlers, because the menu outlives the call that first built it - handlers closing over the
    /// first set of delegates would silently ignore every later one.
    /// </summary>
    private sealed record TrayActions(
        Action About,
        Action History,
        Action Settings,
        Action Manual,
        Action Help,
        Action CheckForUpdates,
        Action PauseToggle,
        Action DisableToggle,
        Action Restart,
        Action Exit);

    private static TrayActions? _actions;

    private static ContextMenu? _menu;
    private static MenuItem? _pauseItem;
    private static MenuItem? _disableItem;

    /// <summary>
    /// The menu currently open, or null. Used to decide whether a deferred hide is still wanted - see
    /// <see cref="OnMenuClosed"/>.
    /// </summary>
    private static ContextMenu? _openMenu;

    public static ContextMenu Build(
        Action onAbout,
        Action onHistory,
        Action onSettings,
        Action onManual,
        Action onHelp,
        Action onCheckForUpdates,
        Action onPauseToggle,
        Action onDisableToggle,
        Action onRestart,
        Action onExit,
        bool isPaused,
        bool isDisabled)
    {
        _actions = new TrayActions(
            onAbout,
            onHistory,
            onSettings,
            onManual,
            onHelp,
            onCheckForUpdates,
            onPauseToggle,
            onDisableToggle,
            onRestart,
            onExit);

        _menu ??= Compose();

        // The only two items whose text depends on state, so the only two that need touching per show. Both
        // labels name their effect on Ctrl+V, because that is the sole difference between the commands and
        // "Pause monitoring" beside "Disable PasteJump" was reported as two names for one thing.
        // Title case for the command, sentence case inside the parentheses: "(Keep Pasting)" reads as a second
        // command rather than as the explanation it is.
        _pauseItem!.Header = isPaused ? "_Resume Capture" : "_Pause Capture (keep pasting)";
        _disableItem!.Header = isDisabled ? "_Enable PasteJump" : "_Disable PasteJump (Ctrl+V passes through)";

        return _menu;
    }

    private static ContextMenu Compose()
    {
        var menu = new ContextMenu();

        // About first and bold, as requested. Note that bold in a context menu conventionally marks the
        // DEFAULT item - the one a double-click invokes - and that is still "Clipboard history", which
        // is what a left-click on the tray icon opens. The emphasis here is presentational only; the
        // tray's own activation behaviour is unchanged.
        menu.Items.Add(MenuItemFor("_About PasteJump…", static a => a.About, emphasised: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Clipboard _History…", static a => a.History));
        menu.Items.Add(new Separator());

        _pauseItem = MenuItemFor("_Pause Capture (keep pasting)", static a => a.PauseToggle);
        menu.Items.Add(_pauseItem);

        menu.Items.Add(MenuItemFor("_Settings…", static a => a.Settings));

        // The manual had no route into it from anywhere in the application until now - it shipped in the
        // download and could only be found in Explorer. It sits above the keys card because it is the general
        // answer and the card is the specific one, which is also the order the two appear in F1's own window.
        //
        // Accelerator is L, not H: "Clipboard _History" already owns H, and History is the item people reach
        // for by keyboard. A duplicate accelerator does not fail, it just makes the first press select rather
        // than invoke - which reads as the menu ignoring you.
        menu.Items.Add(MenuItemFor("He_lp…", static a => a.Manual));
        menu.Items.Add(MenuItemFor("Paste-Mode _Keys…", static a => a.Help));

        // No "clear clips" item here, deliberately. One was added and then removed: the gesture's X cycle
        // already reaches DELETE ALL and now confirms before acting, which was the real problem with it, and
        // the Paste Mode tab names the keys. A destructive item one click away in the tray earned nothing
        // beyond a mouse-only route, in an application whose whole premise is the keyboard.
        menu.Items.Add(new Separator());

        // Fourth item from the bottom, in the group with Restart and Exit rather than beside About. It belongs
        // here: what an update leads to is replacing the program and restarting it, and the ellipsis says it
        // opens a dialog rather than acting silently. It only ever runs when clicked - see UpdateChecker for
        // why nothing checks at start-up.
        menu.Items.Add(MenuItemFor("Check for _Updates…", static a => a.CheckForUpdates));

        // Distinct from Pause above, and the difference is worth the two menu items. Pause stops capturing
        // but keeps the gesture, so Ctrl+V still opens the overlay on the clips already held. Disable also
        // releases the keyboard hook, handing Ctrl+V back to Windows untouched - which is what you want in
        // order to use another clipboard manager, or to rule PasteJump out when something else misbehaves.
        _disableItem = MenuItemFor("_Disable PasteJump (Ctrl+V passes through)", static a => a.DisableToggle);
        menu.Items.Add(_disableItem);

        // Restart sits immediately above Exit. Both are the same kind of end-of-session action, and grouping
        // them leaves Exit at the very bottom where muscle memory expects it - appending Restart last would
        // have moved Exit and caused mis-clicks.
        menu.Items.Add(MenuItemFor("_Restart PasteJump", static a => a.Restart));
        menu.Items.Add(MenuItemFor("E_xit PasteJump", static a => a.Exit));

        // Subscribed once, here, rather than per show - which is what the old code did, and it had to
        // unsubscribe again to stop handlers accumulating on a menu that was shown repeatedly.
        menu.Closed += OnMenuClosed;

        return menu;
    }

    /// <summary>
    /// The invisible window the menu is anchored to, created once and reused.
    /// <para>
    /// Reused rather than created per click because creating it is free (0.1 ms) and <c>Show</c> is not: a
    /// fresh transparent window cost 36-73 ms to show even once WPF was warm. Hidden between uses rather than
    /// closed, so the second and later clicks re-show a window that already has its HWND.
    /// </para>
    /// </summary>
    private static Window? _owner;

    /// <summary>
    /// Creates and shows the shared owner once, off-screen and unactivated, so the first real right-click
    /// reuses it instead of paying for it.
    /// <para>
    /// This is the single most valuable part of the warm-up. Showing this window cost 365 ms on the first
    /// click even after WPF's general window stack had been warmed by another window - a new HWND is not free -
    /// and reusing it afterwards costs 0.1 ms. Never activated here: at idle after launch the user may be
    /// typing, and taking the foreground to warm a cache would be a worse bug than the slowness.
    /// </para>
    /// </summary>
    public static void PrewarmOwner()
    {
        if (_owner is not null)
        {
            return;
        }

        _owner = CreateOwner();
        _owner.Left = -32000;
        _owner.Top = -32000;

        _owner.Show();
        _owner.Hide();
    }

    public static void ShowAt(ContextMenu menu, int physicalX, int physicalY)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var scale = WindowInterop.GetScaleForPoint(physicalX, physicalY);

        // Captured before the null-coalescing assignment below, or the log claims every owner was created.
        var reused = _owner is not null;

        var owner = _owner ??= CreateOwner();

        var constructed = timer.Elapsed.TotalMilliseconds;

        owner.Left = physicalX / scale;
        owner.Top = physicalY / scale;

        owner.Show();

        // Activated on purpose, unlike every other window here: this one exists to hold keyboard focus so the
        // menu dismisses when the user clicks elsewhere. Without it the menu opens and refuses to close.
        owner.Activate();

        var shown = timer.Elapsed.TotalMilliseconds;

        menu.PlacementTarget = owner;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = physicalX / scale;
        menu.VerticalOffset = physicalY / scale;
        menu.StaysOpen = false;

        // Recorded before opening, so a deferred hide left over from the previous show can tell that a new menu
        // is up and decline. See OnMenuClosed - this is the whole race.
        _openMenu = menu;

        menu.IsOpen = true;

        DebugConsole.Log(
            $"  ShowAt: owner {(reused ? "reused" : "created")}, "
                + $"Show+Activate {shown - constructed:0.0} ms, IsOpen {timer.Elapsed.TotalMilliseconds - shown:0.0} ms");
    }

    /// <summary>
    /// Hides the shared owner once the menu has closed.
    /// <para>
    /// Hide, not Close, because the window is reused - closing it would throw away the HWND this exists to
    /// keep. Deferred for the original reason: doing it synchronously from <c>Closed</c> tears down the visual
    /// tree the menu is still finishing with.
    /// </para>
    /// <para>
    /// The <see cref="_openMenu"/> check is what makes rapid repeated right-clicks safe, and its absence was a
    /// real defect. The hide is queued, so on a quick second click it could run <em>after</em> the next
    /// <see cref="ShowAt"/> had shown the owner and opened a menu on it - hiding the owner out from under a live
    /// menu, which is a menu that flashes up and vanishes. Checked twice on purpose: once here, and again
    /// inside the queued work, because a new show can land in the gap between the two.
    /// </para>
    /// </summary>
    private static void OnMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(_openMenu, sender))
        {
            // A newer menu is already open; this Closed belongs to the one it replaced.
            return;
        }

        _openMenu = null;

        _owner?.Dispatcher.BeginInvoke(() =>
        {
            if (_openMenu is null)
            {
                _owner?.Hide();
            }
        });
    }

    /// <summary>
    /// The owner window. One definition, because <see cref="PrewarmOwner"/> warms the very window
    /// <see cref="ShowAt"/> reuses - two constructor calls that drifted apart would leave the warm-up warming
    /// something subtly different from the real thing, which is exactly the mistake that cost 365 ms once.
    /// </summary>
    private static Window CreateOwner() => new()
    {
        Width = 1,
        Height = 1,
        WindowStyle = WindowStyle.None,
        ShowInTaskbar = false,
        AllowsTransparency = true,
        Background = null,
        Opacity = 0,
    };

    private static MenuItem MenuItemFor(
        string header,
        Func<TrayActions, Action> action,
        bool emphasised = false)
    {
        var item = new MenuItem { Header = header };

        if (emphasised)
        {
            item.FontWeight = FontWeights.SemiBold;
        }

        // Resolved from the current actions at click time rather than captured, since the menu is reused across
        // shows. Null only if a click somehow arrives before the first Build, which cannot happen - Build is
        // what creates the menu.
        item.Click += (_, _) =>
        {
            if (_actions is not null)
            {
                action(_actions)();
            }
        };

        return item;
    }
}
