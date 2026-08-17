using PasteJump.Core;
using PasteJump.Core.Capture;
using PasteJump.Core.Imaging;
using PasteJump.Core.Model;
using PasteJump.Core.Settings;
using PasteJump.Core.Storage;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The image path, end to end through capture, storage and rehydration.
/// <para>
/// This existed as a gap: every other image test used a payload of a few bytes, which stays inline in
/// the database row. A real screenshot is several megabytes, so it crosses
/// <see cref="BlobStore.InlineThresholdBytes"/> and takes a completely different route - out to a
/// content-addressed file and back. Nothing covered that, and "paste produces nothing" is exactly
/// what a blob that fails to rehydrate would look like.
/// </para>
/// </summary>
public sealed class ImagePathTests : IDisposable
{
    private readonly string _root;
    private readonly ClipStore _store;
    private readonly FakeClipboardAccess _clipboard = new();
    private readonly FakeForegroundWindowInfo _foreground = new("explorer.exe");
    private readonly ManualScheduler _scheduler = new();
    private readonly SelfWriteGuard _selfWrites = new();
    private PasteJumpSettings _settings = new();

    public ImagePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pastejump-image-tests", Guid.NewGuid().ToString("n"));
        _store = new ClipStore(AppPaths.At(_root));
    }

    public void Dispose()
    {
        _store.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private CaptureService Build() => new(
        _clipboard,
        _store,
        _selfWrites,
        _foreground,
        () => _settings,
        clock: null,
        schedule: _scheduler.Schedule);

    /// <summary>
    /// Advances the sequence number and lets the settle window elapse, as a real clipboard change does.
    /// </summary>
    /// <remarks>
    /// The read is scheduled rather than immediate since coalescing arrived - one copy raises more than one
    /// notification, so PasteJump waits for the clipboard to stop changing before reading it. Draining the
    /// scheduler here rather than setting <c>ClipboardSettleMs</c> to zero in these tests is deliberate: it keeps
    /// every test in this file exercising the path the application actually takes.
    /// </remarks>
    private void SignalChange(CaptureService capture)
    {
        _clipboard.SequenceNumber++;
        capture.OnClipboardChanged();

        // The scheduled read. Nothing else is queued at this point, so this cannot swallow a retry.
        _scheduler.RunPending();
    }

    /// <summary>
    /// A 32bpp bottom-up BI_RGB DIB, which is exactly the CF_DIB layout: a 40-byte
    /// BITMAPINFOHEADER followed by BGRA rows.
    /// </summary>
    private static byte[] MakeDib(int width, int height)
    {
        const int headerSize = 40;
        var stride = width * 4;
        var buffer = new byte[headerSize + (stride * height)];
        var header = buffer.AsSpan(0, headerSize);

        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[0..], headerSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[16..], 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * height);

        for (var i = headerSize; i < buffer.Length; i += 4)
        {
            buffer[i + 0] = (byte)(i % 251);
            buffer[i + 1] = (byte)(i % 241);
            buffer[i + 2] = (byte)(i % 239);
            buffer[i + 3] = 255;
        }

        return buffer;
    }

    private static ClipboardSnapshot ImageSnapshot(byte[] dib)
        => new([new ClipPayload(8, null, dib)], null, ClipKind.Image, "explorer.exe");

    [Fact]
    public void A_screenshot_sized_image_survives_capture_and_rehydration()
    {
        // 640x480x4 == 1.2 MB, comfortably over the 256 KB inline threshold, so this goes out to a
        // blob file. The bytes must come back identical or a paste writes something corrupt.
        var dib = MakeDib(640, 480);
        Assert.True(dib.Length > BlobStore.InlineThresholdBytes);

        _clipboard.EnqueueRead(ImageSnapshot(dib));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        Assert.Equal(1, _store.Count);

        var clip = _store.GetOrdered(1).Single();
        Assert.Equal(ClipKind.Image, clip.Kind);

        var payloads = _store.GetPayloads(clip.Id);
        var restored = payloads.Single(p => p.FormatId == 8);

        Assert.Equal(dib.Length, restored.Data.Length);
        Assert.True(dib.AsSpan().SequenceEqual(restored.Data), "rehydrated DIB differs from the captured one");
    }

    [Fact]
    public void A_small_image_stays_inline_and_still_rehydrates()
    {
        var dib = MakeDib(8, 8);
        Assert.True(dib.Length <= BlobStore.InlineThresholdBytes);

        _clipboard.EnqueueRead(ImageSnapshot(dib));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        var clip = _store.GetOrdered(1).Single();
        var restored = _store.GetPayloads(clip.Id).Single(p => p.FormatId == 8);

        Assert.True(dib.AsSpan().SequenceEqual(restored.Data));
    }

    [Fact]
    public void A_large_image_still_rehydrates_after_garbage_collection()
    {
        // CollectGarbage runs on every shutdown and deletes any blob no row references. A live clip's
        // blob being swept would turn every image in the stack into a paste-nothing on next launch.
        var dib = MakeDib(640, 480);
        _clipboard.EnqueueRead(ImageSnapshot(dib));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        var clip = _store.GetOrdered(1).Single();

        _store.CollectGarbage();

        var restored = _store.GetPayloads(clip.Id).SingleOrDefault(p => p.FormatId == 8);

        Assert.NotNull(restored);
        Assert.True(dib.AsSpan().SequenceEqual(restored.Data), "blob was collected while still referenced");
    }

    [Fact]
    public void An_image_produces_a_renderable_preview()
    {
        // What the overlay and the History window show. A null here is an image clip that previews as
        // blank even when the bytes are intact.
        var dib = MakeDib(64, 48);
        var bmp = DibConverter.TryCreateBitmapFile(dib);

        Assert.NotNull(bmp);

        // BM signature plus a 14-byte BITMAPFILEHEADER prepended to the DIB.
        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(dib.Length + 14, bmp.Length);
    }

    [Fact]
    public void A_history_image_blob_converts_back_into_a_clipboard_DIB()
    {
        // The History window's Copy button depends on this. Before it existed, copying an image row
        // fell through to the text path and put the literal string "[image]" on the clipboard - the
        // preview text history stores for a picture.
        var original = MakeDib(64, 48);

        var stored = DibConverter.TryCreateBitmapFile(original);
        Assert.NotNull(stored);

        var recovered = DibConverter.TryExtractDib(stored);
        Assert.NotNull(recovered);

        Assert.True(original.AsSpan().SequenceEqual(recovered), "DIB did not survive the BMP round-trip");
    }

    [Theory]
    [InlineData(0)]     // empty
    [InlineData(8)]     // too short to hold a header
    [InlineData(60)]    // long enough, but no BM signature and no valid info header
    public void Nonsense_input_does_not_yield_a_DIB(int length)
    {
        // Blobs can be truncated by a crash mid-write, and a bad DIB handed to the clipboard would be
        // worse than no image at all.
        Assert.Null(DibConverter.TryExtractDib(new byte[length]));
    }

    /// <summary>32bpp DIB with colour but a completely zeroed alpha channel.</summary>
    private static byte[] MakeDibZeroAlpha(int width, int height)
    {
        var dib = MakeDib(width, height);

        for (var i = 40 + 3; i < dib.Length; i += 4)
        {
            dib[i] = 0;
        }

        return dib;
    }

    [Fact]
    public void A_fully_transparent_image_is_made_opaque()
    {
        // The observed failure mode: an image whose alpha is entirely zero is invisible in any consumer
        // that honours alpha, so the paste appears to do nothing at all.
        var dib = MakeDibZeroAlpha(16, 16);

        var repaired = DibConverter.TryMakeOpaqueIfFullyTransparent(dib);

        Assert.NotNull(repaired);

        for (var i = 40 + 3; i < repaired.Length; i += 4)
        {
            Assert.Equal(0xFF, repaired[i]);
        }

        // Colour channels untouched.
        Assert.Equal(dib[40], repaired[40]);
        Assert.Equal(dib[41], repaired[41]);
        Assert.Equal(dib[42], repaired[42]);
    }

    [Fact]
    public void Real_transparency_is_left_alone()
    {
        // The guard that keeps this fix from becoming a bug of its own: an image with a MIX of alpha
        // values is genuinely transparent, and flattening it would destroy what the user copied.
        var dib = MakeDibZeroAlpha(16, 16);
        dib[40 + 3] = 128;

        Assert.Null(DibConverter.TryMakeOpaqueIfFullyTransparent(dib));
    }

    [Fact]
    public void An_already_opaque_image_is_not_rewritten()
    {
        // Null means "nothing to do", which lets the caller avoid copying the array.
        Assert.Null(DibConverter.TryMakeOpaqueIfFullyTransparent(MakeDib(16, 16)));
    }

    [Fact]
    public void Alpha_repair_ignores_formats_it_cannot_reason_about()
    {
        // 24bpp has no alpha channel at all, so there is nothing to normalise and the fourth byte of
        // each group is real colour data that must not be overwritten.
        var dib = MakeDib(16, 16);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14), 24);

        Assert.Null(DibConverter.TryMakeOpaqueIfFullyTransparent(dib));
    }

    [Fact]
    public void Turning_off_image_storage_drops_the_capture_entirely()
    {
        _settings = new PasteJumpSettings { StoreImages = false };
        _clipboard.EnqueueRead(ImageSnapshot(MakeDib(64, 48)));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);

        Assert.Equal(0, _store.Count);
        Assert.Equal(0, _store.HistoryCount);
    }

    [Fact]
    public void Two_copies_of_the_same_image_share_one_blob()
    {
        // Content addressing should dedupe identical payloads. Worth asserting because a bug here
        // would quietly multiply disk usage by the number of times a screenshot is copied.
        var dib = MakeDib(640, 480);

        _clipboard
            .EnqueueRead(ImageSnapshot(dib))
            .EnqueueRead(ImageSnapshot(MakeDib(320, 240)))
            .EnqueueRead(ImageSnapshot(dib));

        var capture = Build();
        capture.Prime();
        SignalChange(capture);
        SignalChange(capture);
        SignalChange(capture);

        // Three clipboard changes, two distinct images: the third is byte-identical to the first, so
        // the store promotes the existing clip rather than inserting one.
        Assert.Equal(2, _store.Count);

        var blobDirectory = Path.Combine(_root, "data", "blobs");

        var blobFiles = Directory.Exists(blobDirectory)
            ? Directory.GetFiles(blobDirectory, "*", SearchOption.AllDirectories)
            : [];

        // Asserted by content address rather than by total file count, because history writes its own
        // preview blob per image as well - so the total is legitimately four here, and pinning that
        // number would be asserting an implementation detail rather than deduplication.
        //
        // Matched on the hash, not on the file's length. Blobs are deflated on disk, so a file's size no
        // longer equals the payload's; the hash is over the uncompressed bytes precisely so that content
        // addressing keeps meaning what it says.
        var expectedName = BlobStore.ComputeHash(dib);

        var copiesOfTheRepeatedImage = blobFiles.Count(f =>
            string.Equals(Path.GetFileName(f), expectedName, StringComparison.Ordinal));

        Assert.Equal(1, copiesOfTheRepeatedImage);
    }
}
