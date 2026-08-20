using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PasteJump.App.Services;
using PasteJump.Core.Model;
using PasteJump.Core.Settings;
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

        // Rounded corners and the drop shadow come from DWM rather than from AllowsTransparency, which would
        // cost ClearType on every glyph in a window that is nothing but 11-12px text.
        WindowInterop.ApplyRoundedCorners(this);
    }

    /// <summary>
    /// Which cosmetic parts to draw. Everything on until told otherwise, so a host that never calls
    /// <see cref="ApplyParts"/> behaves exactly as this window always did.
    /// </summary>
    private OverlayParts _parts = OverlayParts.All;

    /// <summary>
    /// Sets which parts of the overlay are drawn. Applied on the next <see cref="Render"/>, which happens on every
    /// tap of the trigger key - so a change made in Settings while a session is somehow open still lands.
    /// </summary>
    public void ApplyParts(OverlayParts parts) => _parts = parts;

    /// <summary>
    /// Sets the key hint, or hides it.
    /// <para>
    /// Built as coloured runs rather than one string: the keys are accented and the words beside them are ordinary
    /// text, separated by a dim pipe. At 10px in a single muted colour it was reported as unreadable on the dark
    /// palette, and it was - a hint nobody can read is worse than the space it occupies, because it looks like
    /// something is wrong with the rendering.
    /// </para>
    /// <para>
    /// The letters come from the key map, not from literals. They are configurable now, so a hint saying
    /// <c>A: newest</c> after the user moved that action would name a key that does nothing - the same failure the
    /// F1 card avoids by reading the map. An action switched off is left out entirely.
    /// </para>
    /// </summary>
    public void ApplyKeyHint(bool show, char triggerKey, PasteKeyMap? keyMap = null)
    {
        var map = keyMap ?? PasteKeyMap.Default;

        // Recorded before the early return below, because the chips need them on every frame while this runs only
        // when the settings change. Null when the action is switched off, which is what stops a chip naming a key
        // that does nothing.
        _showKeyHint = show;
        _formatKey = map.LetterFor("format");
        _kindKey = map.LetterFor("kind");

        if (!show)
        {
            KeyHintText.Visibility = Visibility.Collapsed;
            return;
        }

        // Ordered by how often someone reaching for the hint needs them: getting back to the top, moving, and the
        // two ways out. F1 last, because it is the answer when none of the others were enough.
        var pairs = new List<(string Key, string What)>();

        if (map.LetterFor("newest") is { } newest)
        {
            pairs.Add(($"{newest}/Home", "newest"));
        }
        else
        {
            pairs.Add(("Home", "newest"));
        }

        pairs.Add((map.LetterFor("back") is { } back ? $"{triggerKey}/{back}" : $"{triggerKey}/↑↓", "step"));
        pairs.Add(("Del", "delete"));

        if (map.LetterFor("kind") is { } kind)
        {
            pairs.Add((kind.ToString(), "filter"));
        }

        // Joining earns a place here despite the hint being deliberately short: it is the one action whose
        // existence cannot be guessed from anything on screen until a clip is already marked, at which point the
        // chip explains itself. Left out when switched off, like every other letter.
        if (map.LetterFor("join") is { } join)
        {
            pairs.Add((join.ToString(), "join"));
        }

        pairs.Add(("Esc", "cancel"));
        pairs.Add(("F1", "all keys"));

        KeyHintText.Visibility = Visibility.Visible;
        KeyHintText.Inlines.Clear();

        for (var i = 0; i < pairs.Count; i++)
        {
            if (i > 0)
            {
                KeyHintText.Inlines.Add(new System.Windows.Documents.Run("  |  ")
                {
                    Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
                });
            }

            KeyHintText.Inlines.Add(new System.Windows.Documents.Run(pairs[i].Key)
            {
                Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                FontWeight = FontWeights.SemiBold,
            });

            KeyHintText.Inlines.Add(new System.Windows.Documents.Run(": " + pairs[i].What));
        }
    }

    private bool _showKeyHint;
    private char? _formatKey;
    private char? _kindKey;

    /// <summary>
    /// A state chip that names the key which changes it: <c>Original (Z)</c>, <c>images only (K)</c>.
    /// <para>
    /// Asked for because the formatter chip said <c>Original</c> and nothing on screen said how to make it anything
    /// else - the footer hint lists the keys for stepping and leaving, not for the chips, and F1 was the only
    /// answer. The rule that came out of it is worth keeping general: <b>a chip that shows cycling state names its
    /// own key</b>, so the thing you are looking at is the thing that tells you.
    /// </para>
    /// <para>
    /// Three conditions, each of which stops the chip lying. The letter comes from the key map, so a rebound format
    /// key shows its new letter; a <c>null</c> letter means the action is switched off and the chip stays a plain
    /// name; and the whole parenthetical is suppressed when key hints are off, because that is what the setting
    /// asks for and this is a key hint wherever it happens to sit.
    /// </para>
    /// <para>
    /// Runs rather than one string, and the same colours the footer uses - accent for the letter, muted for the
    /// brackets - so the two read as one idea. The name itself inherits whatever the chip's style set, which is how
    /// the accent-coloured kind filter chip keeps its colour while its brackets go muted.
    /// </para>
    /// <para>
    /// Deliberately NOT applied to the JOIN chip. That one only appears once a clip is marked, so whoever is
    /// looking at it has already found the key; naming it there would be clutter of exactly the kind that got the
    /// join key taken out of the footer.
    /// </para>
    /// </summary>
    private void SetChipNamingItsKey(System.Windows.Controls.TextBlock chip, string text, char? key)
    {
        chip.Inlines.Clear();
        chip.Inlines.Add(new System.Windows.Documents.Run(text));

        if (!_showKeyHint || key is not { } letter)
        {
            return;
        }

        var muted = (System.Windows.Media.Brush)FindResource("MutedTextBrush");

        chip.Inlines.Add(new System.Windows.Documents.Run(" (") { Foreground = muted });
        chip.Inlines.Add(new System.Windows.Documents.Run(letter.ToString())
        {
            Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
            FontWeight = FontWeights.SemiBold,
        });
        chip.Inlines.Add(new System.Windows.Documents.Run(")") { Foreground = muted });
    }

    /// <summary>
    /// Shows or hides the transient <c>DELETED</c> chip.
    /// <para>
    /// Set by the host on a timer rather than carried on the frame, because it expires without a keystroke - and a
    /// frame is only produced when something the user did changes what the overlay says. Left visible across
    /// renders in between, which is why <c>Render</c> does not touch it.
    /// </para>
    /// </summary>
    public void ShowDeleted(bool show)
        => DeletedChip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Applies a frame and positions the window at the anchor, clamped to the work area.</summary>
    public void Render(PasteOverlayModel model, OverlayAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(model);

        PositionText.Text = model.IsEmpty
            ? "No matching clips"
            : $"Clip {model.Position} of {model.Total}";

        // "No matching clips" survives the position being switched off. It is the only thing on screen when a search
        // matches nothing, so hiding it would leave an empty box reading as a broken overlay rather than as a search
        // with no hits.
        PositionText.Visibility = _parts.Position || model.IsEmpty
            ? Visibility.Visible
            : Visibility.Collapsed;

        SetChipNamingItsKey(FormatterChip, model.FormatterName, _formatKey);
        FormatterChip.Visibility = _parts.Formatter ? Visibility.Visible : Visibility.Collapsed;

        PinnedChip.Visibility = model.Pinned && _parts.Pinned ? Visibility.Visible : Visibility.Collapsed;
        PopChip.Visibility = model.PopOnPaste ? Visibility.Visible : Visibility.Collapsed;

        // Describe() returns null for "all", which is the state that needs no chip.
        if (model.KindFilter.Describe() is { } filter)
        {
            SetChipNamingItsKey(KindFilterChip, filter, _kindKey);
            KindFilterChip.Visibility = Visibility.Visible;
        }
        else
        {
            KindFilterChip.Visibility = Visibility.Collapsed;
        }

        // The count is what will be pasted; the tick says whether THIS clip is part of it. Both, because the
        // count alone leaves the user unable to tell whether pressing the key again would add or remove.
        if (model.MarkedCount > 0)
        {
            JoinChip.Text = model.CurrentIsMarked
                ? $"JOIN {model.MarkedCount} ✓"
                : $"JOIN {model.MarkedCount}";

            JoinChip.Visibility = Visibility.Visible;
        }
        else
        {
            JoinChip.Visibility = Visibility.Collapsed;
        }

        if (model.Tags.Count > 0 && _parts.Tags)
        {
            TagsChip.Text = "#" + string.Join(" #", model.Tags);
            TagsChip.Visibility = Visibility.Visible;
        }
        else
        {
            TagsChip.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrEmpty(model.SourceExecutable) && _parts.Source)
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
        Position(anchor);
    }

    /// <summary>Supplies decoded image bytes for an image clip, or null to clear.</summary>
    public void SetImagePayload(byte[]? imageBytes) => _pendingImageBytes = imageBytes;

    /// <summary>
    /// Sets the largest size an image preview may occupy. A maximum, not a size - the overlay never enlarges a
    /// picture, so a smaller one still draws at its own dimensions.
    /// <para>
    /// The window's own width cap follows it. Leaving that at a constant would mean a preview configured wider
    /// than the window was simply clipped, which would look like the setting not working rather than like two
    /// limits disagreeing.
    /// </para>
    /// </summary>
    /// <summary>Sets the overlay's font. Applies to a visible overlay as well as the next one.</summary>
    /// <param name="family">
    /// A font family name, or empty for the built-in look - the system UI font for labels with the clip's own
    /// text in Consolas. A name applies to the whole overlay, preview included; see the setting for why.
    /// </param>
    /// <param name="size">
    /// Text size in device-independent pixels, clamped to <see cref="SettingsBounds.OverlayFontSize"/>. The
    /// detail line stays a point smaller, which is the one size difference the overlay has always had.
    /// </param>
    /// <remarks>
    /// Writes the same four resource keys the XAML defines defaults for, so the bindings there re-resolve. A
    /// <see cref="FontFamily"/> is constructed from the name rather than validated: an unknown family falls back
    /// to the default face, which is a better outcome than refusing to draw the overlay at all.
    /// </remarks>
    public void ApplyFont(string? family, int size)
    {
        var clamped = Math.Clamp(size, SettingsBounds.OverlayFontSize.Min, SettingsBounds.OverlayFontSize.Max);

        Resources["OverlayFontSize"] = (double)clamped;

        // A point smaller, not a ratio: the difference is meant to stay one step at every size, and at 9px a
        // proportional shrink would round to the same number and lose the distinction entirely.
        Resources["OverlaySmallFontSize"] = (double)Math.Max(clamped - 1, SettingsBounds.OverlayFontSize.Min);

        if (string.IsNullOrWhiteSpace(family))
        {
            Resources["OverlayFontFamily"] = new FontFamily("Segoe UI");
            Resources["OverlayMonoFontFamily"] = new FontFamily("Consolas");
            return;
        }

        var chosen = new FontFamily(family.Trim());

        Resources["OverlayFontFamily"] = chosen;
        Resources["OverlayMonoFontFamily"] = chosen;
    }

    public void ApplyPreviewSize(int maxWidth, int maxHeight)
    {
        PreviewImage.MaxWidth = maxWidth;
        PreviewImage.MaxHeight = maxHeight;

        // Never narrower than the original 560: the header carries the position, chips and formatter name, and
        // squeezing those to suit a small preview would trade a readable overlay for a smaller picture.
        RootBorder.MaxWidth = Math.Max(560, maxWidth + PreviewMargin);

        // Text previews keep pace, so a taller overlay is taller for a long clip as well rather than only for a
        // picture - otherwise the setting would look like it applied selectively.
        PreviewText.MaxHeight = maxHeight;
    }

    /// <summary>Left and right padding around the preview inside the window, from the body Grid's margin.</summary>
    private const int PreviewMargin = 20;

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
            PreviewFileText.Visibility = Visibility.Collapsed;
            ImageFacts.Visibility = Visibility.Collapsed;
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

                // Dimensions only. The clip's stored byte count is not on the model, and the decoded size of a
                // DIB would be a different number from the one history reports for the same clip.
                ShowImageFacts(ClipKind.Image, $"{bitmap.PixelWidth} × {bitmap.PixelHeight}", null);
                return;
            }
        }

        // Reset before the file branches below decide to show one. The overlay is a single reused window, so a
        // block left visible from the previous clip is drawn under the next one - which reads as the wrong file's
        // contents rather than as a stale control.
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewFileText.Visibility = Visibility.Collapsed;
        PreviewText.Visibility = Visibility.Visible;
        PreviewText.Text = string.IsNullOrEmpty(model.PreviewText)
            ? DescribeKind(model.Kind)
            : model.PreviewText;

        // A copied image file keeps its path above and gains a thumbnail below it. Cached, because this runs on
        // every tap of the trigger key - see FileThumbnailCache.
        if (model.Kind == ClipKind.Files && FileThumbnailCache.TryGet(model.PreviewText) is { } thumb)
        {
            PreviewImage.Source = thumb.Bitmap;
            PreviewImage.Visibility = Visibility.Visible;
            // An image FILE is both things at once: its dimensions are image details, its size is a file's size. Split
            // that way because the questions answered in Settings were "do I want resolutions" and "do I want file
            // sizes", and a picture on disk answers to both.
            ShowImageFacts(
                ClipKind.Image,
                $"{thumb.PixelWidth} × {thumb.PixelHeight}",
                FormatBytes(thumb.FileBytes),
                sizeKind: ClipKind.Files);
            return;
        }

        // A copied text file gets the same treatment: path above, contents below, facts underneath. Tried after
        // the thumbnail, so a file that is somehow both is drawn as a picture.
        if (model.Kind == ClipKind.Files && FileTextPreviewCache.TryGet(model.PreviewText) is { } textFile)
        {
            PreviewFileText.Text = textFile.Text;
            PreviewFileText.Visibility = Visibility.Visible;
            ShowImageFacts(ClipKind.Files, textFile.Facts, FormatBytes(textFile.FileBytes));
            return;
        }

        // Text says as much about itself as a picture does: lines and characters on the left, bytes on the right.
        // The counts are computed by the controller, which is the only place that knows how much of the clip was
        // actually stored - see PasteModeController.DescribeTextFacts.
        if (model.TextFacts is { Length: > 0 } facts)
        {
            ShowImageFacts(model.Kind, facts, model.TotalBytes > 0 ? FormatBytes(model.TotalBytes) : null);
            return;
        }

        ImageFacts.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Fills the row under the preview, honouring the two switches that govern its halves.
    /// <para>
    /// The row disappears when neither half has anything to say, rather than remaining as an empty strip of padding.
    /// Each half is cleared as well as hidden, so a stale value cannot reappear if the other is switched back on
    /// while an overlay is up.
    /// </para>
    /// </summary>
    /// <param name="kind">
    /// Which set of switches governs this row. Not always <c>model.Kind</c>: a copied image file asks the image
    /// switches about its dimensions and the file switches about its size.
    /// </param>
    /// <param name="sizeKind">The kind governing the size half, when it differs from <paramref name="kind"/>.</param>
    private void ShowImageFacts(ClipKind kind, string dimensions, string? bytes, ClipKind? sizeKind = null)
    {
        var showDetails = _parts.DetailsFor(kind) && dimensions.Length > 0;
        var showSize = _parts.SizeFor(sizeKind ?? kind) && !string.IsNullOrEmpty(bytes);

        ImageDimensions.Text = showDetails ? dimensions : string.Empty;
        ImageBytes.Text = showSize ? bytes! : string.Empty;

        ImageFacts.Visibility = showDetails || showSize ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

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
    /// Places the overlay at the anchor without letting it run off the monitor.
    /// <para>
    /// Anchor coordinates arrive in physical pixels from Win32, but WPF positions windows in
    /// device-independent units, so they must be scaled by the DPI of the monitor the anchor is
    /// actually on - not the primary monitor's. Skipping that conversion is what puts overlays in
    /// the wrong place on mixed-DPI multi-monitor setups.
    /// </para>
    /// <para>
    /// The two placements are not interchangeable. A caret or a mouse pointer is a small thing the overlay must
    /// not sit on top of, so it goes just below and right of it; the middle of a window is not, and offsetting
    /// from it would put the overlay in that window's lower half rather than in the middle of it. Centring is
    /// exact rather than approximate because <c>Render</c> calls <c>UpdateLayout</c> first, so
    /// <c>ActualWidth</c> is this frame's size and not the previous frame's - the overlay changes size
    /// substantially between a text clip and an image one.
    /// </para>
    /// </summary>
    private void Position(OverlayAnchor anchor)
        => AnchoredPlacement.Apply(this, anchor, fallbackWidth: 360, fallbackHeight: 140);

    /// <summary>
    /// The largest <c>FontSize</c> any text in the overlay actually draws at. UI smoke harness only.
    /// </summary>
    /// <remarks>
    /// Reads the visual tree rather than the resources, which is the point: a resource can hold 18 while a
    /// <c>FontSize="12"</c> left behind in the XAML keeps a row at twelve, and nothing else would notice.
    /// </remarks>
    public double LargestTextSizeForSmokeTest()
    {
        var largest = 0d;

        Walk(this);

        return largest;

        void Walk(DependencyObject node)
        {
            if (node is TextBlock { Text.Length: > 0 } text && text.IsVisible)
            {
                largest = Math.Max(largest, text.FontSize);
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }
    }

}
