using System.IO.Compression;
using System.Security.Cryptography;

namespace PasteJump.Core.Storage;

/// <summary>
/// Content-addressed, compressed storage for payloads too large to keep inline in SQLite.
/// <para>
/// Addressing by content hash gives deduplication for free, which matters more than it sounds:
/// copying the same 4 MB screenshot five times costs one file, and re-copying a large block of
/// text that is already in history costs nothing. It also makes writes idempotent, so a crash
/// midway through a capture cannot leave a half-written blob that a later read would trust.
/// </para>
/// <para>
/// Blobs are deflated on the way in, because the clipboard's image formats are gigantic and
/// enormously compressible. A screenshot arrives as an uncompressed DIB - raw pixels, no encoding - so a
/// PNG that is 146 KB on disk lands here as 15 MB, and Windows publishes the same pixels two or three
/// times over as <c>CF_DIB</c>, <c>CF_DIBV5</c> and often <c>System.Drawing.Bitmap</c>. Measured on real
/// captures, deflate at <see cref="CompressionLevel.Optimal"/> shrinks such a blob about 62x for roughly
/// 34 ms of CPU on 8 MB, and it *reduces* overall capture time by cutting what has to reach the disk.
/// </para>
/// <para>
/// Compressing rather than discarding the duplicate formats is deliberate. Dropping them would save a
/// further third at best, against a real risk: <c>System.Drawing.Bitmap</c> is a registered format that
/// Windows will not synthesise back, so an application that asks only for it would paste nothing. Storing
/// what the source published stays the rule; the redundancy is made cheap instead of being second-guessed.
/// </para>
/// </summary>
public sealed class BlobStore
{
    /// <summary>
    /// Marks a compressed blob. Blobs written before compression existed have no marker and are still read
    /// verbatim, so an existing store keeps working untouched.
    /// <para>
    /// An explicit marker rather than sniffing for a deflate stream: raw deflate has no magic number, and
    /// guessing wrong in either direction corrupts a clip silently.
    /// </para>
    /// </summary>
    private static readonly byte[] CompressedMarker = [0x50, 0x4A, 0x42, 0x31]; // "PJB1"
    /// <summary>
    /// Payloads at or below this size stay inline in the database row. Small blobs as loose files
    /// would mean thousands of tiny files and a syscall per preview; large blobs inline would
    /// bloat every query that touches the row.
    /// </summary>
    public const int InlineThresholdBytes = 256 * 1024;

    private readonly string _root;

