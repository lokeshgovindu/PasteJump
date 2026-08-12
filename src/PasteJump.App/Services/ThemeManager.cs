using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PasteJump.Core.Settings;
using PasteJump.Core.Theming;
using Microsoft.Win32;

namespace PasteJump.App.Services;

/// <summary>
/// Swaps the application's palette dictionary, and keeps the OS title bar in step.
/// <para>
/// Only the palette dictionary is replaced. <c>Themes/Controls.xaml</c> stays put and refers to the
/// palette entirely through <c>DynamicResource</c>, so a swap re-resolves every brush without
/// rebuilding a single control template. That is also why nothing in this app may reference a palette
/// key with <c>StaticResource</c>: it would bind once and then never follow a theme change.
/// </para>
/// </summary>
public sealed class ThemeManager : IDisposable
{
    /// <summary>
    /// Index of the palette entry in <see cref="Application.Resources"/>'s merged dictionaries.
    /// Must match the ordering in App.xaml.
    /// </summary>
    private const int PaletteSlot = 0;

    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Governs application windows. What <c>System</c> follows.
    /// <para>
    /// Note this is <em>not</em> the setting the taskbar and notification area obey - that is
    /// <c>SystemUsesLightTheme</c>, a genuinely independent value, and light-apps-on-a-dark-taskbar is
    /// the Windows default. It used to be read here to pick between two monochrome tray glyphs. The
    /// tray now shows the coloured application icon, which reads against either taskbar, so nothing in
    /// the app needs that value any more.
    /// </para>
    /// </summary>
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    /// <summary>
    /// The base palettes, as absolute component pack URIs rather than the relative paths these used to be.
    /// <para>
    /// A relative URI resolves against the <em>entry</em> assembly, so it worked in the application and threw
    /// "Cannot locate resource" the moment anything else drove this class - which the UI smoke harness does, being
    /// its own executable that references this one. The component form names the assembly, so it resolves from
    /// either host. Same lesson as <c>TrayIconArt</c>'s pack URI.
    /// </para>
    /// </summary>
    private static readonly Uri LightPalette = new("pack://application:,,,/PasteJump;component/Themes/Light.xaml", UriKind.Absolute);
    private static readonly Uri DarkPalette = new("pack://application:,,,/PasteJump;component/Themes/Dark.xaml", UriKind.Absolute);

    private readonly Application _application;
    private readonly ThemeCatalog? _catalog;
    private string _requested = ThemeNames.Light;
    private bool _disposed;

