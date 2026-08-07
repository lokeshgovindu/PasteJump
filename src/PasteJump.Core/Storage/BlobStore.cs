using System.Security.Cryptography;

namespace PasteJump.Core.Storage;

/// <summary>
/// Content-addressed storage for payloads too large to keep inline in SQLite.
/// <para>
/// Addressing by content hash gives deduplication for free, which matters more than it sounds:
/// copying the same 4 MB screenshot five times costs one file, and re-copying a large block of
/// text that is already in history costs nothing. It also makes writes idempotent, so a crash
/// midway through a capture cannot leave a half-written blob that a later read would trust.
/// </para>
/// </summary>
public sealed class BlobStore
{
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

    /// <summary>Writes the blob if absent and returns its hash.</summary>
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

        // Write to a temp name then move, so a reader never observes a partial file.
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("n")[..8];
        File.WriteAllBytes(temp, data);

        try
        {
            File.Move(temp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another writer won the race with identical content. Nothing to do.
            TryDelete(temp);
        }

        return hash;
    }

    public byte[]? TryRead(string hash)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return null;
        }

        var path = PathFor(hash);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
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
