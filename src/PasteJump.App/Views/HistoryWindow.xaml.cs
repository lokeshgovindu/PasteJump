using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PasteJump.App.Services;
using PasteJump.Core.Abstractions;
using PasteJump.Core.Capture;
using PasteJump.Core.Formatting;
using PasteJump.Core.Imaging;
using PasteJump.Core.Model;
using PasteJump.Core.Settings;
using PasteJump.Core.Storage;
using PasteJump.Interop;

namespace PasteJump.App.Views;

/// <summary>Row view-model for the history grid.</summary>
public sealed class HistoryRow
{
    public required long Id { get; init; }

    /// <summary>
    /// Position in history, newest first, assigned when the list is loaded.
    /// <para>
    /// Deliberately not recomputed when the user sorts by another column. The number is there to
    /// identify an entry and to give the eye something to count against; a value that renumbered on
    /// every sort would do neither.
    /// </para>
    /// </summary>
    public required int Number { get; init; }

    public required string Preview { get; init; }

    public required DateTimeOffset CapturedUtc { get; init; }

    public required long Bytes { get; init; }

    public required ClipKind Kind { get; init; }

    public string? BlobHash { get; init; }

    /// <summary>
    /// True when this row is a clip from the stack rather than a history entry. The two differ in more than
    /// where they came from: a clip carries every clipboard format, so Copy replays it faithfully and its image
    /// preview comes from the payloads; a history entry has one flattened record and a blob at most.
    /// </summary>
    public bool IsClip { get; init; }

    /// <summary>Only meaningful for a clip. Pinned clips sort first and survive DELETE ALL.</summary>
    public bool Pinned { get; init; }

    public string PinnedText => Pinned ? "PINNED" : string.Empty;

    public string KindText => Kind switch
    {
        ClipKind.Text => "Text",
        ClipKind.Image => "Image",
        ClipKind.Files => "Files",
        _ => "Other",
    };

    /// <summary>
    /// Full preview for the row tooltip, unlike <see cref="SingleLinePreview"/> which is truncated to
    /// keep the grid readable. Capped anyway, because a tooltip taller than the screen is useless.
    /// </summary>
    public string TooltipText
    {
        get
        {
            var text = Preview.Length > 1200 ? Preview[..1200] + "…" : Preview;

            return $"{KindText} · {SizeText} · {LocalTimeText}\n\n{text}";
        }
    }

    /// <summary>
    /// Grid-safe preview. Newlines are collapsed because a DataGrid row renders embedded line
    /// breaks as a single tall row, making a multi-line clip swamp the list.
    /// </summary>
    public string SingleLinePreview
    {
        get
        {
            var flattened = Preview
                .ReplaceLineEndings(" ")
                .Replace('\t', ' ');

            return flattened.Length > 300 ? flattened[..300] : flattened;
        }
    }

    public string LocalTimeText => CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public string SizeText => Bytes switch
    {
        < 1024 => $"{Bytes} B",
        < 1024 * 1024 => $"{Bytes / 1024.0:0.#} KB",
        _ => $"{Bytes / (1024.0 * 1024.0):0.#} MB",
    };
}

/// <summary>
/// The history browser: search, preview, copy back to the clipboard, delete.
/// </summary>
public partial class HistoryWindow : Window
{
    /// <summary>
    /// <c>CF_DIB</c>. Named here rather than reaching into PasteJump.Interop's internal constants,
    /// which are not public.
    /// </summary>
    private const uint CfDib = 8;

    /// <summary>
    /// Same labels as the Settings dialog's own density combo, deliberately: two controls for one setting
    /// that named the options differently would read as two different settings.
    /// </summary>
    private static readonly (GridDensity Density, string Label)[] DensityChoices =
    [
        (GridDensity.Roomy, "Roomy"),
        (GridDensity.Cozy, "Cozy"),
        (GridDensity.Compact, "Compact"),
    ];

    private readonly ClipStore _store;
    private readonly IClipboardAccess _clipboard;
    private readonly SelfWriteGuard _selfWrites;
    private readonly FormatterRegistry _formatters;
    private readonly ObservableCollection<HistoryRow> _rows = [];
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _refreshDebounce;

