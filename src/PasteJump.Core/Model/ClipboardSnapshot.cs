using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PasteJump.Core.Model;

/// <summary>
/// Everything that was on the clipboard at one instant: every available format, plus the
/// derived text used for previews and search.
/// <para>
/// This type is the reason most of the original AutoHotkey complexity disappears. Clipjump
/// treated the live system clipboard as its working buffer and re-read it constantly, which
/// forced retry loops, an <c>ONCLIPBOARD</c> flag protocol, and an invisible focus-stealing
/// window to appease Excel. We read everything exactly once, here, and never consult the
/// system clipboard again until the user actually pastes.
/// </para>
/// </summary>
public sealed class ClipboardSnapshot
{
    public ClipboardSnapshot(
        IReadOnlyList<ClipPayload> payloads,
        string? text,
        ClipKind kind,
        string? sourceExecutable)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        Payloads = payloads;
        Text = text;
        Kind = kind;
        SourceExecutable = sourceExecutable;
        TotalBytes = payloads.Sum(static p => (long)p.ByteLength);
        ContentHash = ComputeHash(payloads);
        DedupKey = ComputeDedupKey(text, kind, ContentHash);
    }

    public IReadOnlyList<ClipPayload> Payloads { get; }

    /// <summary>Plain text of the clip, when it has any. Null for images and other binary-only clips.</summary>
    public string? Text { get; }

    public ClipKind Kind { get; }

    /// <summary>File name of the process that owned the foreground window at capture time.</summary>
    public string? SourceExecutable { get; }

    public long TotalBytes { get; }

    /// <summary>
    /// Stable hash over every format's id and bytes.
    /// <para>
    /// This is how we recognise our own writes. When we put a clip on the clipboard in order
    /// to paste it, Windows raises a change notification that would otherwise be captured as a
    /// brand new clip - an infinite regress. The original guards this with a mutable
    /// <c>blockMonitoring()</c> flag plus a 200 ms time-difference heuristic
    /// (Clipjump.ahk:412), both of which have timing windows where a fast real copy is
    /// swallowed or a slow self-write is recorded. Comparing content hashes has no timing
    /// component at all: either the bytes are the ones we just wrote, or they are not.
    /// </para>
    /// </summary>
    public string ContentHash { get; }

    /// <summary>
    /// Identifies a clip by what the user would call "the same thing", which is deliberately looser
    /// than <see cref="ContentHash"/>.
    /// <para>
    /// <see cref="ContentHash"/> covers every format's bytes, which is exactly right for recognising
    /// our own writes but useless for recognising a repeat copy. The rich formats that travel
    /// alongside text are not stable between two copies of the same selection: Word and Excel stamp
    /// <c>Rich Text Format</c> with generator ids and embed an object descriptor, browsers vary the
    /// byte offsets in the <c>HTML Format</c> header, and several apps include a timestamp. So
    /// copying one paragraph twice yields two different content hashes, hash dedup never fires, and
    /// the stack and history fill with what look to the user like identical entries.
    /// </para>
    /// <para>
    /// For text clips the key is therefore the text alone, whitespace-trimmed - some applications
    /// append a trailing newline to a line selection and others do not. Non-text clips keep the full
    /// content hash, since there is no equivalent notion of "the same image" short of comparing bytes.
    /// </para>
    /// </summary>
    public string DedupKey { get; }

    public bool IsEmpty => Payloads.Count == 0;

    private static string ComputeDedupKey(string? text, ClipKind kind, string contentHash)
    {
        if (kind != ClipKind.Text || text is null)
        {
            return "h:" + contentHash;
        }

        // Hashed rather than kept verbatim: a clip can hold megabytes of text, and this key is
        // retained between captures to compare the next one against.
        var trimmed = text.Trim();
        var bytes = SHA256.HashData(Encoding.Unicode.GetBytes(trimmed));

        return "t:" + Convert.ToHexStringLower(bytes);
    }

    private static string ComputeHash(IReadOnlyList<ClipPayload> payloads)
    {
        using var sha = SHA256.Create();
        Span<byte> scratch = stackalloc byte[8];

        // Order-independent: the clipboard does not guarantee enumeration order, and a clip
        // whose formats come back shuffled is still the same clip.
        foreach (var payload in payloads
            .OrderBy(static p => p.FormatId)
            .ThenBy(static p => p.FormatName, StringComparer.Ordinal))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(scratch, payload.FormatId);
            BinaryPrimitives.WriteInt32LittleEndian(scratch[4..], payload.ByteLength);
            sha.TransformBlock(scratch.ToArray(), 0, 8, null, 0);

            if (payload.FormatName is { Length: > 0 } name)
            {
                var nameBytes = Encoding.UTF8.GetBytes(name);
                sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            }

            if (payload.Data.Length > 0)
            {
                sha.TransformBlock(payload.Data, 0, payload.Data.Length, null, 0);
            }
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash ?? []);
    }
}
