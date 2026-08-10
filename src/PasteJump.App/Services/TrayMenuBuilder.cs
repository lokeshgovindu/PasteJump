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
/// </summary>
internal static class TrayMenuBuilder
{
    public static ContextMenu Build(
        Action onAbout,
        Action onHistory,
        Action onSettings,
        Action onHelp,
        Action onCheckForUpdates,
        Action onPauseToggle,
        Action onDisableToggle,
        Action onRestart,
        Action onExit,
        bool isPaused,
        bool isDisabled)
    {
        var menu = new ContextMenu();

        // About first and bold, as requested. Note that bold in a context menu conventionally marks the
        // DEFAULT item - the one a double-click invokes - and that is still "Clipboard history", which
        // is what a left-click on the tray icon opens. The emphasis here is presentational only; the
        // tray's own activation behaviour is unchanged.
        menu.Items.Add(MenuItemFor("_About PasteJump…", onAbout, emphasised: true));

        // Beside About, where people look for it, and phrased with an ellipsis because it opens a dialog rather
        // than doing something silently. It only ever runs when clicked - see UpdateChecker for why there is no
        // check at start-up.
        menu.Items.Add(MenuItemFor("Check for _Updates…", onCheckForUpdates));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("Clipboard _History…", onHistory));
        menu.Items.Add(new Separator());
        // The parenthetical is the whole point of the wording. "Pause monitoring" next to "Disable PasteJump"
        // was reported as two names for one command, because the two differ in exactly one respect - whether
        // Ctrl+V still works - and nothing in either label said so.
        // Title case for the command, sentence case inside the parentheses. The parenthetical is an
        // explanatory phrase rather than a label, and "(Keep Pasting)" reads as a second command.
        menu.Items.Add(MenuItemFor(
            isPaused ? "_Resume Capture" : "_Pause Capture (keep pasting)",
            onPauseToggle));
        menu.Items.Add(MenuItemFor("_Settings…", onSettings));
        menu.Items.Add(MenuItemFor("Paste-Mode _Keys…", onHelp));

        // No "clear clips" item here, deliberately. One was added and then removed: the gesture's X cycle
        // already reaches DELETE ALL and now confirms before acting, which was the real problem with it, and
        // the Paste Mode tab names the keys. A destructive item one click away in the tray earned nothing
        // beyond a mouse-only route, in an application whose whole premise is the keyboard.
        menu.Items.Add(new Separator());

        // Distinct from Pause above, and the difference is worth the two menu items. Pause stops capturing
        // but keeps the gesture, so Ctrl+V still opens the overlay on the clips already held. Disable also
        // releases the keyboard hook, handing Ctrl+V back to Windows untouched - which is what you want in
        // order to use another clipboard manager, or to rule PasteJump out when something else misbehaves.
        // Both labels name their effect on Ctrl+V, because that is the only difference between them.
        menu.Items.Add(MenuItemFor(
            isDisabled ? "_Enable PasteJump" : "_Disable PasteJump (Ctrl+V passes through)",
            onDisableToggle));

        // Restart sits immediately above Exit. Both are the same kind of end-of-session action, and grouping
        // them leaves Exit at the very bottom where muscle memory expects it - appending Restart last would
        // have moved Exit and caused mis-clicks.
        menu.Items.Add(MenuItemFor("_Restart PasteJump", onRestart));
        menu.Items.Add(MenuItemFor("E_xit PasteJump", onExit));

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

        _owner = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = null,
            Opacity = 0,
            Left = -32000,
            Top = -32000,
        };

        _owner.Show();
        _owner.Hide();
    }

    public static void ShowAt(ContextMenu menu, int physicalX, int physicalY)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var scale = WindowInterop.GetScaleForPoint(physicalX, physicalY);

        // Captured before the null-coalescing assignment below, or the log claims every owner was created.
        var reused = _owner is not null;

        var owner = _owner ??= new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = null,
            Opacity = 0,
        };

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

        menu.Closed += OnMenuClosed;

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
    /// tree the menu is still finishing with. Unsubscribed here because <c>ShowAt</c> subscribes per show, and
    /// a menu shown repeatedly would otherwise accumulate handlers.
    /// </para>
    /// </summary>
    private static void OnMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            menu.Closed -= OnMenuClosed;
        }

        _owner?.Dispatcher.BeginInvoke(() => _owner?.Hide());
    }

    private static MenuItem MenuItemFor(string header, Action action, bool emphasised = false)
    {
        var item = new MenuItem { Header = header };

        if (emphasised)
        {
            item.FontWeight = FontWeights.SemiBold;
        }

        item.Click += (_, _) => action();
        return item;
    }
}
