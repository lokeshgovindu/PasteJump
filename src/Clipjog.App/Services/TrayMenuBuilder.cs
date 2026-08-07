using System.Windows;
using System.Windows.Controls;

namespace Clipjog.App.Services;

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
        Action onHistory,
        Action onSettings,
        Action onHelp,
        Action onPauseToggle,
        Action onExit,
        bool isPaused)
    {
        var menu = new ContextMenu();

        menu.Items.Add(MenuItemFor("Clipboard _history…", onHistory, isDefault: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor(isPaused ? "_Resume monitoring" : "_Pause monitoring", onPauseToggle));
        menu.Items.Add(MenuItemFor("_Settings…", onSettings));
        menu.Items.Add(MenuItemFor("Paste-mode _keys…", onHelp));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItemFor("E_xit Clipjog", onExit));

        return menu;
    }

    public static void ShowAt(ContextMenu menu, int physicalX, int physicalY)
    {
        var scale = WindowInterop.GetScaleForPoint(physicalX, physicalY);

        var owner = new Window
        {
            Width = 1,
            Height = 1,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = null,
            Opacity = 0,
            Left = physicalX / scale,
            Top = physicalY / scale,
        };

        owner.Show();
        owner.Activate();

        menu.PlacementTarget = owner;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = physicalX / scale;
        menu.VerticalOffset = physicalY / scale;
        menu.StaysOpen = false;

        menu.Closed += (_, _) =>
        {
            // Deferred: closing the owner synchronously from the Closed handler tears down the
            // visual tree the menu is still finishing with.
            owner.Dispatcher.BeginInvoke(owner.Close);
        };

        menu.IsOpen = true;
    }

    private static MenuItem MenuItemFor(string header, Action action, bool isDefault = false)
    {
        var item = new MenuItem { Header = header };

        if (isDefault)
        {
            item.FontWeight = FontWeights.SemiBold;
        }

        item.Click += (_, _) => action();
        return item;
    }
}
