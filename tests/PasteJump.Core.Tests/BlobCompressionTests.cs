using PasteJump.Core.Storage;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Blob compression, and the transparent handling of blobs written before it existed.
/// <para>
/// This exists because of a real observation: the history window reported 15.2 MB for an image whose file on
/// disk was 146 KB. The number was truthful - the clipboard hands out images as uncompressed DIBs, raw pixels
/// with no encoding, and Windows publishes the same pixels two or three times over as <c>CF_DIB</c>,
/// <c>CF_DIBV5</c> and often <c>System.Drawing.Bitmap</c>. What was wrong was storing all of it verbatim.
/// </para>
/// <para>
/// The round-trip tests are the ones that matter. A compression bug here does not announce itself: it
/// corrupts a clip that will be pasted days later into something the user cares about.
/// </para>
/// </summary>
public sealed class BlobCompressionTests : IDisposable
{
    private readonly string _root;
    private readonly BlobStore _blobs;

    public BlobCompressionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pastejump-blob-tests", Guid.NewGuid().ToString("n"));
        _blobs = new BlobStore(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Stands in for a captured screenshot: a plausible DIB header followed by broad areas of flat colour,
    /// which is what makes real screenshots compress as hard as they do.
    /// </summary>
    private static byte[] FakeDib(int pixels = 400_000)
    {
        var data = new byte[40 + (pixels * 4)];

        // BITMAPINFOHEADER size, as a real CF_DIB begins.
        data[0] = 40;

        for (var i = 40; i < data.Length; i += 4)
        {
            var band = (byte)((i / 4096) % 7 * 32);
            data[i] = band;
            data[i + 1] = band;
            data[i + 2] = (byte)(band / 2);
            data[i + 3] = 0xFF;
        }

        return data;
    }

    private string PathOf(string hash) => Path.Combine(_root, hash[..2], hash);

    // ------------------------------------------------------------------ round trip

    [Fact]
    public void A_blob_round_trips_byte_for_byte()
    {
        var data = FakeDib();

        var hash = _blobs.Write(data);

        Assert.Equal(data, _blobs.TryRead(hash));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(65_536)]
    public void Awkward_sizes_round_trip_too(int size)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);

        var hash = _blobs.Write(data);

