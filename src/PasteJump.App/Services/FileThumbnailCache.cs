using System.Windows.Media.Imaging;
using PasteJump.Core.Model;

namespace PasteJump.App.Services;

/// <summary>
/// Thumbnails for copied image files, for the paste overlay.
/// <para>
/// Cached because the overlay is redrawn on <em>every tap of the trigger key</em> during the gesture. Decoding
/// the same photograph again on each tap would put disk I/O in the middle of the one interaction the whole
/// application exists to make fast. A miss costs one scaled decode; every step back through the stack after
/// that is free.
/// </para>
/// <para>
/// Deliberately synchronous, which is a judgement rather than an oversight: the overlay already decodes a
/// multi-megabyte DIB inline for image clips, so a scaled JPEG decode is not a new class of cost. If it ever
/// does show, the fix is to load off-thread and re-render, not to remove the cache.
/// </para>
/// </summary>
internal static class FileThumbnailCache
{
    /// <summary>
    /// Widest thumbnail worth decoding, tracking the overlay's configured preview width. JPEG decoders scale
    /// during decode rather than after, so asking for less genuinely costs less - and asking for less than the
    /// overlay will draw would stretch the result, which is the one outcome worth avoiding here.
    /// </summary>
    private static int _maxWidth = 600;

    /// <summary>
    /// Follows the preview-size setting. Cached thumbnails are dropped, because they were decoded for the old
    /// width and reusing them is precisely the stretching this exists to avoid.
    /// </summary>
    internal static void SetMaxWidth(int maxWidth)
    {
        if (maxWidth == _maxWidth)
        {
            return;
        }

        _maxWidth = maxWidth;
        Entries.Clear();
        Order.Clear();
    }

    /// <summary>
    /// How many entries to keep. Small on purpose: the gesture walks a handful of recent clips, and holding
    /// decoded bitmaps for a long history would be megabytes for pictures nobody is looking at.
    /// </summary>
    private const int Capacity = 8;

    private static readonly Dictionary<string, Thumbnail> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Order = [];

    /// <summary>A decoded thumbnail, with the dimensions and byte count of the file rather than of itself.</summary>
    internal sealed record Thumbnail(BitmapSource Bitmap, int PixelWidth, int PixelHeight, long FileBytes);

    /// <summary>
    /// The thumbnail for the first image file named in a <see cref="FileListPreview"/> description, or null
    /// when there is not one. Only the first: the overlay shows one picture, and a copy of forty photographs
    /// must not read forty files to draw it.
    /// </summary>
    internal static Thumbnail? TryGet(string? description)
    {
        foreach (var path in FileListPreview.TryReadPathsFromDescription(description))
        {
            if (!ImageExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            // Never a network path. This is the gesture's redraw path, and a stat or read against an offline
            // server stalls for seconds - the same reason the folder probe skips UNC.
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                continue;
            }

            var thumbnail = Load(path);

            if (thumbnail is not null)
            {
                return thumbnail;
            }
        }

        return null;
    }

    private static Thumbnail? Load(string path)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
            {
                return null;
            }

            // Keyed on the write time as well, so editing a file in place does not leave a stale picture.
            var key = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";

            if (Entries.TryGetValue(key, out var cached))
            {
                return cached;
            }

            using var stream = info.OpenRead();

            // Real dimensions from the header, before decoding: DecodePixelWidth resizes, so the decoded
            // bitmap can only report the size we asked for.
            var header = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = header.Frames[0];
            var pixelWidth = frame.PixelWidth;
            var pixelHeight = frame.PixelHeight;

            stream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;

            // Downwards only. Enlarging a small image would be pointless and would misreport its size.
            if (pixelWidth > _maxWidth)
            {
                bitmap.DecodePixelWidth = _maxWidth;
            }

            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            var thumbnail = new Thumbnail(bitmap, pixelWidth, pixelHeight, info.Length);

            Entries[key] = thumbnail;
            Order.Add(key);

            if (Order.Count > Capacity)
            {
                Entries.Remove(Order[0]);
                Order.RemoveAt(0);
            }

            return thumbnail;
        }
        catch (Exception)
        {
            // Unreadable, gone, or not an image after all. The overlay still shows the path, so there is
            // nothing to report and nothing to retry.
            return null;
        }
    }

    /// <summary>
    /// Extensions worth opening. An allow-list, so a copied folder of executables is not opened and failed on
    /// once per keystroke during the gesture.
    /// </summary>
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp",
        };
}
