using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PasteJump.Core.Settings;
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
    /// Governs application windows. What <see cref="AppTheme.System"/> follows.
    /// <para>
    /// Note this is <em>not</em> the setting the taskbar and notification area obey - that is
    /// <c>SystemUsesLightTheme</c>, a genuinely independent value, and light-apps-on-a-dark-taskbar is
    /// the Windows default. It used to be read here to pick between two monochrome tray glyphs. The
    /// tray now shows the coloured application icon, which reads against either taskbar, so nothing in
    /// the app needs that value any more.
    /// </para>
    /// </summary>
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static readonly Uri LightPalette = new("Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkPalette = new("Themes/Dark.xaml", UriKind.Relative);

    private readonly Application _application;
    private AppTheme _requested = AppTheme.Light;
    private bool _disposed;

    public ThemeManager(Application application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));

        // Subscribed unconditionally rather than only while Theme is System. Keeping the subscription
        // tied to the current setting would mean attaching and detaching it from Apply, and the handler
        // already ignores everything it does not care about.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>The theme actually in force: never <see cref="AppTheme.System"/>, always resolved.</summary>
    public bool IsDark { get; private set; }

    /// <summary>Applies a theme, resolving <see cref="AppTheme.System"/> against the OS setting.</summary>
    public void Apply(AppTheme theme)
    {
        _requested = theme;

        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemPrefersDark(),
        };

        ApplyResolved(dark);
    }

    private void ApplyResolved(bool dark)
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

        merged[PaletteSlot] = new ResourceDictionary
        {
            Source = dark ? DarkPalette : LightPalette,
        };

        // Window chrome is drawn by the OS, not by WPF, so it does not follow the palette. Without
        // this a dark window keeps a white title bar.
        foreach (var window in _application.Windows.OfType<Window>())
        {
            ApplyTitleBar(window);
        }
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

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || _requested != AppTheme.System)
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
