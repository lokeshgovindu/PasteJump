using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PasteJump.App.Services;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;

namespace PasteJump.App.Views;

/// <summary>
/// The paste-mode overlay. Renders one <see cref="PasteOverlayModel"/> per frame and, critically,
/// never takes foreground away from the window being pasted into.
/// </summary>
public partial class OverlayWindow : Window
{
    private byte[]? _pendingImageBytes;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;

        // The XAML flags reduce the chance of activation; these extended styles remove it.
        // NOACTIVATE keeps clicks from focusing us, TRANSPARENT makes the window ignore hit
        // testing entirely, and TOOLWINDOW keeps it out of Alt+Tab.
        WindowInterop.MakeNonActivating(handle);
    }

    /// <summary>Applies a frame and positions the window at the anchor, clamped to the work area.</summary>
    public void Render(PasteOverlayModel model, int anchorX, int anchorY)
    {
        ArgumentNullException.ThrowIfNull(model);

        PositionText.Text = model.IsEmpty
            ? "No matching clips"
            : $"Clip {model.Position} of {model.Total}";

        FormatterChip.Text = model.FormatterName;

        PinnedChip.Visibility = model.Pinned ? Visibility.Visible : Visibility.Collapsed;
        PopChip.Visibility = model.PopOnPaste ? Visibility.Visible : Visibility.Collapsed;

        if (model.Tags.Count > 0)
        {
            TagsChip.Text = "#" + string.Join(" #", model.Tags);
            TagsChip.Visibility = Visibility.Visible;
        }
        else
        {
            TagsChip.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrEmpty(model.SourceExecutable))
        {
            SourceChip.Text = model.SourceExecutable;
            SourceChip.Visibility = Visibility.Visible;
        }
        else
        {
            SourceChip.Visibility = Visibility.Collapsed;
        }

        RenderCommitMode(model.CommitMode);
        RenderSearch(model);
        RenderBody(model);

        // Measure before positioning: SizeToContent means ActualWidth is stale until layout runs,
        // and clamping against a stale size puts the window partly off-screen.
        UpdateLayout();
        Position(anchorX, anchorY);
    }

    /// <summary>Supplies decoded image bytes for an image clip, or null to clear.</summary>
    public void SetImagePayload(byte[]? imageBytes) => _pendingImageBytes = imageBytes;

    private void RenderCommitMode(PasteCommitMode mode)
    {
        if (mode == PasteCommitMode.Paste)
        {
            ModeBanner.Visibility = Visibility.Collapsed;
            return;
        }

        ModeBanner.Visibility = Visibility.Visible;

        ModeText.Text = mode switch
        {
            PasteCommitMode.Cancel => "CANCEL  -  release Ctrl to cancel  (X cycles)",
            PasteCommitMode.Delete => "DELETE  -  release Ctrl to delete this clip  (X cycles)",
            PasteCommitMode.DeleteAll => "DELETE ALL  -  release Ctrl to clear unpinned clips  (X cycles)",
            _ => string.Empty,
        };

        ModeBanner.Background = mode == PasteCommitMode.Cancel
            ? (Brush)FindResource("WarnBrush")
            : (Brush)FindResource("DangerBrush");
    }

    private void RenderSearch(PasteOverlayModel model)
    {
        if (!model.IsSearching)
        {
            SearchRow.Visibility = Visibility.Collapsed;
            return;
        }

        SearchRow.Visibility = Visibility.Visible;
        SearchQueryText.Text = string.IsNullOrEmpty(model.SearchQuery)
            ? "type to filter…"
            : model.SearchQuery;
        MatchCountText.Text = $"{model.MatchCount} match{(model.MatchCount == 1 ? string.Empty : "es")}";
    }

    private void RenderBody(PasteOverlayModel model)
    {
        if (model.IsEmpty)
        {
            PreviewText.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;

        if (model.Kind == ClipKind.Image && _pendingImageBytes is { Length: > 0 })
        {
            var bitmap = TryDecodeImage(_pendingImageBytes);

            if (bitmap is not null)
            {
                PreviewImage.Source = bitmap;
                PreviewImage.Visibility = Visibility.Visible;
                PreviewText.Visibility = Visibility.Collapsed;
                return;
            }
        }

        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Visible;
        PreviewText.Text = string.IsNullOrEmpty(model.PreviewText)
            ? DescribeKind(model.Kind)
            : model.PreviewText;
    }

    private static string DescribeKind(ClipKind kind) => kind switch
    {
        ClipKind.Image => "[image]",
        ClipKind.Files => "[files]",
        _ => "[binary data]",
    };

    private static BitmapSource? TryDecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            return decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
        }
        catch (Exception)
        {
            // A DIB we cannot decode is not worth failing the whole overlay for; fall back to text.
            return null;
        }
    }

    /// <summary>
    /// Places the overlay near the anchor without letting it run off the monitor.
    /// <para>
    /// Anchor coordinates arrive in physical pixels from Win32, but WPF positions windows in
    /// device-independent units, so they must be scaled by the DPI of the monitor the anchor is
    /// actually on - not the primary monitor's. Skipping that conversion is what puts overlays in
    /// the wrong place on mixed-DPI multi-monitor setups.
    /// </para>
    /// </summary>
    private void Position(int anchorX, int anchorY)
    {
        var scale = WindowInterop.GetScaleForPoint(anchorX, anchorY);

        var desiredLeft = (anchorX / scale) + 4;
        var desiredTop = (anchorY / scale) + 20;

        var bounds = WindowInterop.GetWorkAreaForPoint(anchorX, anchorY, scale);

        var width = ActualWidth > 0 ? ActualWidth : 360;
        var height = ActualHeight > 0 ? ActualHeight : 140;

        if (desiredLeft + width > bounds.Right)
        {
            desiredLeft = bounds.Right - width - 4;
        }

        if (desiredTop + height > bounds.Bottom)
        {
            // Flip above the caret rather than clamping to the bottom edge, so the overlay does
            // not cover the line the user is typing on.
            desiredTop = (anchorY / scale) - height - 6;
        }

        Left = Math.Max(bounds.Left, desiredLeft);
        Top = Math.Max(bounds.Top, desiredTop);
    }
}