    /// <summary>
    /// Suppresses <see cref="DensityChanged"/> while the combo is being set in code. Without it, applying a
    /// change that arrived <em>from</em> the settings dialog would raise it straight back and save again.
    /// </summary>
    private bool _settingDensity;

    /// <summary>Most rows to load at once, from settings. A backstop, not a page size.</summary>
    private int _historyLoadLimit;

    /// <summary>Widest an image file is decoded for the preview pane, from settings.</summary>
    private int _thumbnailMaxWidth;

    public HistoryWindow(
        ClipStore store,
        IClipboardAccess clipboard,
        SelfWriteGuard selfWrites,
        FormatterRegistry formatters,
        GridDensity density = GridDensity.Cozy,
        int historyLoadLimit = ClipStore.DefaultHistoryLimit,
        int previewImageMaxWidth = DefaultThumbnailMaxWidth)
    {
        _store = store;
        _clipboard = clipboard;
        _selfWrites = selfWrites;
        _formatters = formatters;
        _historyLoadLimit = historyLoadLimit;
        _thumbnailMaxWidth = previewImageMaxWidth;

        InitializeComponent();

        EntriesGrid.ItemsSource = _rows;

        foreach (var choice in DensityChoices)
        {
            DensityCombo.Items.Add(choice.Label);
        }

        ApplyDensity(density);

        // Debounced so typing does not run one FTS query per keystroke.
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            Refresh();
        };

        // Debounced separately: a burst of captures should coalesce into one reload.
        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshDebounce.Tick += (_, _) =>
        {
            _refreshDebounce.Stop();
            Refresh();
        };

        Loaded += (_, _) =>
        {
            Refresh();
            SearchBox.Focus();
        };

        PreviewKeyDown += OnWindowKeyDown;

