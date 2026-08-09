using PasteJump.Import;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Reading Clipjump's <c>.avc</c> clip files, which is what lets an import reach the paste stack rather than
/// only the searchable archive.
/// <para>
/// The layout was confirmed against a real installation before any of this was written: 1004 files, none
/// failing to parse, trailing bytes never more than a pad byte. These tests pin the reader against that shape
/// and against the malformed cases a crash mid-write can leave behind.
/// </para>
/// </summary>
public class ClipjumpClipFileTests
{
    /// <summary>Builds a container: <c>{ uint32 format, uint64 size, bytes }</c>… then a zero terminator.</summary>
    private static byte[] Avc(params (uint Format, byte[] Data)[] records)
    {
        using var stream = new MemoryStream();

        foreach (var (format, data) in records)
        {
            stream.Write(BitConverter.GetBytes(format));
            stream.Write(BitConverter.GetBytes((ulong)data.Length));
            stream.Write(data);
        }

        stream.Write(BitConverter.GetBytes(0u));
        return stream.ToArray();
    }

    private static byte[] Text(string value) => System.Text.Encoding.Unicode.GetBytes(value + '\0');

    [Fact]
    public void ReadsStandardFormats()
    {
        var payloads = ClipjumpClipFile.TryReadPayloads(Avc(
            (13u, Text("hello")),
            (16u, [9, 4, 0, 0])));

        Assert.Equal(2, payloads.Count);
        Assert.Equal(13u, payloads[0].FormatId);
        Assert.Equal(Text("hello"), payloads[0].Data);

        // No name: standard ids are durable, so there is nothing to re-register on write.
        Assert.Null(payloads[0].FormatName);
    }

    /// <summary>
    /// Registered ids are dropped. They are unique only within the Windows session that allocated them, and the
    /// file records the number rather than the name - so replaying 49406 today would attach an HTML fragment to
    /// whatever happens to hold that id now. A corrupt paste is worse than a missing format.
    /// </summary>
    [Fact]
    public void DropsSessionScopedRegisteredFormats()
    {
        var payloads = ClipjumpClipFile.TryReadPayloads(Avc(
            (49406u, [1, 2, 3]),        // "HTML Format", in some past session
            (50256u, [4, 5]),
            (13u, Text("kept"))));

        Assert.Single(payloads);
        Assert.Equal(13u, payloads[0].FormatId);
    }

    /// <summary>
    /// Handle-based formats are dropped too, and that is a correctness matter rather than tidiness: their bytes
    /// are a GDI handle belonging to a process that exited long ago.
    /// </summary>
    [Theory]
    [InlineData(2u)]    // CF_BITMAP
    [InlineData(3u)]    // CF_METAFILEPICT
    [InlineData(9u)]    // CF_PALETTE
    [InlineData(14u)]   // CF_ENHMETAFILE
    public void DropsHandleBasedFormats(uint format)
        => Assert.Empty(ClipjumpClipFile.TryReadPayloads(Avc((format, [1, 2, 3, 4]))));

    [Fact]
    public void ReadsAnImageClip()
    {
        var dib = new byte[64];
        var payloads = ClipjumpClipFile.TryReadPayloads(Avc((8u, dib), (16u, [9, 4, 0, 0])));

        Assert.Contains(payloads, p => p.FormatId == 8 && p.Data.Length == 64);
    }

    /// <summary>Real files carry a pad byte or two after the terminator. Ignored, not treated as corruption.</summary>
    [Fact]
    public void IgnoresPaddingAfterTheTerminator()
    {
        var file = Avc((13u, Text("padded"))).Concat(new byte[] { 0, 0 }).ToArray();

        Assert.Single(ClipjumpClipFile.TryReadPayloads(file));
    }

    /// <summary>
    /// A length running past the end of the file means a clip half-written when something crashed. What was
    /// read before that point is still good and is kept; nothing throws, because one bad file must not abandon
    /// an import of a thousand.
    /// </summary>
    [Fact]
    public void KeepsWhatCameBeforeATruncatedRecord()
    {
        var good = Avc((13u, Text("first")));
        var truncated = good[..^4]                                   // drop the terminator
            .Concat(BitConverter.GetBytes(8u))                       // a CF_DIB record...
            .Concat(BitConverter.GetBytes(999_999UL))                // ...claiming far more than remains
            .Concat(new byte[] { 1, 2, 3 })
            .ToArray();

        var payloads = ClipjumpClipFile.TryReadPayloads(truncated);

        Assert.Single(payloads);
        Assert.Equal(13u, payloads[0].FormatId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    public void ShortFileYieldsNothing(int length)
        => Assert.Empty(ClipjumpClipFile.TryReadPayloads(new byte[length]));

    /// <summary>
    /// <c>CF_LOCALE</c> on its own is a codepage and nothing else - the same judgement
    /// <c>BookkeepingFormats</c> makes. Importing it would put an empty row in the stack.
    /// </summary>
    [Fact]
    public void LocaleAloneIsNotAClip()
        => Assert.Empty(ClipjumpClipFile.TryReadPayloads(Avc((16u, [9, 4, 0, 0]))));
}
