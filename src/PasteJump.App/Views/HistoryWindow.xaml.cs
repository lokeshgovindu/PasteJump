using System.Collections.ObjectModel;
using System.Globalization;
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

    private readonly ClipStore _store;
    private readonly IClipboardAccess _clipboard;
    private readonly SelfWriteGuard _selfWrites;
    private readonly FormatterRegistry _formatters;
    private readonly ObservableCollection<HistoryRow> _rows = [];
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _refreshDebounce;

    public HistoryWindow(
        ClipStore store,
        IClipboardAccess clipboard,
        SelfWriteGuard selfWrites,
        FormatterRegistry formatters,
        GridDensity density = GridDensity.Cozy)
    {
        _store = store;
        _clipboard = clipboard;
        _selfWrites = selfWrites;
        _formatters = formatters;

        InitializeComponent();

        EntriesGrid.ItemsSource = _rows;
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

    private void Refresh()
    {
        var selectedId = (EntriesGrid.SelectedItem as HistoryRow)?.Id;

        var entries = _store.SearchHistory(SearchBox.Text);

        _rows.Clear();

        var number = 1;

        foreach (var entry in entries)
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
            ? $"{_rows.Count} of {total} entries"
            : $"{_rows.Count} matches of {total} entries";

        // Said outright when the query hit its cap, rather than leaving the two numbers to be compared. A
        // window silently showing a fraction of the store is indistinguishable from an import that failed -
        // which is exactly how an 11,000-entry import was first reported.
        if (_rows.Count < total && string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            StatusText.Text += $" — showing the newest {_rows.Count}; search to reach the rest";
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
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;
            return;
        }

        PreviewHeader.Text = $"#{row.Number}  ·  {row.KindText}  ·  {row.SizeText}  ·  {row.LocalTimeText}";

        if (row.Kind == ClipKind.Image && row.BlobHash is { Length: > 0 })
        {
            var bytes = _store.Blobs.TryRead(row.BlobHash);
            var bitmap = bytes is null ? null : TryDecode(bytes);

            if (bitmap is not null)
            {
                PreviewImage.Source = bitmap;
                PreviewImage.Visibility = Visibility.Visible;
                PreviewScroller.Visibility = Visibility.Collapsed;
                return;
            }
        }

        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewScroller.Visibility = Visibility.Visible;
        PreviewBox.Text = row.Preview;
    }

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

        var payloads = TryBuildImagePayloads(row) ?? Win32ClipboardAccess.TextOnlyPayloads(row.Preview);

        var kind = payloads[0].FormatId == CfDib ? ClipKind.Image : ClipKind.Text;
        var snapshot = new ClipboardSnapshot(payloads, kind == ClipKind.Text ? row.Preview : null, kind, null);

        // Registered before writing so the capture service recognises this as our own write and
        // does not file it as a brand-new clip.
        _selfWrites.NoteWrite(snapshot.ContentHash);

        StatusText.Text = _clipboard.TryWrite(payloads)
            ? kind == ClipKind.Image ? "Image copied to clipboard." : "Copied to clipboard."
            : "Could not open the clipboard - another application may be holding it.";
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
            _store.DeleteHistory(row.Id);
        }

        Refresh();
        StatusText.Text = $"Deleted {selected.Count} entr{(selected.Count == 1 ? "y" : "ies")}.";
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        var accepted = MessageDialog.Show(
            "This cannot be undone.",
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
        StatusText.Text = "History cleared.";
    }
}
