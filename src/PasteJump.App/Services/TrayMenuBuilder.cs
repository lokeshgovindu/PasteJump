using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PasteJump.Interop;

namespace PasteJump.App.Services;

/// <summary>
/// Draws the tray menu described by <see cref="TrayMenu"/>, and shows it.
/// <para>
/// A WPF <see cref="ContextMenu"/> needs an activated owner to receive keyboard focus and to dismiss itself when
/// the user clicks elsewhere. A tray-only app has no such window, so a throwaway invisible one is created purely
/// to own the menu - otherwise the menu opens and then refuses to close, which is a common bug in hand-rolled
/// tray apps.
/// </para>
/// <para>
/// <b>The owner and the ContextMenu are created once and reused; the items are rebuilt per show.</b> That split
/// matters and is not arbitrary. Rebuilding the <em>menu</em> per click was reported as a visible glitch on
/// repeated right-clicks, because a new <see cref="ContextMenu"/> carries a new
/// <see cref="System.Windows.Controls.Primitives.Popup"/> - a new HWND with nothing rendered in it yet, so its
/// first frame can appear unpainted. Rebuilding the <em>items</em> inside the same menu keeps that HWND, costs a
/// fraction of a millisecond for a dozen rows, and is what lets each item carry its own action instead of the
/// static delegate table this used to need.
/// </para>
/// <para>
/// What it looks like is in <c>Themes/Controls.xaml</c>, not here. Nothing in this file sets a colour: an
/// unstyled ContextMenu renders in WPF's own light chrome regardless of the theme, and the templates there fix
/// that.
/// </para>
/// </summary>
internal static class TrayMenuBuilder
{
    /// <summary>The icon font. Fluent first, MDL2 as the Windows 10 fallback - see <see cref="TrayGlyph"/>.</summary>
    private static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static ContextMenu? _menu;

    /// <summary>
    /// The menu currently open, or null. Used to decide whether a deferred hide is still wanted - see
    /// <see cref="OnMenuClosed"/>.
    /// </summary>
    private static ContextMenu? _openMenu;

    /// <summary>Fills the shared menu with these items and returns it, ready to show.</summary>
    public static ContextMenu Build(IReadOnlyList<TrayMenuItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var timer = System.Diagnostics.Stopwatch.StartNew();

        // Created once: see the note on this class for what a second Popup costs.
        var reused = _menu is not null;

        var menu = _menu ??= CreateMenu();

        menu.Items.Clear();

        foreach (var item in items)
        {
            menu.Items.Add(Compose(item));
        }

        DebugConsole.Log(
            $"  TrayMenu: {items.Count} items {(reused ? "rebuilt in the existing menu" : "into a new menu")}, "
                + $"{timer.Elapsed.TotalMilliseconds:0.0} ms");

        return menu;
    }

    /// <summary>
    /// Drops the cached menu, so the next show builds one under the current palette.
    /// <para>
    /// Called by <see cref="ThemeManager"/> when the palette changes, and necessary because of what the cache
    /// costs: a <see cref="ContextMenu"/> held between right-clicks belongs to no visual tree, so the resource
    /// invalidation that re-resolves every <c>DynamicResource</c> in an open window never reaches it, and it keeps
    /// the colours it was built with. The UI smoke harness caught exactly that - a Dark menu still painted
    /// <c>#FFFFFF</c> - which is why this exists rather than being discovered by a user on theme number twenty.
    /// </para>
    /// <para>
    /// The cost is one fresh popup on the next right-click after a theme change: the same first-frame risk the
    /// cache exists to avoid, but paid once per theme change rather than once per click. Re-resolving the
    /// properties by hand instead was the alternative, and it would have to enumerate every brush the style and
    /// the item template set - a list that would go stale the first time the template gained a colour.
    /// </para>
    /// </summary>
    /// <para>
    /// Nulling the field is the whole of it. The discarded menu keeps its <c>Closed</c> handler on purpose, so one
    /// that happened to be open when the theme changed still hides the owner window on the way out -
    /// unsubscribing here would leave that window shown, invisible and activated.
    /// </para>
    public static void InvalidateForThemeChange() => _menu = null;

    /// <summary>One item, or a separator. Recurses for submenus, of which there are none today.</summary>
    private static object Compose(TrayMenuItem item)
    {
        if (item.IsSeparator)
        {
            return new Separator();
        }

        var element = new MenuItem
        {
            Header = item.Text,
            IsEnabled = item.IsEnabled,
            IsChecked = item.IsChecked,
        };

        if (item.Emphasised)
        {
            element.FontWeight = FontWeights.SemiBold;
        }

        if (item.Gesture is { Length: > 0 })
        {
            element.InputGestureText = item.Gesture;
        }

        if (item.Glyph is { Length: > 0 })
        {
            // No Foreground set on purpose: inheriting the item's means the glyph follows the theme and greys out
            // with a disabled row, both for free. Setting it here would need a DynamicResource per glyph and would
            // still miss the disabled case.
            var icon = new TextBlock
            {
                Text = item.Glyph,
                FontFamily = GlyphFont,

                // 16, not the 15 this shipped with for one day. Reported as poor quality, and the reason is that an
                // icon font is hinted for the sizes its designers used - 16, 20, 24 - so 15 lands between stems and
                // the rasteriser has to guess. Compared at 3x magnification across 14/15/16/18/20: 16 is the first
                // size where the gear's teeth and the keyboard's keys are distinct, and 18 is cleaner still but too
                // large beside 12px labels in a 26px row.
                FontSize = 16,
            };

            // Ideal, against the Display mode the menu sets for its text, and deliberately only here. Display snaps
            // glyph outlines to the pixel grid the way GDI did, which is what makes small TEXT crisp and what
            // visibly distorts an icon: the same comparison showed uneven stems and a blobby gear under Display at
            // every size. Grayscale rather than ClearType because subpixel antialiasing puts colour fringes on a
            // monochrome glyph, which reads as a rendering fault on the dark palettes.
            TextOptions.SetTextFormattingMode(icon, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(icon, TextRenderingMode.Grayscale);

            element.Icon = icon;
        }

        if (item.Submenu is { Count: > 0 })
        {
            foreach (var child in item.Submenu)
            {
                element.Items.Add(Compose(child));
            }

            // A header with children is not itself clickable - clicking it opens the submenu.
            return element;
        }

        if (item.Invoke is { } invoke)
        {
            // Captured directly, which is only safe because the items are rebuilt on every show. When the menu was
            // composed once and kept, handlers closing over the first set of delegates silently ignored every
            // later one, and a static table had to be consulted at click time instead.
            element.Click += (_, _) => invoke();
        }

        return element;
    }

    private static ContextMenu CreateMenu()
    {
        var menu = new ContextMenu();

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
}