        Closed += (_, _) =>
        {
            _searchDebounce.Stop();
            _refreshDebounce.Stop();
        };
    }

    /// <summary>
    /// Applies the row-spacing setting. Called at construction and again whenever settings change, so
    /// an open window follows the new density without needing to be reopened.
    /// <para>
    /// Row height and cell padding move together: shrinking the row alone clips the text, and padding
    /// alone leaves the rhythm unchanged.
    /// </para>
    /// </summary>
    public void ApplyDensity(GridDensity density)
    {
        // Also the entry point for a change made in the Settings dialog, so the combo has to follow - guarded,
        // or setting it here raises OnDensityChanged and saves a change that came from the other direction.
        _settingDensity = true;

        try
        {
            DensityCombo.SelectedItem = DensityChoices.First(c => c.Density == density).Label;
        }
        finally
        {
            _settingDensity = false;
        }

        EntriesGrid.RowHeight = GridDensityMetrics.RowHeight(density);

        EntriesGrid.CellStyle = new Style(typeof(DataGridCell), (Style)FindResource(typeof(DataGridCell)))
        {
            Setters =
            {
                new Setter(
                    Control.PaddingProperty,
                    new Thickness(
                        GridDensityMetrics.CellPaddingX,
                        GridDensityMetrics.CellPaddingY(density),
                        GridDensityMetrics.CellPaddingX,
                        GridDensityMetrics.CellPaddingY(density))),
            },
        };
    }

    /// <summary>
    /// Applies the two numeric limits from settings. Separate from <see cref="ApplyDensity"/> only because
    /// density has a control in this window and these do not; both exist so an open window follows Apply.
    /// </summary>
    public void ApplyLimits(int historyLoadLimit, int previewImageMaxWidth)
    {
        var reload = historyLoadLimit != _historyLoadLimit;

        _historyLoadLimit = historyLoadLimit;
        _thumbnailMaxWidth = previewImageMaxWidth;

        // Only the row limit needs a reload; the thumbnail width is read on the next selection change, and
        // re-decoding the current one would be work for a difference nobody asked to see right now.
        if (reload && IsLoaded)
        {
            Refresh();
        }
    }

    /// <summary>
    /// Raised when the user picks a density here, so the host can persist it and keep an open settings
    /// dialog in step. Not raised for a density applied <em>to</em> this window.
    /// </summary>
    public event Action<GridDensity>? DensityChanged;

    private void OnDensityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingDensity || DensityCombo.SelectedItem is not string label)
        {
            return;
        }

        var density = DensityChoices
            .FirstOrDefault(c => string.Equals(c.Label, label, StringComparison.Ordinal))
            .Density;

        // Applied before announcing it, so the effect is on screen in the same frame as the click rather
        // than waiting for the host to hand it back.
        ApplyDensity(density);
        DensityChanged?.Invoke(density);
    }

    /// <summary>
    /// Switches to the clip stack. Exists for the UI smoke harness: the two views differ in their columns,
    /// buttons and status line, so a screenshot of one says nothing about the other.
    /// </summary>
    public void ShowClipsForSmokeTest() => ViewCombo.SelectedIndex = 0;

    /// <summary>
    /// Selects a row by index. Exists for the UI smoke harness, so a screenshot can show the selected
    /// state rather than only the default first-row selection.
    /// </summary>
    public void SelectRowForSmokeTest(int index)
    {
        if (index >= 0 && index < _rows.Count)
        {
            EntriesGrid.SelectedIndex = index;
        }
    }

    /// <summary>Asks for a reload, coalescing rapid successive calls.</summary>
    public void QueueRefresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(QueueRefresh);
            return;
        }

        _refreshDebounce.Stop();
        _refreshDebounce.Start();
    }

    /// <summary>Whether the grid is currently showing the clip stack rather than the history archive.</summary>
    private bool ShowingClips => ViewCombo.SelectedIndex == 0;

    /// <summary>
    /// Loads the clip stack.
    /// <para>
    /// Filtered here rather than in SQL because the archive's full-text index covers <c>history</c> only, and
    /// the stack is bounded by a clip count - a few hundred rows - so a plain scan over previews and tags costs
    /// nothing and needs no second index that could disagree with the first.
    /// </para>
    /// </summary>
    private void LoadClips()
    {
        var term = SearchBox.Text?.Trim();
        var number = 1;

        foreach (var clip in _store.GetOrdered())
        {
            if (!string.IsNullOrEmpty(term)
                && !clip.Preview.Contains(term, StringComparison.OrdinalIgnoreCase)
                && !clip.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _rows.Add(new HistoryRow
            {
                Id = clip.Id,
                Number = number++,
                Preview = clip.Preview,
                CapturedUtc = clip.CreatedUtc,
                Bytes = clip.TotalBytes,
                Kind = clip.Kind,
                IsClip = true,
                Pinned = clip.Pinned,
            });
        }

        var total = _store.Count;

        StatusText.Text = string.IsNullOrWhiteSpace(term)
            ? $"{_rows.Count} clip{(_rows.Count == 1 ? string.Empty : "s")} the Ctrl+V gesture can reach"
            : $"{_rows.Count} of {total} clips match";
    }

    private void LoadHistory()
    {
        var number = 1;

        foreach (var entry in _store.SearchHistory(SearchBox.Text, _historyLoadLimit))
        {
            _rows.Add(new HistoryRow
            {
                Id = entry.Id,
                Number = number++,
                Preview = entry.Preview,
                CapturedUtc = entry.CapturedUtc,
                Bytes = entry.TotalBytes,
                Kind = entry.Kind,
                BlobHash = entry.BlobHash,
            });
        }

        var total = _store.HistoryCount;

        StatusText.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? $"{_rows.Count} of {total} history entries"
            : $"{_rows.Count} matches of {total} history entries";

        // Said outright when the query hit its cap, rather than leaving the two numbers to be compared. A
        // window silently showing a fraction of the store is indistinguishable from an import that failed -
        // which is exactly how an 11,000-entry import was first reported.
        if (_rows.Count < total && string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            StatusText.Text += $" — showing the newest {_rows.Count}; search to reach the rest";
        }
    }

    private void OnViewChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard: SelectionChanged fires while the combo is being populated, before the grid exists.
        if (EntriesGrid is null)
        {
            return;
        }

        ClearButton.Content = ShowingClips ? "Clear _Clips" : "Clear _History";
        PinButton.Visibility = ShowingClips ? Visibility.Visible : Visibility.Collapsed;

        // Disabled rather than hidden in the Clips view: a clip is judged by content alone already, so the option
        // has nothing to change there, and a control that disappears invites the question of where it went.
        IgnoreTimestampCheck.IsEnabled = !ShowingClips;

        // The cue names the store being searched. It said "history" in both views, which is the same confusion
        // between the two stores that the view switch exists to dispel.
        SearchCue.Text = ShowingClips ? "Search clips…  (Ctrl+K)" : "Search history…  (Ctrl+K)";

        Refresh();
    }

    private void Refresh()
    {
        var selectedId = (EntriesGrid.SelectedItem as HistoryRow)?.Id;

        _rows.Clear();

        if (ShowingClips)
        {
            LoadClips();
        }
        else
        {
            LoadHistory();
        }

        UpdateSearchCue();

        // Keep the selection where it was if that row survived the reload.
        if (selectedId is { } id)
        {
            var match = _rows.FirstOrDefault(r => r.Id == id);

            if (match is not null)
            {
                EntriesGrid.SelectedItem = match;
                return;
            }
        }

        if (_rows.Count > 0)
        {
            EntriesGrid.SelectedIndex = 0;
        }
        else
        {
            ShowPreview(null);
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchCue();
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>
    /// Shows or hides the placeholder text. WPF has no cue-banner support, so the hint is a TextBlock
    /// layered over the box; it must be hidden the moment there is content or it shows through.
    /// </summary>
    private void UpdateSearchCue()
        => SearchCue.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>Moves focus to the search box and selects what is there, so typing replaces it.</summary>
    public ICommand FocusSearchCommand => _focusSearch ??= new RelayCommand(() =>
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    });

    private ICommand? _focusSearch;

    /// <summary>
    /// Window-level keys. Handled in the preview phase so they work wherever focus happens to be,
    /// but each is careful not to steal a key the focused control legitimately needs.
    /// </summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                // Clears the filter if there is one, otherwise closes - the usual two-stage Escape.
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Clear();
                    e.Handled = true;
                }
                else
                {
                    Close();
                }

                break;

            case Key.Enter when EntriesGrid.SelectedItem is not null && !SearchBox.IsKeyboardFocusWithin:
                CopySelectionToClipboard();
                e.Handled = true;
                break;

            case Key.Enter when SearchBox.IsKeyboardFocusWithin && _rows.Count > 0:
                // From the search box, Enter moves into the results rather than copying: copying the
                // top hit on the first Enter would be a surprising, hard-to-undo action.
                EntriesGrid.Focus();

                if (EntriesGrid.SelectedItem is null)
                {
                    EntriesGrid.SelectedIndex = 0;
                }

                e.Handled = true;
                break;

            case Key.Delete when !SearchBox.IsKeyboardFocusWithin:
                OnDeleteClicked(sender, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => ShowPreview(EntriesGrid.SelectedItem as HistoryRow);

    private void ShowPreview(HistoryRow? row)
    {
        if (row is null)
        {
            PreviewHeader.Text = "Preview";
            PreviewBox.Text = string.Empty;
            PreviewBox.Visibility = Visibility.Visible;
            ShowImage(null, null, null, 0, 0);
            return;
        }

        PreviewHeader.Text = $"#{row.Number}  ·  {row.KindText}  ·  {row.SizeText}  ·  {row.LocalTimeText}";

        if (row.Kind == ClipKind.Image && row.BlobHash is { Length: > 0 })
        {
            var bytes = _store.Blobs.TryRead(row.BlobHash);
            var bitmap = bytes is null ? null : TryDecode(bytes);

            if (bitmap is not null)
            {
                // Whole pane: the picture is the content, and there is no path to show above it. A stored image
                // is decoded in full, so its own dimensions are the true ones here.
                PreviewScroller.Visibility = Visibility.Collapsed;
                ShowImage(bitmap, null, bytes!.LongLength, bitmap.PixelWidth, bitmap.PixelHeight);
                return;
            }
        }

        PreviewScroller.Visibility = Visibility.Visible;
        PreviewBox.Text = row.Preview;

        // A copied image FILE: the path stays visible and the picture goes underneath it, which is the one
        // case where both halves of the pane are wanted at once.
        if (row.Kind == ClipKind.Files && TryLoadFirstImageFile(row.Preview) is { } file)
        {
            ShowImage(file.Bitmap, file.Path, file.FileBytes, file.PixelWidth, file.PixelHeight);
            return;
        }

        ShowImage(null, null, null, 0, 0);
    }

    /// <summary>
    /// Shows or hides the thumbnail and the footer together, and sizes the two content rows to suit.
    /// <para>
    /// Row heights are set here rather than in XAML because the same pane serves three shapes: text only,
    /// picture only, and a path above a picture. Collapsing a row to zero is what keeps the text from taking
    /// half the pane when there is a thumbnail to show.
    /// </para>
    /// </summary>
    private void ShowImage(BitmapSource? bitmap, string? path, long? storedBytes, int pixelWidth, int pixelHeight)
    {
        if (bitmap is null)
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewFooter.Visibility = Visibility.Collapsed;
            PreviewTextRow.Height = new GridLength(1, GridUnitType.Star);
            PreviewImageRow.Height = new GridLength(0);
            return;
        }

        PreviewImage.Source = bitmap;
        PreviewImage.Visibility = Visibility.Visible;

        // With a path above it the text gets only what it needs; without one it gets nothing.
        PreviewTextRow.Height = path is null ? new GridLength(0) : GridLength.Auto;
        PreviewImageRow.Height = new GridLength(1, GridUnitType.Star);

        // The file's dimensions, passed in rather than read off the bitmap: a thumbnail has been resized, so
        // its own PixelWidth is the size we asked for and not the image's.
        PreviewDimensions.Text = $"{pixelWidth} × {pixelHeight}";
        PreviewBytes.Text = storedBytes is { } bytes ? FormatBytes(bytes) : string.Empty;
        PreviewFooter.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Loads the first path in a file list that names an image we can decode.
    /// <para>
    /// The paths come back out of the stored description, since a history row keeps no <c>CF_HDROP</c> - see
    /// <see cref="FileListPreview.TryReadPathsFromDescription"/>. Only the first is used: the pane has room for
    /// one picture, and a copy of forty photographs should not read forty files to fill it.
    /// </para>
    /// </summary>
    private ImageFilePreview? TryLoadFirstImageFile(string? preview)
    {
        foreach (var path in FileListPreview.TryReadPathsFromDescription(preview))
        {
            if (!ImageFileExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);

                if (!info.Exists)
                {
                    continue;
                }

                using var stream = info.OpenRead();

                // The real dimensions come from the header, before any decode, because the decoded bitmap
                // cannot report them: DecodePixelWidth resizes, so PixelWidth afterwards is the size WE asked
                // for. Reporting that as the image's resolution was wrong in both directions - it claimed
                // 640x802 for a 1016x1274 photograph and 640x640 for a 2x2 test file.
                var header = BitmapDecoder.Create(
                    stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);

                var frame = header.Frames[0];
                var pixelWidth = frame.PixelWidth;
                var pixelHeight = frame.PixelHeight;

                stream.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;

                // Only ever downwards. Capping a large photograph avoids decoding forty megapixels to fill a
                // pane a few hundred pixels wide; applying the same cap to a small image would enlarge it,
                // which is both pointless and how the wrong resolution got reported.
                if (pixelWidth > _thumbnailMaxWidth)
                {
                    bitmap.DecodePixelWidth = _thumbnailMaxWidth;
                }

                // OnLoad, so the bitmap does not depend on the stream after this using block closes it.
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                return new ImageFilePreview(bitmap, info.FullName, info.Length, pixelWidth, pixelHeight);
            }
            catch (Exception)
            {
                // Not decodable, gone, or unreadable. The next candidate gets a turn; the path text is still
                // shown either way, so nothing is lost by giving up here.
            }
        }

        return null;
    }

    /// <summary>What the pane needs about a copied image file. The dimensions are the file's, not the thumbnail's.</summary>
    private sealed record ImageFilePreview(
        BitmapSource Bitmap,
        string Path,
        long FileBytes,
        int PixelWidth,
        int PixelHeight);

    /// <summary>Widest thumbnail worth decoding. The pane is a few hundred pixels; this leaves room to enlarge it.</summary>
    internal const int DefaultThumbnailMaxWidth = 640;

    /// <summary>
    /// Extensions worth attempting. An allow-list rather than "try everything": the alternative is opening and
    /// failing on every .exe and .zip in a copied folder, on the UI thread, on every selection change.
    /// </summary>
    private static readonly HashSet<string> ImageFileExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp",
        };

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

    private static BitmapSource? TryDecode(byte[] bytes)
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
            return null;
        }
    }

    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => CopySelectionToClipboard();

    private void OnCopyClicked(object sender, RoutedEventArgs e) => CopySelectionToClipboard();

    /// <summary>
    /// Puts the selected entry back on the clipboard.
    /// <para>
    /// History stores only the preview text and, for images, a rendered BMP blob - not the full
    /// original format set. So this restores text or the image rather than pretending to reproduce the
    /// exact multi-format clip, which history was never able to hold.
    /// </para>
    /// <para>
    /// The image branch is load-bearing. Without it this fell through to the text path for every row,
    /// and an image row's preview text is the literal string "[image]" - so copying a picture from
    /// history silently put that word on the clipboard instead of the picture.
    /// </para>
    /// </summary>
    private void CopySelectionToClipboard()
    {
        if (EntriesGrid.SelectedItem is not HistoryRow row)
        {
            return;
        }

        // A clip still has every format it was copied with, so it is replayed exactly - which is the whole
        // difference between the two views. A history entry can only offer its flattened record.
        if (row.IsClip)
        {
            var stored = _store.GetPayloads(row.Id);

            if (stored.Count > 0)
            {
                _selfWrites.NoteWrite(new ClipboardSnapshot(stored, null, row.Kind, null).ContentHash);

                if (!_clipboard.TryWrite(stored))
                {
                    StatusText.Text = "Could not open the clipboard - another application may be holding it.";
                    return;
                }

                // To the front as well, which is what Q does during the gesture. Copying a clip and then
                // finding Ctrl+V still offering a different one would be indefensible.
                _store.MoveToFront(row.Id);
                Refresh();

                StatusText.Text =
                    $"Copied with all {stored.Count} format{(stored.Count == 1 ? string.Empty : "s")} intact, "
                    + "and moved to the front of the stack.";

                return;
            }
        }

        var payloads = TryBuildImagePayloads(row);
        var truncated = false;

        if (payloads is null)
        {
            // Prefer the archived full text over the preview column, which is capped at
            // ClipStore.PreviewMaxChars. Copying the preview was handing back a silently shortened clip for
            // anything longer - and for an entry no longer in the stack, this is the only way to get it back.
            var full = TryReadArchivedText(row);

            // Entries captured before full text was archived have nothing else to offer, so say so rather than
            // producing a quietly incomplete copy.
            truncated = full is null && row.Preview.Length >= _store.PreviewMaxChars;
            payloads = Win32ClipboardAccess.TextOnlyPayloads(full ?? row.Preview);
        }

        var kind = payloads[0].FormatId == CfDib ? ClipKind.Image : ClipKind.Text;
        var snapshot = new ClipboardSnapshot(payloads, kind == ClipKind.Text ? row.Preview : null, kind, null);

        // Registered before writing so the capture service recognises this as our own write and does not file
        // it as a brand-new clip - it is added to the stack explicitly below instead, which also keeps it out
        // of the history archive, where it already has a row.
        _selfWrites.NoteWrite(snapshot.ContentHash);

        if (!_clipboard.TryWrite(payloads))
        {
            StatusText.Text = "Could not open the clipboard - another application may be holding it.";
            return;
        }

        // The point of the archive. Until this line, copying a history entry put it on the system clipboard and
        // nowhere else - so the gesture, which pastes from the stack rather than from the clipboard, went on
        // offering something entirely different, and an imported history of thousands of entries was searchable
        // and otherwise useless. Adding it makes it the newest clip, so Ctrl+V offers it first.
        //
        // Duplicates allowed regardless of the setting: the user has explicitly asked for this entry, and
        // silently declining because an identical clip exists somewhere in the stack would look like the button
        // had done nothing.
        _store.Add(snapshot, allowDuplicates: true);
        Refresh();

        StatusText.Text = kind == ClipKind.Image
            ? "Image copied, and added to the stack as the newest clip."
            : truncated
                ? "Copied and added to the stack - but this entry predates full-text archiving, so only the "
                    + $"first {_store.PreviewMaxChars:N0} characters were kept."
                : "Copied, and added to the stack as the newest clip - Ctrl+V will offer it first.";
    }

    /// <summary>
    /// The archived full text for an entry too long for the preview column, or null when the row predates
    /// full-text archiving, is not text, or its blob has been collected.
    /// </summary>
    private string? TryReadArchivedText(HistoryRow row)
    {
        if (row.Kind != ClipKind.Text || row.BlobHash is not { Length: > 0 })
        {
            return null;
        }

        var bytes = _store.Blobs.TryRead(row.BlobHash);

        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Rebuilds a <c>CF_DIB</c> payload from a history image blob, or null when the row is not an
    /// image or its blob has been collected.
    /// </summary>
    private IReadOnlyList<ClipPayload>? TryBuildImagePayloads(HistoryRow row)
    {
        if (row.Kind != ClipKind.Image || row.BlobHash is not { Length: > 0 })
        {
            return null;
        }

        var bitmapFile = _store.Blobs.TryRead(row.BlobHash);
        var dib = bitmapFile is null ? null : DibConverter.TryExtractDib(bitmapFile);

        return dib is null ? null : [new ClipPayload(CfDib, null, dib)];
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        var selected = EntriesGrid.SelectedItems.OfType<HistoryRow>().ToList();

        if (selected.Count == 0)
        {
            return;
        }

        foreach (var row in selected)
        {
            if (row.IsClip)
            {
                _store.Delete(row.Id);
            }
            else
            {
                _store.DeleteHistory(row.Id);
            }
        }

        Refresh();

        StatusText.Text = selected[0].IsClip
            ? $"Deleted {selected.Count} clip{(selected.Count == 1 ? string.Empty : "s")}. "
                + "The history entries are still there."
            : $"Deleted {selected.Count} histor{(selected.Count == 1 ? "y entry" : "y entries")}.";
    }

    /// <summary>
    /// Clears the clip stack, keeping pinned clips. The counterpart of the gesture's DELETE ALL, and now the
    /// discoverable route to it - the keystroke remains for anyone already in the middle of a paste.
    /// </summary>
    private void ClearClips()
    {
        var total = _store.Count;
        var pinned = _store.GetOrdered().Count(static c => c.Pinned);
        var going = total - pinned;

        if (going == 0)
        {
            StatusText.Text = total == 0
                ? "There are no clips to clear."
                : $"All {total} clip{(total == 1 ? " is" : "s are")} pinned, so nothing would be cleared.";
            return;
        }

        var message = pinned == 0
            ? "This cannot be undone. The history archive is not affected."
            : $"This cannot be undone. {pinned} pinned clip{(pinned == 1 ? string.Empty : "s")} will be kept, "
                + "and the history archive is not affected.";

        if (MessageDialog.Show(
                message,
                headline: $"Clear {going} clip{(going == 1 ? string.Empty : "s")} from the Ctrl+V stack?",
                kind: DialogKind.Warning,
                buttons: DialogButtons.OkCancel,
                owner: this) != DialogResultKind.Accepted)
        {
            return;
        }

        _store.DeleteAll(includePinned: false);
        Refresh();

        StatusText.Text = $"Cleared {going} clip{(going == 1 ? string.Empty : "s")}. "
            + "The history entries are still there.";
    }

    /// <summary>Pins or unpins the selected clips. Pinned clips sort first and survive DELETE ALL.</summary>
    private void OnPinClicked(object sender, RoutedEventArgs e)
    {
        var selected = EntriesGrid.SelectedItems.OfType<HistoryRow>().Where(static r => r.IsClip).ToList();

        if (selected.Count == 0)
        {
            return;
        }

        // Whatever the first selection is not, so a mixed selection ends up uniform rather than each item
        // flipping to the opposite of itself.
        var pin = !selected[0].Pinned;

        foreach (var row in selected)
        {
            _store.SetPinned(row.Id, pin);
        }

        Refresh();

        StatusText.Text = pin
            ? $"Pinned {selected.Count} clip{(selected.Count == 1 ? string.Empty : "s")}."
            : $"Unpinned {selected.Count} clip{(selected.Count == 1 ? string.Empty : "s")}.";
    }

    /// <summary>
    /// Clears the history archive, and says plainly that the clip stack is a different thing.
    /// <para>
    /// The distinction is deliberate - an archive you search versus the stack Ctrl+V walks - but it was
    /// invisible: a button labelled "Clear All" in a window called Clipboard History was read, reasonably, as
    /// clearing everything, and it was reported as clips still appearing afterwards. The store was behaving
    /// correctly and the words were wrong.
    /// </para>
    /// </summary>
    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        if (ShowingClips)
        {
            ClearClips();
            return;
        }

        var clips = _store.Count;

        var accepted = MessageDialog.Show(
            "This cannot be undone.\n\n"
                + $"The {clips} clip{(clips == 1 ? string.Empty : "s")} the Ctrl+V gesture walks are a separate "
                + "store and are NOT affected. To clear those, hold Ctrl, tap X three times until the red "
                + "DELETE ALL banner appears, then release — it asks before deleting.",
            headline: $"Delete all {_store.HistoryCount} history entries?",
            kind: DialogKind.Warning,
            buttons: DialogButtons.OkCancel,
            owner: this) == DialogResultKind.Accepted;

        if (!accepted)
        {
            return;
        }

        _store.ClearHistory();
        _store.CollectGarbage();
        Refresh();

        StatusText.Text = clips == 0
            ? "History cleared."
            : $"History cleared. {clips} clip{(clips == 1 ? string.Empty : "s")} still available to Ctrl+V.";
    }

    /// <summary>
    /// Collapses exact duplicates in whichever store is on screen.
    /// <para>
    /// Confirmed, because it deletes rows and cannot be undone - but stated as what it is: the survivors are
    /// indistinguishable from what is removed. It exists because imports were not idempotent until
    /// <c>AddHistoryIfAbsent</c>, so anyone who ran the Clipjump import more than once has a copy per run and
    /// no other way to get rid of them short of clearing everything.
    /// </para>
    /// </summary>
    private void OnDeduplicateClicked(object sender, RoutedEventArgs e)
    {
        var clips = ShowingClips;
        var before = clips ? _store.Count : _store.HistoryCount;

        // Only history honours it; a clip is judged by content already. The check box is disabled in the Clips
        // view, but read defensively rather than trusted - IsEnabled is presentation, and this deletes rows.
        var ignoreTimestamp = !clips && IgnoreTimestampCheck.IsChecked == true;

        var accepted = MessageDialog.Show(
            "Entries that are an exact duplicate of another are removed, keeping one of each. Nothing that "
                + "differs in any way is touched.\n\n"
                + (clips
                    ? "A clip is judged by its content, which is the same test the gesture uses to recognise a "
                        + "re-copy. The newest of each set is kept, and a pinned one always wins."
                    : ignoreTimestamp
                        // Spelled out at length because it is the destructive one: it collapses a phrase copied
                        // every day for a year into a single entry, and the prompt is the last chance to say so.
                        ? "Ignore time is ticked, so an entry is judged by its kind, its text and its image "
                            + "only - the same thing copied on different days counts as one, and the most "
                            + "recent is kept. This removes far more than the ordinary sweep does."
                        : "An entry is judged by its timestamp, its kind, its text and its image, so two "
                            + "screenshots taken in the same second are not mistaken for one. The oldest of each "
                            + "set is kept.")
                + "\n\nThis cannot be undone.",
            headline: clips ? "Remove duplicate clips?" : "Remove duplicate history entries?",
            kind: DialogKind.Warning,
            buttons: DialogButtons.OkCancel,
            owner: this) == DialogResultKind.Accepted;

        if (!accepted)
        {
            return;
        }

        var removed = clips ? _store.DeduplicateClips() : _store.DeduplicateHistory(ignoreTimestamp);

        if (removed > 0)
        {
            // Only when something went: the blob sweep walks every row in both stores, which is not work worth
            // doing to discover that nothing was orphaned.
            _store.CollectGarbage();
        }

        Refresh();

        var noun = clips ? "clip" : "history entry";
        var plural = clips ? "clips" : "history entries";

        StatusText.Text = removed == 0
            ? $"No duplicates found among the {before} {plural}."
            : $"Removed {removed} duplicate {(removed == 1 ? noun : plural)}. {before - removed} left.";
    }
}
