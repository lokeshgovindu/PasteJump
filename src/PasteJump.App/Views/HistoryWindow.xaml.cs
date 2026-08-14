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
using PasteJump.Core.Paste;
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
    /// Resolves this row's thumbnail, set by whoever built the row. Null for rows that cannot have one.
    /// <para>
    /// A delegate rather than the picture itself, because the row is a view-model with no access to the store and
    /// no business acquiring one - and because loading every thumbnail up front would read a megabyte per image
    /// row to fill a list nobody has hovered over yet.
    /// </para>
    /// </summary>
    public Func<HistoryRow, BitmapSource?>? ThumbnailResolver { get; init; }

    private BitmapSource? _thumbnail;
    private bool _thumbnailTried;

    /// <summary>
    /// A small picture for the row tooltip, loaded on first read and then remembered.
    /// <para>
    /// Lazy by binding: nothing asks for this until WPF realises the tooltip, so hovering one row reads one image
    /// and scrolling past a thousand reads none. The failure is remembered too - <see cref="_thumbnailTried"/> -
    /// or a row whose picture cannot be decoded would retry on every hover.
    /// </para>
    /// </summary>
    public BitmapSource? Thumbnail
    {
        get
        {
            if (_thumbnailTried)
            {
                return _thumbnail;
            }

            _thumbnailTried = true;
            _thumbnail = ThumbnailResolver?.Invoke(this);

            return _thumbnail;
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
    /// <c>CF_DIBV5</c>, the format Windows offers for an image with an alpha channel. Read as a fallback wherever
    /// <see cref="CfDib"/> is, because a clip captured from an application that offered only V5 has no plain DIB and
    /// would otherwise look like a clip with no picture in it at all.
    /// </summary>
    private const uint CfDibV5 = 17;

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

    /// <summary>
    /// What goes between clips when several are copied at once, in its escaped settings form. Held escaped and
    /// parsed at the point of use, so a change from the Settings dialog needs no conversion on the way in.
    /// </summary>
    private string _joinSeparator;

    public HistoryWindow(
        ClipStore store,
        IClipboardAccess clipboard,
        SelfWriteGuard selfWrites,
        FormatterRegistry formatters,
        GridDensity density = GridDensity.Cozy,
        int historyLoadLimit = ClipStore.DefaultHistoryLimit,
        int previewImageMaxWidth = DefaultThumbnailMaxWidth,
        string joinSeparator = ClipJoiner.DefaultSeparator)
    {
        _store = store;
        _clipboard = clipboard;
        _selfWrites = selfWrites;
        _formatters = formatters;
        _historyLoadLimit = historyLoadLimit;
        _thumbnailMaxWidth = previewImageMaxWidth;
        _joinSeparator = joinSeparator;

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
    public void ApplyLimits(int historyLoadLimit, int previewImageMaxWidth, string joinSeparator)
    {
        var reload = historyLoadLimit != _historyLoadLimit;

        _historyLoadLimit = historyLoadLimit;
        _thumbnailMaxWidth = previewImageMaxWidth;
        _joinSeparator = joinSeparator;

        // The Copy button's tooltip names the separator, so it has to be rebuilt rather than left saying what
        // the old one was.
        UpdateCopyButton();

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
    /// Test hook: selects the first row of a given kind, and reports whether there was one.
    /// <para>
    /// By kind rather than by index, because an index is not stable across a harness run: earlier cases copy and
    /// join, both of which add a clip and move one to the front, so "row 5" was the seeded image in the first
    /// theme pass and something else in the second. That produced a check which passed once and failed once, which
    /// is worse than one that simply fails.
    /// </para>
    /// </summary>
    public bool SelectFirstRowOfKindForSmokeTest(ClipKind kind)
    {
        for (var i = 0; i < EntriesGrid.Items.Count; i++)
        {
            if (EntriesGrid.Items[i] is HistoryRow row && row.Kind == kind)
            {
                SelectRowForSmokeTest(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Test hook: whether the selected row resolved a tooltip thumbnail.
    /// <para>
    /// The tooltip itself lives in a popup the harness cannot render, so this asserts the part that can break
    /// quietly: the resolver reaching the store and the bytes decoding. A tooltip that shows no picture looks
    /// exactly like one for a text row.
    /// </para>
    /// </summary>
    public bool SelectedRowHasThumbnailForSmokeTest =>
        (EntriesGrid.SelectedItem as HistoryRow)?.Thumbnail is not null;

    /// <summary>
    /// Test hook: whether the preview pane is showing a picture rather than text.
    /// <para>
    /// Exists because a screenshot is not an assertion. An image clip whose picture failed to load looks like a
    /// pane with <c>[image]</c> in it, which is also what a correctly rendered <em>text</em> clip looks like to
    /// anything comparing pixels - so the Clips view could lose its previews entirely, as it had, without any
    /// shot going missing or any check going red.
    /// </para>
    /// </summary>
    public bool PreviewShowsPictureForSmokeTest =>
        PreviewImageHost.Visibility == Visibility.Visible && PreviewImage.Source is not null;

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

    /// <summary>
    /// Selects several rows and joins them, for the UI smoke harness.
    /// <para>
    /// Two states nothing else renders: the Copy button relabelled to <c>Copy Joined</c>, and the status line a
    /// join produces. Both need more than one row selected, which no other case does. The harness's clipboard
    /// accepts writes without touching the real one, so this runs the whole path - read the text, join it, write
    /// it, add the result to the stack - and the shot shows what the user would see.
    /// </para>
    /// </summary>
    public void SelectFirstRowsForSmokeTest(int count)
    {
        EntriesGrid.SelectedItems.Clear();

        foreach (var row in _rows.Take(count))
        {
            EntriesGrid.SelectedItems.Add(row);
        }

        UpdateCopyButton();
    }

    /// <summary>
    /// Joins whatever is selected. Split from <see cref="SelectFirstRowsForSmokeTest"/> because the two produce
    /// different shots: the relabelled button is only visible <em>before</em> the join, since the reload
    /// afterwards drops the multi-selection and the label follows it back.
    /// </summary>
    public void JoinSelectionForSmokeTest() => CopySelectionJoined();

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
                ThumbnailResolver = TryLoadRowThumbnail,
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
                ThumbnailResolver = TryLoadRowThumbnail,
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
    {
        var empty = string.IsNullOrEmpty(SearchBox.Text);

        SearchCue.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        // The clear button appears only when there is something to clear - a cross on an empty box invites a click
        // that does nothing, and it crowds the cue text.
        SearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Clears the search and returns the caret to the box, since the button itself is not focusable. Esc already
    /// did this from the keyboard; the cross is the same action for the mouse.
    /// </summary>
    private void OnSearchClearClicked(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

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

            // Through the button's own handler rather than straight to CopySelectionToClipboard, so Enter joins
            // when several rows are selected. Enter is documented as the keyboard equivalent of Copy, and a
            // button reading "Copy Joined" while Enter quietly copied one row would make a liar of it.
            case Key.Enter when EntriesGrid.SelectedItem is not null && !SearchBox.IsKeyboardFocusWithin:
                OnCopyClicked(sender, new RoutedEventArgs());
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
    {
        ShowPreview(EntriesGrid.SelectedItem as HistoryRow);
        UpdateCopyButton();
    }

    /// <summary>
    /// Relabels Copy to say what it will actually do with the current selection.
    /// <para>
    /// The alternative was a sixth toolbar button. This is one action with two shapes rather than two actions,
    /// and joining is not discoverable at all if nothing changes when several rows are selected - the user has
    /// to already know. The access key stays on C either way, because an access key that moves with the
    /// selection is worse than no label change at all.
    /// </para>
    /// </summary>
    private void UpdateCopyButton()
    {
        var several = EntriesGrid.SelectedItems.Count > 1;

        CopyButton.Content = several ? "_Copy Joined" : "_Copy";

        CopyButton.ToolTip = several
            ? "Copy the selected entries as ONE clip, their text joined with "
                + $"{ClipJoiner.Describe(ClipJoiner.ParseSeparator(_joinSeparator))}, in the order shown. "
                + "Change the separator in Settings, History. Entries with no text, such as images, are left out."
            : "Put the selected entry back on the clipboard (Enter, or double-click a row). Select several rows "
                + "to copy them joined into one clip.";
    }

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

        if (row.Kind == ClipKind.Image && TryReadImageBytes(row) is { } picture)
        {
            var bitmap = TryDecode(picture.Bytes);

            if (bitmap is not null)
            {
                // Whole pane: the picture is the content, and there is no path to show above it. A stored image
                // is decoded in full, so its own dimensions are the true ones here.
                PreviewScroller.Visibility = Visibility.Collapsed;
                ShowImage(bitmap, null, DescribeImageBytes(row, picture), bitmap.PixelWidth, bitmap.PixelHeight);
                return;
            }
        }

        PreviewScroller.Visibility = Visibility.Visible;
        PreviewBox.Text = row.Preview;

        // A copied image FILE: the path stays visible and the picture goes underneath it, which is the one
        // case where both halves of the pane are wanted at once.
        if (row.Kind == ClipKind.Files && TryLoadFirstImageFile(row.Preview) is { } file)
        {
            ShowImage(file.Bitmap, file.Path, FormatBytes(file.FileBytes), file.PixelWidth, file.PixelHeight);
            return;
        }

        ShowImage(null, null, null, 0, 0);
    }

    /// <summary>
    /// The picture behind an image row, as a decodable bitmap file, or null when there is not one.
    /// <para>
    /// <b>Two stores, two places a picture lives, and that is the whole of this method.</b> A history entry keeps
    /// one flattened record plus a blob addressed by hash; a clip keeps every clipboard format it was copied with,
    /// and no blob. So a clip row has no <see cref="HistoryRow.BlobHash"/> - and this pane used to test only for
    /// that, which meant selecting an image in the Clips view fell through to the text branch and drew the
    /// <c>[image]</c> placeholder. Reported, and the row class had documented the intended behaviour all along:
    /// "its image preview comes from the payloads". It never did.
    /// </para>
    /// <para>
    /// Note that Copy was never affected, which is why this survived: it takes a clip's payloads directly, several
    /// hundred lines below, and only history rows reach <c>TryBuildImagePayloads</c>. The two paths had drifted.
    /// </para>
    /// </summary>
    /// <param name="Bytes">A decodable bitmap file.</param>
    /// <param name="PictureBytes">How big just the picture is, which is not what the row's Size column says.</param>
    /// <param name="FormatCount">
    /// How many clipboard formats the clip stores, or zero for a history entry, which keeps one.
    /// </param>
    private sealed record PreviewPicture(byte[] Bytes, long PictureBytes, int FormatCount);

    private PreviewPicture? TryReadImageBytes(HistoryRow row)
    {
        if (!row.IsClip)
        {
            if (row.BlobHash is not { Length: > 0 } hash || _store.Blobs.TryRead(hash) is not { } blob)
            {
                return null;
            }

            return new PreviewPicture(blob, blob.LongLength, 0);
        }

        var payloads = _store.GetPayloads(row.Id);

        var dib = payloads.FirstOrDefault(static p => p.FormatId == CfDib)
            ?? payloads.FirstOrDefault(static p => p.FormatId == CfDibV5);

        // A DIB is raw pixels with a header the imaging stack will not read; the converter prepends the 14-byte
        // file header that turns it into something BitmapDecoder accepts. Same call the overlay makes.
        var file = dib is null ? null : DibConverter.TryCreateBitmapFile(dib.Data);

        return file is null ? null : new PreviewPicture(file, file.LongLength, payloads.Count);
    }

    /// <summary>
    /// Shows or hides the thumbnail and the footer together, and sizes the two content rows to suit.
    /// <para>
    /// Row heights are set here rather than in XAML because the same pane serves three shapes: text only,
    /// picture only, and a path above a picture. Collapsing a row to zero is what keeps the text from taking
    /// half the pane when there is a thumbnail to show.
    /// </para>
    /// </summary>
    /// <summary>How wide a tooltip thumbnail is decoded. Medium: big enough to recognise, small enough to be free.</summary>
    private const int TooltipThumbnailWidth = 260;

    /// <summary>
    /// The picture for a row's tooltip, or null when the row has none.
    /// <para>
    /// <b>Decoded small, not shrunk small.</b> <c>DecodePixelWidth</c> makes the decoder produce a 260px image
    /// directly, so a 3 MB screenshot never becomes a full-size bitmap in memory on the way to a tooltip. Loading
    /// the full picture and letting the Image element scale it would cost the same as the preview pane, per hover.
    /// </para>
    /// <para>
    /// Only image rows, deliberately. A file copy would mean reading from disk while the pointer moves across a
    /// list, which is the one thing a hover must not do.
    /// </para>
    /// </summary>
    private BitmapSource? TryLoadRowThumbnail(HistoryRow row)
    {
        if (row.Kind != ClipKind.Image || TryReadImageBytes(row) is not { } picture)
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();

            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = TooltipThumbnailWidth;
            bitmap.StreamSource = new MemoryStream(picture.Bytes);
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception)
        {
            // A clip whose bytes will not decode is not an error worth reporting from a tooltip - the pane it was
            // hovering over says the same thing more usefully.
            return null;
        }
    }

    // ---- image zoom and pan

    /// <summary>
    /// Zoom, or null while fitting to the pane.
    /// <para>
    /// Null rather than a flag plus a number, because "fitting" is not a zoom level: the scale it implies changes
    /// with the pane, so storing one would go stale the moment the splitter moved. The readout asks for the
    /// effective scale instead - see <see cref="ApplyZoom"/>.
    /// </para>
    /// </summary>
    private double? _zoom;

    /// <summary>Where a pan started, in scroller coordinates, or null when no drag is in progress.</summary>
    private Point? _panFrom;
    private double _panOffsetX;
    private double _panOffsetY;

    private const double MinZoom = 0.05;
    private const double MaxZoom = 8.0;

    /// <summary>
    /// Applies <see cref="_zoom"/>, or the fit scale when it is null, and updates the readout.
    /// <para>
    /// <b>Never upscales in Fit mode.</b> A 16x16 icon blown up to fill the pane is a blurred mess that also
    /// misrepresents what was copied, so the fit scale is capped at 1 - which is what the old
    /// <c>StretchDirection="DownOnly"</c> did, kept deliberately.
    /// </para>
    /// </summary>
    private void ApplyZoom()
    {
        if (PreviewImage.Source is not { } source)
        {
            return;
        }

        // Selection runs before layout, so on the first frame the scroller has no viewport yet - and a fit scale
        // divided by a viewport of zero came out at 0.3%, which read as the picture having disappeared. Caught by
        // rendering it. Wait for the layout pass rather than guessing a size: LayoutUpdated fires once per pass,
        // so this re-enters exactly as often as the layout actually changes and cannot spin.
        if (_zoom is null && (ImageScroller.ViewportWidth <= 1 || ImageScroller.ViewportHeight <= 1))
        {
            ImageScale.ScaleX = 1;
            ImageScale.ScaleY = 1;
            ZoomReadout.Text = "Fit";

            ImageScroller.LayoutUpdated -= OnLayoutSettledForFit;
            ImageScroller.LayoutUpdated += OnLayoutSettledForFit;
            return;
        }

        var scale = _zoom ?? FitScale(source);

        ImageScale.ScaleX = scale;
        ImageScale.ScaleY = scale;

        ZoomReadout.Text = _zoom is null
            ? $"Fit · {scale * 100:0}%"
            : $"{scale * 100:0}%";

        // In Fit mode there is nothing to scroll to, and leaving the bars enabled lets a rounding error put a
        // scrollbar on a picture that is entirely visible.
        var fitting = _zoom is null;
        ImageScroller.HorizontalScrollBarVisibility = fitting ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ImageScroller.VerticalScrollBarVisibility = fitting ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

        ZoomFitButton.IsEnabled = !fitting;
        ZoomActualButton.IsEnabled = _zoom is not 1.0;
    }

    private void OnLayoutSettledForFit(object? sender, EventArgs e)
    {
        ImageScroller.LayoutUpdated -= OnLayoutSettledForFit;
        ApplyZoom();
    }

    private double FitScale(System.Windows.Media.ImageSource source)
    {
        var available = new Size(
            Math.Max(1, ImageScroller.ViewportWidth),
            Math.Max(1, ImageScroller.ViewportHeight));

        if (source.Width <= 0 || source.Height <= 0)
        {
            return 1;
        }

        return Math.Min(1, Math.Min(available.Width / source.Width, available.Height / source.Height));
    }

    private void SetZoom(double? zoom)
    {
        _zoom = zoom is { } value ? Math.Clamp(value, MinZoom, MaxZoom) : null;
        ApplyZoom();
    }

    private void OnZoomFit(object sender, RoutedEventArgs e) => SetZoom(null);

    private void OnZoomActual(object sender, RoutedEventArgs e) => SetZoom(1);

    private void OnZoomIn(object sender, RoutedEventArgs e) => ZoomBy(1.25);

    private void OnZoomOut(object sender, RoutedEventArgs e) => ZoomBy(1 / 1.25);

    /// <summary>
    /// Multiplies the zoom, starting from whatever Fit currently resolves to.
    /// <para>
    /// Starting from the fit scale rather than from 1 is what makes the first click on <b>+</b> feel like a step
    /// rather than a jump: a large screenshot fits at 30%, and zooming from 100% would skip four steps of the
    /// range the user can actually see.
    /// </para>
    /// </summary>
    private void ZoomBy(double factor)
    {
        if (PreviewImage.Source is not { } source)
        {
            return;
        }

        SetZoom((_zoom ?? FitScale(source)) * factor);
    }

    /// <summary>
    /// Ctrl+wheel zooms about the pointer; a plain wheel scrolls, which is the ScrollViewer's own job.
    /// <para>
    /// Zooming about the pointer rather than the top left is the difference between a usable zoom and one that
    /// throws the detail you were looking at off the edge. The content point under the cursor is worked out before
    /// the scale changes and then put back underneath it afterwards, which needs the layout to have run - hence
    /// the dispatcher hop.
    /// </para>
    /// </summary>
    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || PreviewImage.Source is not { } source)
        {
            return;
        }

        e.Handled = true;

        var before = _zoom ?? FitScale(source);
        var pointer = e.GetPosition(ImageScroller);

        // Where in the picture the cursor is, in unscaled pixels.
        var contentX = (ImageScroller.HorizontalOffset + pointer.X) / before;
        var contentY = (ImageScroller.VerticalOffset + pointer.Y) / before;

        SetZoom(before * (e.Delta > 0 ? 1.25 : 1 / 1.25));

        var after = _zoom ?? FitScale(source);

        Dispatcher.BeginInvoke(() =>
        {
            ImageScroller.ScrollToHorizontalOffset((contentX * after) - pointer.X);
            ImageScroller.ScrollToVerticalOffset((contentY * after) - pointer.Y);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Drag to pan, but only when there is something to pan - in Fit mode a drag would do nothing.</summary>
    private void OnImagePanStart(object sender, MouseButtonEventArgs e)
    {
        ImageScroller.Focus();

        if (_zoom is null || e.ClickCount > 1)
        {
            return;
        }

        _panFrom = e.GetPosition(ImageScroller);
        _panOffsetX = ImageScroller.HorizontalOffset;
        _panOffsetY = ImageScroller.VerticalOffset;

        ImageScroller.CaptureMouse();
        ImageScroller.Cursor = Cursors.ScrollAll;
    }

    private void OnImagePanMove(object sender, MouseEventArgs e)
    {
        if (_panFrom is not { } from)
        {
            return;
        }

        var now = e.GetPosition(ImageScroller);

        ImageScroller.ScrollToHorizontalOffset(_panOffsetX - (now.X - from.X));
        ImageScroller.ScrollToVerticalOffset(_panOffsetY - (now.Y - from.Y));
    }

    private void OnImagePanEnd(object sender, MouseButtonEventArgs e)
    {
        _panFrom = null;
        ImageScroller.ReleaseMouseCapture();
        ImageScroller.Cursor = null;
    }

    /// <summary>Double-click toggles Fit and 100%, which is what every picture viewer does.</summary>
    private void OnImageDoubleClick(object sender, MouseButtonEventArgs e) => SetZoom(_zoom is null ? 1 : null);

    /// <summary>
    /// The keyboard equivalents, live only while the scroller has focus - which a click on the picture gives it.
    /// Scoped that way on purpose: bound at window level, <c>0</c> and <c>1</c> would fight the grid and the search
    /// box for keys that mean something else there.
    /// </summary>
    private void OnImageKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.D0 or Key.NumPad0:
                SetZoom(null);
                break;

            case Key.D1 or Key.NumPad1:
                SetZoom(1);
                break;

            case Key.OemPlus or Key.Add:
                ZoomBy(1.25);
                break;

            case Key.OemMinus or Key.Subtract:
                ZoomBy(1 / 1.25);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>The fit scale depends on the pane, so it has to be recomputed when the pane changes size.</summary>
    private void OnImageViewportChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoom is null)
        {
            ApplyZoom();
        }
    }

    /// <summary>
    /// What the footer says about an image's size, and why it is a sentence rather than a number.
    /// <para>
    /// Two different quantities were being shown three inches apart with no labels, and it was reported as a
    /// contradiction - reasonably. For the clip in the report: the header said <b>205 KB</b>, which is
    /// <c>total_bytes</c>, everything the clip stores; the footer said <b>198.1 KB</b>, which is its <c>CF_DIB</c>
    /// plus the 14-byte file header. The other 7,076 bytes were the five further formats it was copied with
    /// (<c>49161</c>, <c>49171</c>, <c>49349</c>, <c>50025</c>, <c>50026</c>), which are what make a paste
    /// reproduce the copy exactly. Both numbers were right; neither said what it was.
    /// </para>
    /// <para>
    /// So the footer now names both and their relationship, and the format count is the part that explains the
    /// gap. A history entry keeps one flattened record, so it gets the plain size it always had.
    /// </para>
    /// </summary>
    private static string DescribeImageBytes(HistoryRow row, PreviewPicture picture) =>
        picture.FormatCount > 1
            ? $"{FormatBytes(picture.PictureBytes)} picture  ·  {row.SizeText} in {picture.FormatCount} formats"
            : FormatBytes(picture.PictureBytes);

    private void ShowImage(BitmapSource? bitmap, string? path, string? bytesCaption, int pixelWidth, int pixelHeight)
    {
        if (bitmap is null)
        {
            PreviewImage.Source = null;
            PreviewImageHost.Visibility = Visibility.Collapsed;
            PreviewFooter.Visibility = Visibility.Collapsed;
            PreviewTextRow.Height = new GridLength(1, GridUnitType.Star);
            PreviewImageRow.Height = new GridLength(0);
            return;
        }

        PreviewImage.Source = bitmap;
        PreviewImageHost.Visibility = Visibility.Visible;

        // Back to Fit for every new picture. Carrying a zoom across selections was tried mentally and rejected:
        // 400% on a screenshot is not a preference, it is something you did to one picture, and inheriting it
        // means the next row opens showing a corner of itself.
        _panFrom = null;
        SetZoom(null);

        // With a path above it the text gets only what it needs; without one it gets nothing.
        PreviewTextRow.Height = path is null ? new GridLength(0) : GridLength.Auto;
        PreviewImageRow.Height = new GridLength(1, GridUnitType.Star);

        // The file's dimensions, passed in rather than read off the bitmap: a thumbnail has been resized, so
        // its own PixelWidth is the size we asked for and not the image's.
        PreviewDimensions.Text = $"{pixelWidth} × {pixelHeight}";
        PreviewBytes.Text = bytesCaption ?? string.Empty;
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

    /// <summary>
    /// Deliberately the single-entry path, unlike Enter and the button. A plain click collapses a multi-selection
    /// to the row under the pointer, so by the time this fires there is exactly one row selected and it is the one
    /// double-clicked - "join these" cannot be what was meant.
    /// </summary>
    private void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => CopySelectionToClipboard();

    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        // Two rows or more is taken as "join these", which is why the button relabels itself - see
        // UpdateCopyButton. One row keeps the single-clip path, which replays every format a clip was copied
        // with; joining can only ever produce text, so overloading the button must not cost that fidelity when
        // it is not what was asked for.
        if (EntriesGrid.SelectedItems.Count > 1)
        {
            CopySelectionJoined();
            return;
        }

        CopySelectionToClipboard();
    }

    /// <summary>
    /// Copies several selected entries as one clip, their text run together with the configured separator.
    /// <para>
    /// Distinct from <c>Enter</c> during the gesture, which pastes clips one after another: that leaves the
    /// target application to decide what happens between them, and in a spreadsheet it means separate cells.
    /// This is one clip, so it lands as one paste.
    /// </para>
    /// <para>
    /// Joined in the order shown rather than the order the rows were clicked. Selection order is not something
    /// a <c>DataGrid</c> reports reliably - a shift-click gives no order at all - and "top to bottom, as I see
    /// it" is the only rule that can be predicted before pressing the button.
    /// </para>
    /// </summary>
    private void CopySelectionJoined()
    {
        // Back to display order: SelectedItems is in the order rows were added to the selection, so a
        // ctrl-click upwards would otherwise join bottom-to-top.
        var selected = EntriesGrid.SelectedItems
            .OfType<HistoryRow>()
            .OrderBy(_rows.IndexOf)
            .ToList();

        if (selected.Count == 0)
        {
            return;
        }

        var separator = ClipJoiner.ParseSeparator(_joinSeparator);

        // Null for anything with no text of its own, which ClipJoiner counts so the status line can account for
        // every row the user selected. An image is the case that matters: its preview text is the literal
        // "[image]", so contributing that would silently paste the word instead of admitting it was left out.
        var result = ClipJoiner.Join(selected.Select(TryReadJoinableText), separator);

        if (result.Joined == 0)
        {
            StatusText.Text = selected.Count == 1
                ? "That entry has no text to join."
                : $"None of those {selected.Count} entries has text to join - images cannot be joined.";

            return;
        }

        var payloads = Win32ClipboardAccess.TextOnlyPayloads(result.Text);
        var snapshot = new ClipboardSnapshot(payloads, result.Text, ClipKind.Text, null);

        // Registered before the write, exactly as the single-entry path does: without it the capture service
        // sees a clipboard change it did not cause and files the joined text as a brand-new clip, on top of the
        // one added deliberately below.
        _selfWrites.NoteWrite(snapshot.ContentHash);

        if (!_clipboard.TryWrite(payloads))
        {
            StatusText.Text = "Could not open the clipboard - another application may be holding it.";
            return;
        }

        // Added to the stack for the same reason a single copy is: the gesture pastes from the stack, not from
        // the system clipboard, so without this Ctrl+V would immediately offer something else. Duplicates
        // allowed because the user asked for this specific combination.
        _store.Add(snapshot, allowDuplicates: true);
        Refresh();

        var skipped = result.Skipped == 0
            ? string.Empty
            : $" {result.Skipped} with no text {(result.Skipped == 1 ? "was" : "were")} left out.";

        StatusText.Text =
            $"Joined {result.Joined} entries with {ClipJoiner.Describe(separator)} and copied as one clip - "
            + $"{result.Text.Length:N0} characters.{skipped}";
    }

    /// <summary>
    /// The text a row contributes to a join, or null when it has none.
    /// <para>
    /// Prefers the archived full text over the preview column, which is capped at
    /// <c>ClipStore.PreviewMaxChars</c> - joining previews would produce a paste that is silently truncated in
    /// the middle, which is worse than one that is obviously short.
    /// </para>
    /// </summary>
    private string? TryReadJoinableText(HistoryRow row)
    {
        // By kind, not by whether any text turns up. A clip with no text still has PREVIEW text, and that preview
        // is a placeholder - the literal "[image]" or "[binary]" - so anything falling back to it would paste
        // those words as though they had been copied. That exact bug shipped once in Copy.
        if (!ClipJoiner.HasJoinableText(row.Kind))
        {
            return null;
        }

        // A clip carries its formats, so the text it was copied with is better than the preview that was
        // rendered from it - and for a clip there is no history blob to read.
        if (row.IsClip)
        {
            if (Win32ClipboardAccess.ExtractText(_store.GetPayloads(row.Id)) is { } text)
            {
                return text;
            }
        }

        return TryReadArchivedText(row) ?? row.Preview;
    }

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

        var body = "Entries that are an exact duplicate of another are removed, keeping one of each. Nothing that "
            + "differs in any way is touched.\n\n"
            + (clips
                // Says outright that time is not part of the test. The History prompt offers an "ignore the time"
                // option and this one does not, which was asked about twice - the answer is that a clip's identity
                // is its content and nothing else, so there is no timestamp here to ignore. Leaving that to be
                // inferred from "judged by its content" was not enough.
                ? "A clip is judged by its content alone - the same test the gesture uses to recognise a re-copy - "
                    + "so the time it was copied plays no part and there is nothing to ignore. The newest of each "
                    + "set is kept, and a pinned one always wins."
                : "An entry is judged by its timestamp, its kind, its text and its image, so two screenshots "
                    + "taken in the same second are not mistaken for one. The oldest of each set is kept.")
            + "\n\nThis cannot be undone.";

        var headline = clips ? "Remove duplicate clips?" : "Remove duplicate history entries?";

        bool accepted;
        var ignoreTimestamp = false;

        if (clips)
        {
            // No option offered: a clip is judged by its content already, so ignoring the time would change
            // nothing. Offering a check box that cannot do anything is worse than not offering it.
            accepted = MessageDialog.Show(
                body,
                headline: headline,
                kind: DialogKind.Warning,
                buttons: DialogButtons.OkCancel,
                owner: this) == DialogResultKind.Accepted;
        }
        else
        {
            // The option belongs in the prompt rather than on the toolbar: this is the moment the decision is
            // made, and it changes what the sentence above means. The help line spells out the consequence at
            // length because this is the destructive choice - it collapses a phrase copied every day for a year
            // into one entry, and the prompt is the last chance to say so.
            (var result, ignoreTimestamp) = MessageDialog.ShowWithOption(
                body,
                optionText: "Ignore the _time it was copied",
                optionHelp: "Judges an entry by its kind, its text and its image only, so the same thing copied "
                    + "on different days counts as one and the most recent is kept. This removes far more than "
                    + "the sweep described above.",
                headline: headline,
                kind: DialogKind.Warning,
                buttons: DialogButtons.OkCancel,
                owner: this);

            accepted = result == DialogResultKind.Accepted;
        }

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
