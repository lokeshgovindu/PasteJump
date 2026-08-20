using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PasteJump.App.Services;
using PasteJump.Core.PasteMode;

namespace PasteJump.App.Views;

/// <summary>
/// The transient notification shown after a copy, and for messages the paste path needs to surface.
/// <para>
/// One instance is reused for the app's lifetime rather than created per notification. Copies arrive
/// in bursts, and creating a window per copy would mean a stream of <c>CreateWindowEx</c> calls and
/// HWND churn on the UI thread at exactly the moment the clipboard is contended.
/// </para>
/// </summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(220);

    private readonly DispatcherTimer _dismissTimer;
    private readonly DispatcherTimer _fadeTimer;
    private readonly Stopwatch _fadeClock = new();
    private bool _stylesApplied;

    public ToastWindow()
    {
        InitializeComponent();

        _dismissTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _dismissTimer.Tick += (_, _) => BeginFadeOut();

        _fadeTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _fadeTimer.Tick += (_, _) => StepFade();

        SourceInitialized += OnSourceInitialized;
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // Same reasoning as the overlay: NOACTIVATE, TRANSPARENT and TOOLWINDOW together make this
        // window incapable of taking focus or intercepting a click. A toast that appears mid-copy and
        // steals foreground would be worse than no toast at all.
        WindowInterop.MakeNonActivating(handle);

        // Rounded corners and the drop shadow now come from DWM rather than from AllowsTransparency, which
        // would cost ClearType on every glyph in the window.
        WindowInterop.ApplyRoundedCorners(this);

        _stylesApplied = true;
    }

    /// <summary>
    /// Shows a notification near the mouse cursor for <paramref name="duration"/>, replacing whatever
    /// is currently displayed.
    /// </summary>
    /// <param name="headline">Short first line, e.g. "Copied - 12 clips".</param>
    /// <param name="detail">Optional preview line. Collapsed when null or empty.</param>
    /// <summary>The detail line's normal font, for clip text. Cached because the toast fires on every copy.</summary>
    private static readonly FontFamily MonospaceFont = new("Consolas");

    public void Notify(string headline, string? detail, TimeSpan duration)
        => Notify(headline, detail, duration, ToastPlacement.NearCursor);

    /// <summary>
    /// As <see cref="Notify(string, string?, TimeSpan)"/>, choosing where it appears.
    /// <para>
    /// Near the cursor is right for a copy: it confirms something that just happened where the user was
    /// looking. The bottom corner is right for a message about the application itself, which is where Windows
    /// puts its own notifications and therefore where people already look for one.
    /// </para>
    /// </summary>
    public void Notify(string headline, string? detail, TimeSpan duration, ToastPlacement placement)
        => Notify(headline, detail, duration, placement, detailIsProse: false);

    /// <summary>
    /// As above, but placed by an <see cref="OverlayAnchor"/> - the same mechanism the paste overlay uses, so the
    /// copy notification can honour the same choice of position.
    /// </summary>
    /// <remarks>
    /// The two windows share <see cref="AnchoredPlacement"/> rather than each doing the arithmetic, which is the
    /// point of routing the toast through an anchor at all: this one used to be hard-wired beside the mouse
    /// pointer, with its own edge clamping, and would have drifted from the overlay the first time either was
    /// fixed.
    /// </remarks>
    public void Notify(string headline, string? detail, TimeSpan duration, OverlayAnchor anchor)
        => Notify(headline, detail, duration, ToastPlacement.NearCursor, detailIsProse: false, anchor);

    /// <summary>
    /// As above, with control over how the detail line is set.
    /// <para>
    /// <paramref name="detailIsProse"/> switches it from Consolas to the UI font. The monospace default is
    /// right for what the detail line normally holds - a clip's text, where alignment and character identity
    /// matter - and wrong for a sentence about the application, which reads as a code listing.
    /// </para>
    /// </summary>
    public void Notify(
        string headline,
        string? detail,
        TimeSpan duration,
        ToastPlacement placement,
        bool detailIsProse,
        OverlayAnchor? anchor = null)
    {
        // Set on every call, not just when prose is asked for: this window is reused for every notification,
        // so a font left behind by the previous one would follow the next clip preview.
        //
        // ClearValue rather than naming a font, which lets the UI font arrive by inheritance from the theme -
        // the XAML's FontFamily="Consolas" is itself a local value, so clearing it is what reveals the default.
        if (detailIsProse)
        {
            DetailText.ClearValue(FontFamilyProperty);
        }
        else
        {
            DetailText.FontFamily = MonospaceFont;
        }

        HeadlineText.Text = headline;

        if (string.IsNullOrWhiteSpace(detail))
        {
            DetailText.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailText.Text = detail;
            DetailText.Visibility = Visibility.Visible;
        }

        // Cancel any in-flight fade before repositioning, or a burst of copies leaves the window
        // stuck part-way through the fade at a stale alpha.
        StopFade();

        _dismissTimer.Stop();

        if (!IsVisible)
        {
            Show();
        }

        // After Show(), so the handle exists on the very first notification.
        WindowInterop.SetWindowAlpha(Handle, 1);

        // Measure before positioning: SizeToContent leaves ActualWidth stale until layout has run,
        // and clamping against a stale size puts the window partly off-screen.
        UpdateLayout();

        if (anchor is { } placeAt)
        {
            // The user's chosen position, resolved by the same helper the overlay uses.
            AnchoredPlacement.Apply(this, placeAt, fallbackWidth: 260, fallbackHeight: 60);
        }
        else if (placement == ToastPlacement.BottomRight)
        {
            PositionInBottomCorner();
        }
        else
        {
            PositionNearCursor();
        }

        if (!_stylesApplied)
        {
            // Show() has now created the handle even if SourceInitialized has not been observed yet.
            WindowInterop.MakeNonActivating(new WindowInteropHelper(this).Handle);
        }

        _dismissTimer.Interval = duration;
        _dismissTimer.Start();
    }

    /// <summary>Hides immediately, without the fade. For shutdown and for entering paste mode.</summary>
    public void HideNow()
    {
        _dismissTimer.Stop();
        StopFade();
        Hide();
    }

    /// <summary>
    /// Fades out over <see cref="FadeDuration"/> and hides.
    /// <para>
    /// This drives alpha through Win32 rather than animating <see cref="UIElement.Opacity"/>, because this
    /// window has no <c>AllowsTransparency</c> - see <see cref="WindowInterop.SetWindowAlpha"/>. A WPF opacity
    /// animation here runs to completion and changes the property while the window stays solid on screen, so
    /// the fade becomes an abrupt disappearance and nothing about the code looks wrong.
    /// </para>
    /// </summary>
    private void BeginFadeOut()
    {
        _dismissTimer.Stop();
        _fadeClock.Restart();
        _fadeTimer.Start();
    }

    private void StepFade()
    {
        var progress = _fadeClock.Elapsed.TotalMilliseconds / FadeDuration.TotalMilliseconds;

        if (progress >= 1)
        {
            // Hide before StopFade restores full alpha, or the last frame of the fade is a fully opaque
            // toast flashing back into view.
            Hide();
            StopFade();
            return;
        }

        WindowInterop.SetWindowAlpha(Handle, 1 - progress);
    }

    /// <summary>
    /// Stops a fade in progress and restores full alpha. Always restores, even when no fade was running: a
    /// window left part-way faded would come back translucent on the next <c>Show()</c>, and the alpha lives
    /// in the window style rather than in a property WPF resets for us.
    /// </summary>
    private void StopFade()
    {
        _fadeTimer.Stop();
        _fadeClock.Reset();

        if (Handle != IntPtr.Zero)
        {
            WindowInterop.SetWindowAlpha(Handle, 1);
        }
    }

    /// <summary>
    /// Places the toast just below-right of the cursor, clamped to the work area of whichever monitor
    /// the cursor is on - at that monitor's DPI, not the primary's.
    /// </summary>
    private void PositionNearCursor()
    {
        var (cursorX, cursorY) = PasteJump.Interop.ForegroundWindowInfo.GetCursorPosition();

        var scale = WindowInterop.GetScaleForPoint(cursorX, cursorY);
        var bounds = WindowInterop.GetWorkAreaForPoint(cursorX, cursorY, scale);

        var width = ActualWidth > 0 ? ActualWidth : 260;
        var height = ActualHeight > 0 ? ActualHeight : 60;

        var left = (cursorX / scale) + 14;
        var top = (cursorY / scale) + 20;

        if (left + width > bounds.Right)
        {
            left = bounds.Right - width - 6;
        }

        if (top + height > bounds.Bottom)
        {
            // Flip above the cursor rather than clamping to the bottom edge, so it does not sit on
            // top of whatever the user is pointing at.
            top = (cursorY / scale) - height - 8;
        }

        // Snapped to whole device pixels - see WindowInterop.SnapToDevicePixel. Dividing physical pixels by
        // a fractional scale lands on a half pixel often enough that this is the main reason the toast used
        // to look soft.
        Left = WindowInterop.SnapToDevicePixel(Math.Max(bounds.Left, left), scale);
        Top = WindowInterop.SnapToDevicePixel(Math.Max(bounds.Top, top), scale);
    }

    /// <summary>
    /// Places the toast in the bottom-right corner, where Windows shows its own notifications.
    /// <para>
    /// On the monitor the cursor is on rather than the primary: the user has just launched something, so that
    /// is the screen they are looking at, and a message about it appearing on another monitor is a message
    /// nobody reads. The work area is used rather than the screen bounds, so it sits above the taskbar - and
    /// it is the taskbar of <em>that</em> monitor, at <em>that</em> monitor's DPI.
    /// </para>
    /// </summary>
    private void PositionInBottomCorner()
    {
        var (cursorX, cursorY) = PasteJump.Interop.ForegroundWindowInfo.GetCursorPosition();

        var scale = WindowInterop.GetScaleForPoint(cursorX, cursorY);
        var bounds = WindowInterop.GetWorkAreaForPoint(cursorX, cursorY, scale);

        var width = ActualWidth > 0 ? ActualWidth : 260;
        var height = ActualHeight > 0 ? ActualHeight : 60;

        const double Margin = 12;

        var left = bounds.Right - width - Margin;
        var top = bounds.Bottom - height - Margin;

        // Same device-pixel snapping as the cursor placement, and for the same reason: these coordinates are
        // physical pixels divided by a possibly fractional scale, and landing on a half pixel renders the
        // whole window - text included - soft.
        Left = WindowInterop.SnapToDevicePixel(Math.Max(bounds.Left, left), scale);
        Top = WindowInterop.SnapToDevicePixel(Math.Max(bounds.Top, top), scale);
    }
}

/// <summary>Where a toast should appear.</summary>
public enum ToastPlacement
{
    /// <summary>Beside the mouse pointer. For confirming something the user just did there.</summary>
    NearCursor,

    /// <summary>
    /// The bottom-right of the work area, where Windows shows its own notifications. For messages about the
    /// application rather than about a clip.
    /// </summary>
    BottomRight,
}
