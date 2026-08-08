using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PasteJump.App.Services;

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
    private readonly DispatcherTimer _dismissTimer;
    private bool _stylesApplied;

    public ToastWindow()
    {
        InitializeComponent();

        _dismissTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _dismissTimer.Tick += (_, _) => BeginFadeOut();

        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // Same reasoning as the overlay: NOACTIVATE, TRANSPARENT and TOOLWINDOW together make this
        // window incapable of taking focus or intercepting a click. A toast that appears mid-copy and
        // steals foreground would be worse than no toast at all.
        WindowInterop.MakeNonActivating(handle);

        // Rounded corners and the drop shadow now come from DWM rather than from AllowsTransparency, which
        // would cost ClearType on every glyph in the window. Border colour taken from the palette so it
        // follows the theme instead of the system accent.
        WindowInterop.ApplyRoundedCorners(handle, ThemeBorderColor());

        _stylesApplied = true;
    }

    /// <summary>
    /// Shows a notification near the mouse cursor for <paramref name="duration"/>, replacing whatever
    /// is currently displayed.
    /// </summary>
    /// <param name="headline">Short first line, e.g. "Copied - 12 clips".</param>
    /// <param name="detail">Optional preview line. Collapsed when null or empty.</param>
    public void Notify(string headline, string? detail, TimeSpan duration)
    {
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
        // stuck part-way through an animation with a stale opacity.
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;

        _dismissTimer.Stop();

        if (!IsVisible)
        {
            Show();
        }

        // Measure before positioning: SizeToContent leaves ActualWidth stale until layout has run,
        // and clamping against a stale size puts the window partly off-screen.
        UpdateLayout();
        PositionNearCursor();

        if (!_stylesApplied)
        {
            // Show() has now created the handle even if SourceInitialized has not been observed yet.
            WindowInterop.MakeNonActivating(new WindowInteropHelper(this).Handle);
        }

        _dismissTimer.Interval = duration;
        _dismissTimer.Start();
    }

    /// <summary>
    /// The palette's border colour, for the DWM border. Falls back to a mid grey if the resource is missing,
    /// which only happens in a host that composed the resource set by hand and forgot one.
    /// </summary>
    private System.Windows.Media.Color ThemeBorderColor()
        => TryFindResource("BorderBrush") is System.Windows.Media.SolidColorBrush brush
            ? brush.Color
            : System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80);

    /// <summary>Hides immediately, without the fade. For shutdown and for entering paste mode.</summary>
    public void HideNow()
    {
        _dismissTimer.Stop();
        BeginAnimation(OpacityProperty, null);
        Hide();
    }

    private void BeginFadeOut()
    {
        _dismissTimer.Stop();

        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            FillBehavior = FillBehavior.HoldEnd,
        };

        fade.Completed += (_, _) =>
        {
            // Guard against a new notification having arrived during the fade: it will have reset
            // Opacity to 1, and hiding here would swallow it.
            if (Opacity < 0.05)
            {
                Hide();
            }
        };

        BeginAnimation(OpacityProperty, fade);
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
}