        Assert.Equal(data, _blobs.TryRead(hash));
    }

    [Fact]
    public void Incompressible_data_still_round_trips()
    {
        // Random bytes cannot be compressed and deflate will emit slightly more than it was given. The
        // round trip is what must hold; the size is not promised.
        var data = new byte[200_000];
        Random.Shared.NextBytes(data);

        var hash = _blobs.Write(data);

        Assert.Equal(data, _blobs.TryRead(hash));
    }

    [Fact]
    public void A_screenshot_shaped_blob_gets_dramatically_smaller_on_disk()
    {
        var data = FakeDib();

        var hash = _blobs.Write(data);
        var onDisk = new FileInfo(PathOf(hash)).Length;

        // The real measurement on captured screenshots was about 62x. Asserting only 4x keeps this a
        // regression test for "compression is actually happening" rather than a brittle assertion about
        // deflate's exact output.
        Assert.True(
            onDisk * 4 < data.Length,
            $"expected strong compression, got {data.Length} bytes down to {onDisk}");
    }

    // ------------------------------------------------------------------ content addressing

    [Fact]
    public void The_hash_is_over_the_uncompressed_bytes()
    {
        // Which is what keeps content addressing meaning what it says, keeps deduplication working, and lets
        // rows written before compression still resolve to their blob.
        var data = FakeDib(1000);

        Assert.Equal(BlobStore.ComputeHash(data), _blobs.Write(data));
    }

    [Fact]
    public void Writing_the_same_content_twice_stores_one_file()
    {
        var data = FakeDib(1000);

        var first = _blobs.Write(data);
        var second = _blobs.Write(data);

        Assert.Equal(first, second);
        Assert.Single(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void No_temp_files_are_left_behind()
    {
        _blobs.Write(FakeDib(1000));

        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories),
            f => f.Contains(".tmp-", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ blobs from before compression

    /// <summary>Writes a blob the way the pre-compression version did: raw bytes under its own hash.</summary>
    private string WriteLegacy(byte[] data)
    {
        var hash = BlobStore.ComputeHash(data);
        var path = PathOf(hash);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);

        return hash;
    }

    [Fact]
    public void An_uncompressed_blob_is_still_read_correctly()
    {
        // The store must keep working with no migration, or upgrading would appear to lose every image.
        var data = FakeDib(1000);
        var hash = WriteLegacy(data);

        Assert.Equal(data, _blobs.TryRead(hash));
    }

    [Fact]
    public void Compacting_converts_uncompressed_blobs_and_preserves_their_content()
    {
        var data = FakeDib();
        var hash = WriteLegacy(data);
        var before = new FileInfo(PathOf(hash)).Length;

        Assert.Equal(1, _blobs.CompactLegacyBlobs());

        Assert.True(new FileInfo(PathOf(hash)).Length < before);
        Assert.Equal(data, _blobs.TryRead(hash));
    }

    [Fact]
    public void Compacting_is_idempotent()
    {
        WriteLegacy(FakeDib(1000));

        Assert.Equal(1, _blobs.CompactLegacyBlobs());

        // Second pass finds nothing left to do, which is what makes it safe to run at every startup.
        Assert.Equal(0, _blobs.CompactLegacyBlobs());
    }

    [Fact]
    public void A_completed_pass_leaves_a_marker_so_later_starts_do_no_work()
    {
        // Measured at 75 ms of the 204 ms spent in Compose - the largest single item there - opening every blob
        // in the store to discover there was nothing to do. Once a pass completes there never will be, because
        // every write since compression was introduced goes out compressed.
        WriteLegacy(FakeDib(1000));
        _blobs.Write(FakeDib(2000));

        Assert.Equal(1, _blobs.CompactLegacyBlobs());
        Assert.True(File.Exists(Path.Combine(_root, ".compressed")));

        // Proven by removing the blobs entirely, leaving only the marker: a pass that still enumerated the
        // store would find nothing and report 0 either way, but one that respects the marker cannot even look.
        // Deleting the fan-out directories is the closest a test gets to "must not enumerate".
        foreach (var fanOut in Directory.GetDirectories(_root))
        {
            Directory.Delete(fanOut, recursive: true);
        }

        Assert.Equal(0, _blobs.CompactLegacyBlobs());
        Assert.True(File.Exists(Path.Combine(_root, ".compressed")));
    }

    /// <summary>
    /// A pass that stopped on its budget must not write the marker, or whatever it did not reach would never be
    /// converted at all.
    /// <para>
    /// Asserted as an end state rather than as exact per-pass counts, and that is a fix rather than a
    /// weakening. This test failed once in roughly forty runs, only ever inside a full-suite run and never
    /// in twenty-five isolated ones. The mechanism is in <c>CompactLegacyBlobs</c>: a transient
    /// <c>IOException</c> - a virus scanner holding a temp file for a moment is enough - is deliberately
    /// swallowed and the blob skipped, but the budget has already been charged for reading it. The pass then
    /// returns one fewer than expected and stops, so <c>Assert.Equal(1, ...)</c> saw 0. Tolerating that IO
    /// error is correct for production, where the next launch simply tries again; it is only the exact count
    /// that was never a safe thing to assert.
    /// </para>
    /// </summary>
    [Fact]
    public void Stopping_on_the_budget_does_not_claim_the_store_is_converted()
    {
        for (var i = 0; i < 3; i++)
        {
            WriteLegacy(FakeDib(50_000 + i));
        }

        var marker = Path.Combine(_root, ".compressed");

        var stopped = _blobs.CompactLegacyBlobs(byteBudget: 1);

        // Stopped early, so it cannot have finished the store - that is the whole claim.
        Assert.InRange(stopped, 0, 2);
        Assert.False(File.Exists(marker));
        Assert.Contains(LegacyBlobs(), static _ => true);

        // And an unbudgeted pass finishes what the first one left, whatever that was.
        _blobs.CompactLegacyBlobs();

        Assert.True(File.Exists(marker));
        Assert.Empty(LegacyBlobs());
    }

    /// <summary>
    /// A blob the filesystem would not let us rewrite is skipped without the marker being written, so the next
    /// pass picks it up.
    /// <para>
    /// Found while chasing the flake above, and it was a real defect rather than a test problem: the skip is
    /// caught and the loop then ends normally, so the pass looked complete and wrote the sentinel - which
    /// short-circuits every future pass. One transient lock stranded that blob uncompressed for ever. Harmless
    /// to read, since an unmarked blob is returned verbatim, but disk space that never comes back and a marker
    /// that claims something untrue.
    /// </para>
    /// </summary>
    [Fact]
    public void A_locked_blob_is_retried_on_the_next_pass()
    {
        var kept = WriteLegacy(FakeDib(2_000));
        var locked = WriteLegacy(FakeDib(3_000));

        var marker = Path.Combine(_root, ".compressed");

        using (File.Open(PathOf(locked), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(1, _blobs.CompactLegacyBlobs());

            // The pass ran to the end of the store, but it did not convert everything, so it must not say so.
            Assert.False(File.Exists(marker));
        }

        // Released, so the retry succeeds and the store is genuinely converted this time.
        Assert.Equal(1, _blobs.CompactLegacyBlobs());
        Assert.True(File.Exists(marker));
        Assert.Empty(LegacyBlobs());

        // And the blob that was converted first is untouched by the second pass.
        Assert.Equal(FakeDib(2_000), _blobs.TryRead(kept));
    }

    /// <summary>
    /// Blobs still lacking the <c>PJB1</c> marker. The end state this asserts on is what actually matters -
    /// "everything is converted" - and unlike a conversion count it cannot be thrown off by one blob having
    /// been skipped and picked up on the following pass.
    /// </summary>
    private IEnumerable<string> LegacyBlobs()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);

            if (name.Equals(".compressed", StringComparison.OrdinalIgnoreCase)
                || name.Contains(".tmp-", StringComparison.Ordinal))
            {
                continue;
            }

            var head = new byte[4];

            using var stream = File.OpenRead(file);

            if (stream.Read(head) < 4 || !head.AsSpan().SequenceEqual("PJB1"u8))
            {
                yield return file;
            }
        }
    }

    [Fact]
    public void Garbage_collection_keeps_the_marker()
    {
        // It is not a blob, so its name is not a live hash - and deleting it would silently reinstate a full
        // compaction pass at every start-up.
        _blobs.CompactLegacyBlobs();

        var marker = Path.Combine(_root, ".compressed");
        Assert.True(File.Exists(marker));

        _blobs.CollectGarbage(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void Compacting_leaves_already_compressed_blobs_alone()
    {
        var hash = _blobs.Write(FakeDib(1000));
        var before = File.ReadAllBytes(PathOf(hash));

        Assert.Equal(0, _blobs.CompactLegacyBlobs());
        Assert.Equal(before, File.ReadAllBytes(PathOf(hash)));
    }

    [Fact]
    public void Compacting_refuses_a_blob_that_does_not_hash_to_its_own_name()
    {
        // Already damaged. Rewriting it would launder the damage into something that looks freshly written.
        var path = PathOf(new string('a', 64));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);

        Assert.Equal(0, _blobs.CompactLegacyBlobs());
        Assert.Equal([1, 2, 3, 4, 5], File.ReadAllBytes(path));
    }

    [Fact]
    public void Compacting_respects_its_byte_budget()
    {
        // Bounded so a store with hundreds of image clips cannot turn one startup into a long stall.
        for (var i = 0; i < 4; i++)
        {
            WriteLegacy(FakeDib(50_000 + i));
        }

        var converted = _blobs.CompactLegacyBlobs(byteBudget: 1);

        // The budget is checked before each file, so exactly one gets through and the rest wait.
        Assert.Equal(1, converted);
        Assert.Equal(3, _blobs.CompactLegacyBlobs());
    }

    // ------------------------------------------------------------------ damage

    [Fact]
    public void A_corrupt_compressed_blob_reads_as_missing_rather_than_throwing()
    {
        var hash = _blobs.Write(FakeDib(1000));
        var path = PathOf(hash);

        // Keep the marker, ruin the stream. Callers already handle null as "this clip's data is gone";
        // throwing would take down a paste or a preview render instead.
        var stored = File.ReadAllBytes(path);
        Array.Fill(stored, (byte)0xEE, 4, stored.Length - 4);
        File.WriteAllBytes(path, stored);

        Assert.Null(_blobs.TryRead(hash));
    }

    [Fact]
    public void Garbage_collection_still_works_on_compressed_blobs()
    {
        var kept = _blobs.Write(FakeDib(1000));
        var dropped = _blobs.Write(FakeDib(2000));

        Assert.Equal(1, _blobs.CollectGarbage(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { kept }));

        Assert.NotNull(_blobs.TryRead(kept));
        Assert.Null(_blobs.TryRead(dropped));
    }
}
