using System.Windows;
using PasteJump.Interop;

namespace PasteJump.App.Services;

/// <summary>
/// Pays WPF's first-window cost once, at idle, so the user does not pay it on their first click.
/// <para>
/// This exists because PasteJump is tray-only. Almost every WPF application shows a window as it starts, so
/// the framework's window and composition stack - HWND creation, the theme dictionaries, MilCore, and a great
/// deal of JIT - is warm by the time anyone interacts with it. Here nothing is shown at startup, so the bill
/// falls on whatever window the user opens first.
/// </para>
/// <para>
/// Measured on a Debug build, first right-click of the tray in a fresh process: 1,435-1,661 ms, of which
/// <em>1,134-1,383 ms was a single <c>Window.Show()</c></em> - the throwaway owner the context menu needs.
/// The second click was 74-99 ms. Constructing the window cost 0.1 ms, so it is <c>Show</c> that does the
/// work, and nothing about the menu itself was slow.
/// </para>
/// </summary>
internal static class WpfWarmUp
{
    private static bool _done;

    /// <summary>
    /// Creates and destroys one throwaway window, off-screen and without activating it.
    /// <para>
    /// Off-screen so nothing flashes, and <em>never</em> activated: this runs a moment after launch, when the
    /// user may well be typing into something else, and stealing the foreground to warm a cache would be a
    /// worse bug than the one being fixed. <c>Show</c> alone is what triggers the initialisation, so the
    /// activation is not needed.
    /// </para>
    /// <para>
    /// Deliberately does not open a menu or any popup, though that would absorb a little more of the cost. A
    /// <see cref="System.Windows.Controls.ContextMenu"/> captures the keyboard while open, and one flickering
    /// open at idle could swallow a keystroke from whatever the user is doing - a small saving is not worth
    /// that risk.
    /// </para>
    /// </summary>
    public static void Run()
    {
        if (_done)
        {
            return;
        }

        _done = true;

        try
        {
            // The window the tray menu will actually reuse, rather than a throwaway. Warming a *different*
            // window left 365 ms on the first click, because a fresh HWND is not free even once the framework
            // is warm; warming the real one leaves 0.1 ms.
            TrayMenuBuilder.PrewarmOwner();

            // Then the popup path, which was the other 129 ms. A plain Popup rather than a ContextMenu on
            // purpose: a ContextMenu captures the keyboard while open, and one flickering open at idle could
            // swallow a keystroke from whatever the user is doing. A Popup warms the same HWND-and-template
            // machinery without touching focus, and the MenuItem inside warms the item template too.
            var popup = new System.Windows.Controls.Primitives.Popup
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute,
                HorizontalOffset = -32000,
                VerticalOffset = -32000,
                AllowsTransparency = true,
                Child = new System.Windows.Controls.MenuItem { Header = "warm" },
                IsOpen = true,
            };

            // Closed on a later pass rather than immediately, so a layout and render actually happen - closing
            // it in the same breath would skip the work this is here to pay for.
            popup.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => popup.IsOpen = false));
        }
        catch (Exception ex)
        {
            // A failure here costs nothing but the warm-up itself, so it must not reach the user. Logged
            // because a warm-up that silently stopped working would show up only as the slowness returning.
            DebugConsole.Log($"WPF warm-up failed: {ex.Message}");
        }
    }
}