    public ThemeManager(Application application, ThemeCatalog? catalog = null)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));

        // Optional so the UI smoke harness can build a manager with no data folder behind it. Without a catalog only
        // the three built-in names resolve, which is all that harness needs.
        _catalog = catalog;

        // Subscribed unconditionally rather than only while Theme is System. Keeping the subscription
        // tied to the current setting would mean attaching and detaching it from Apply, and the handler
        // already ignores everything it does not care about.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>The theme actually in force: never <c>System</c> and never a missing name, always resolved.</summary>
    public bool IsDark { get; private set; }

    /// <summary>
    /// The palette dictionary currently in force. Handed to <see cref="ThemeCatalog.WriteStartingPoint"/> so an
    /// exported theme starts from the colours on screen.
    /// </summary>
    public ResourceDictionary CurrentPalette { get; private set; } = new();

    /// <summary>
    /// Applies a theme by name: <c>System</c>, <c>Light</c>, <c>Dark</c>, or any theme in the catalogue.
    /// <para>
    /// A name that resolves to nothing <b>falls back to following Windows</b> and says nothing about it. This is
    /// reached during start-up, before there is any window to report into, and a theme file can be legitimately
    /// absent for a moment - an unplugged drive, a file being edited. The stored setting is deliberately left
    /// untouched by the fallback, so the choice comes back when the file does.
    /// </para>
    /// </summary>
    public void Apply(string? theme)
    {
        _requested = theme ?? ThemeNames.System;

        var custom = ThemeNames.IsBuiltIn(_requested) ? null : _catalog?.Find(_requested);

        var dark = custom is not null
            ? custom.BasedOn == ThemeBase.Dark
            : string.Equals(_requested, ThemeNames.Dark, StringComparison.OrdinalIgnoreCase)
                || (!string.Equals(_requested, ThemeNames.Light, StringComparison.OrdinalIgnoreCase) && SystemPrefersDark());

        ApplyResolved(dark, custom);
    }

    private void ApplyResolved(bool dark, ThemeDefinition? custom = null)
    {
        var merged = _application.Resources.MergedDictionaries;

        if (merged.Count <= PaletteSlot)
        {
            // App.xaml was changed without updating PaletteSlot. Fail loudly in Debug rather than
            // silently rendering an unthemed window.
            System.Diagnostics.Debug.Fail("Palette slot missing from Application.Resources.");
            return;
        }

        IsDark = dark;

        // The base always loads in full, even for a custom theme: it is what supplies the keys the theme does not
        // name. A dictionary built only from a theme's own keys would leave the rest resolving to nothing, and the
        // controls that referenced them would render unstyled with no error anywhere.
        var palette = new ResourceDictionary
        {
            Source = dark ? DarkPalette : LightPalette,
        };

        if (custom is not null)
        {
            Overlay(palette, custom);
        }

        CurrentPalette = palette;
        merged[PaletteSlot] = palette;

        // Window chrome is drawn by the OS, not by WPF, so it does not follow the palette. Without
        // this a dark window keeps a white title bar.
        foreach (var window in _application.Windows.OfType<Window>())
        {
            ApplyTitleBar(window);
        }

        // The overlay's and toast's borders are drawn by DWM too, from a colour pushed through an API call
        // rather than bound - so they need the same nudge for the same reason.
        WindowInterop.RefreshThemedBorders();
    }

    /// <summary>
    /// Matches a window's title bar to the current theme. Call from a window's
    /// <see cref="Window.SourceInitialized"/> so it is set before the frame is first painted.
    /// </summary>
    public void ApplyTitleBar(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = IsDark ? 1 : 0;

        // DWMWA_USE_IMMERSIVE_DARK_MODE. Attribute 20 since Windows 10 2004; earlier builds used 19.
        // Both are tried and both failures ignored - on a build that supports neither, a light title
        // bar is a cosmetic mismatch, not something worth surfacing.
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
    }

    /// <summary>
    /// Writes a theme's colours over the base palette, in place.
    /// <para>
    /// The kind decides what is built, and getting it wrong is invisible rather than fatal: a <c>Color</c> where a
    /// brush is expected makes <c>DropShadowEffect.Color</c> throw at render time, and a brush where a colour is
    /// expected simply does not apply. The one gradient key accepts a single colour too, which becomes a flat
    /// brush - a theme should not have to care that the default happens to be a gradient.
    /// </para>
    /// </summary>
    private static void Overlay(ResourceDictionary palette, ThemeDefinition theme)
    {
        foreach (var (name, value) in theme.Colors)
        {
            var kind = PaletteKeys.Find(name)?.Kind ?? PaletteEntryKind.Brush;
            var top = ToColor(value.Top);

            palette[name] = kind switch
            {
                PaletteEntryKind.Color => top,

                PaletteEntryKind.Gradient when value.Bottom is { } bottom => new LinearGradientBrush(
                    top,
                    ToColor(bottom),
                    new Point(0, 0),
                    new Point(0, 1)),

                _ => new SolidColorBrush(top),
            };
        }
    }

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Only while following Windows, and only for a built-in name: a custom theme states its own base, so the OS
        // switching to dark must not repaint it.
        if (e.Category != UserPreferenceCategory.General
            || !string.Equals(_requested, ThemeNames.System, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dark = SystemPrefersDark();

        if (dark != IsDark)
        {
            ApplyResolved(dark);
        }
    }

    /// <summary>Reads the Windows "choose your mode" setting for apps.</summary>
    private static bool SystemPrefersDark() => ReadPrefersDark(AppsUseLightThemeValue);

    /// <summary>
    /// True when the named Personalize value says "dark".
    /// <para>
    /// Both values are absent on older builds and on some upgraded installs; the documented default in
    /// that case is light, which is what the fallback of 1 encodes.
    /// </para>
    /// </summary>
    private static bool ReadPrefersDark(string valueName)
    {
        try
        {
            return Registry.GetValue(PersonalizeKey, valueName, 1) is int light && light == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // SystemEvents is static, so leaving this attached would keep the instance alive past shutdown.
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