    public BlobStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = rootDirectory;
        Directory.CreateDirectory(_root);
    }

    public static string ComputeHash(ReadOnlySpan<byte> data)
        => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// Writes the blob if absent and returns its hash.
    /// <para>
    /// The hash is always over the <em>uncompressed</em> bytes. That keeps content addressing meaning what
    /// it says, keeps deduplication working, and means blobs written before compression existed still
    /// resolve under the same name - so no migration is needed for the store to keep working.
    /// </para>
    /// </summary>
    public string Write(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var hash = ComputeHash(data);
        var path = PathFor(hash);

        if (File.Exists(path))
        {
            return hash;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomically(path, Compress(data));

        return hash;
    }

    public byte[]? TryRead(string hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        var path = PathFor(hash);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Decompress(File.ReadAllBytes(path));
        }
        catch (InvalidDataException)
        {
            // A truncated or corrupt blob. Null means "this clip's data is gone", which the callers already
            // handle - throwing here would take down a paste or a preview render instead.
            return null;
        }
    }

    /// <summary>
    /// Rewrites blobs left uncompressed by an earlier version, and reports how many it converted.
    /// <para>
    /// Bounded by <paramref name="byteBudget"/> so it can never turn startup into a long stall on a large
    /// existing store: it converts what fits in the budget and the next launch picks up the rest, reaching
    /// zero work once everything is converted. Without a bound, a store with a few hundred image clips would
    /// make one startup read and rewrite gigabytes.
    /// </para>
    /// </summary>
    public int CompactLegacyBlobs(long byteBudget = 64L * 1024 * 1024)
    {
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        // Once a full pass has found nothing left to convert, there never will be anything again: every write
        // since compression was introduced goes out compressed, so an uncompressed blob can only be a leftover
        // from before. Without this the pass still opened every blob in the store at each start-up to discover
        // that - measured at 75 ms of the 204 ms spent in Compose, making it the single largest item there, for
        // no benefit whatsoever.
        var sentinel = Path.Combine(_root, ConvertedMarkerFileName);

        if (File.Exists(sentinel))
        {
            return 0;
        }

        var converted = 0;
        long spent = 0;
        var withinBudget = true;

        // Set when a blob is skipped because the filesystem would not co-operate. It gates the sentinel for the
        // same reason the budget does: the marker asserts that every blob IS compressed, and one skipped blob
        // makes that false. Without this a single transient lock - a virus scanner holding a file for a moment
        // is enough - stranded that blob uncompressed for ever, because the sentinel then short-circuited every
        // future pass. Harmless to read, since an unmarked blob is returned verbatim, but it is disk space that
        // never comes back and a marker that lies.
        var skippedOnError = false;

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            if (spent >= byteBudget)
            {
                // Stopped early, so the store has not been proven converted and no sentinel is written.
                withinBudget = false;
                break;
            }

            var name = Path.GetFileName(file);

            if (name.Contains(".tmp-", StringComparison.Ordinal)
                || name.Equals(ConvertedMarkerFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                // The marker is tested by reading four bytes, not the whole file. Reading each blob in full
                // just to look at its header meant a complete pass over the blob store on every single
                // start-up - 51 MB across 485 files on the store that exposed this - and it never stopped,
                // because an already-converted blob was read and then discarded exactly like an unconverted
                // one.
                //
                // Measured, so the trade is on record: with the file cache warm the peek is actually SLOWER
                // (150 ms against 123 ms for that store), because 485 opens cost more than one streaming read
                // of cached data. It wins on a cold cache and it stops the cost scaling with the size of the
                // store rather than its file count, which is what matters as a history grows. Do not "optimise"
                // this back to a full read on the strength of a warm-cache benchmark.
                if (IsAlreadyCompressed(file))
                {
                    continue;
                }

                var raw = File.ReadAllBytes(file);
                spent += raw.Length;

                // Verified against the filename before rewriting. If a legacy blob does not hash to its own
                // name it is already damaged, and rewriting it would only launder the damage into a form
                // that looks freshly written.
                if (!string.Equals(ComputeHash(raw), name, StringComparison.Ordinal))
                {
                    continue;
                }

                WriteAtomically(file, Compress(raw), overwrite: true);
                converted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or vanished mid-pass. Nothing is lost - the original is still there - but the next
                // launch only tries again if no sentinel is written, hence the flag.
                skippedOnError = true;
            }
        }

        if (withinBudget && !skippedOnError)
        {
            try
            {
                File.WriteAllBytes(sentinel, []);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Failing to write it only costs one wasted pass per launch, so it is not worth reporting.
            }
        }

        return converted;
    }

    /// <summary>
    /// Written into the blob root once every blob is known to be compressed, so later start-ups can skip the
    /// pass without opening anything. Must be excluded from garbage collection, which otherwise deletes it as
    /// a file whose name is not a live hash - and it would then be rewritten on the next start, forever.
    /// </summary>
    private const string ConvertedMarkerFileName = ".compressed";

    private static bool HasMarker(ReadOnlySpan<byte> stored)
        => stored.Length >= CompressedMarker.Length
            && stored[..CompressedMarker.Length].SequenceEqual(CompressedMarker);

    /// <summary>
    /// Whether a blob on disk already carries the compression marker, read without loading the file.
    /// <para>
    /// A short read rather than <see cref="File.ReadAllBytes"/>, because this runs once per blob on every
    /// start-up and the overwhelmingly common answer is yes.
    /// </para>
    /// </summary>
    private static bool IsAlreadyCompressed(string path)
    {
        Span<byte> head = stackalloc byte[4];

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 0,
            FileOptions.SequentialScan);

        return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length
            && HasMarker(head);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream(CompressedMarker.Length + (data.Length / 4) + 64);
        output.Write(CompressedMarker);

        // Optimal, not Fastest. Measured on real captures the two cost 34 ms and 26 ms on an 8 MB blob while
        // Optimal compresses nearly twice as hard (62x against 34x), so the 8 ms is bought back many times
        // over in bytes not written to disk.
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] stored)
    {
        if (!HasMarker(stored))
        {
            // Written before compression existed. Returned verbatim.
            return stored;
        }

        using var input = new MemoryStream(stored, CompressedMarker.Length, stored.Length - CompressedMarker.Length);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Writes via a temp name and moves, so a reader never observes a partial file.</summary>
    private static void WriteAtomically(string path, byte[] content, bool overwrite = false)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("n")[..8];
        File.WriteAllBytes(temp, content);

        try
        {
            File.Move(temp, path, overwrite);
        }
        catch (IOException) when (!overwrite && File.Exists(path))
        {
            // Another writer won the race with identical content. Nothing to do.
            TryDelete(temp);
        }
    }

    public bool Exists(string hash)
        => !string.IsNullOrEmpty(hash) && File.Exists(PathFor(hash));

    public void Delete(string hash)
    {
        if (!string.IsNullOrEmpty(hash))
        {
            TryDelete(PathFor(hash));
        }
    }

    /// <summary>
    /// Removes blobs no longer referenced by any row. Callers pass the full live set; anything
    /// on disk outside it is unreachable and safe to drop.
    /// </summary>
    public int CollectGarbage(IReadOnlySet<string> liveHashes)
    {
        ArgumentNullException.ThrowIfNull(liveHashes);

        if (!Directory.Exists(_root))
        {
            return 0;
        }

        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);

            if (name.Contains(".tmp-", StringComparison.Ordinal))
            {
                TryDelete(file);
                continue;
            }

            // Not a blob, so not garbage. Deleting it would silently reinstate a full compaction pass at every
            // start-up, since the next launch would find no sentinel and rewrite it.
            if (name.Equals(ConvertedMarkerFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!liveHashes.Contains(name))
            {
                TryDelete(file);
                removed++;
            }
        }

        return removed;
    }

    // Two-character fan-out keeps any single directory to a manageable entry count.
    private string PathFor(string hash) => Path.Combine(_root, hash[..2], hash);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A locked or already-removed file is not worth failing a capture over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
