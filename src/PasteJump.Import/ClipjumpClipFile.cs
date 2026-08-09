using PasteJump.Core.Model;

namespace PasteJump.Import;

/// <summary>
/// Reads a Clipjump <c>.avc</c> clip file — AutoHotkey's <c>ClipboardAll</c> serialisation.
/// <para>
/// The container is a sequence of <c>{ uint32 format, uint64 size, byte[size] }</c> records terminated by a
/// zero format, sometimes followed by a byte or two of padding. Verified against a real installation: 1004
/// files, none failing to parse, the leftovers being at most a pad byte.
/// </para>
/// <para>
/// <b>Only standard formats survive the trip, and that is not a shortcut.</b> Ids from
/// <c>RegisterClipboardFormat</c> are unique only within a Windows session, and this file records the number
/// rather than the name — so an id of 49406 meant "HTML Format" in whichever session Clipjump wrote it and
/// means whatever happens to be registered 49406th today. Replaying those bytes under a re-used id would
/// attach an HTML fragment to an unrelated format, which is a corrupt paste rather than a missing one. The
/// numbers below are the ones Windows fixes for all time.
/// </para>
/// <para>
/// The practical cost is that imported clips lose rich formatting: text, images and file lists come across
/// faithfully, HTML and RTF and Excel's private formats cannot. On the installation this was built against
/// that still leaves 995 of 1004 clips importable.
/// </para>
/// </summary>
public static class ClipjumpClipFile
{
    /// <summary>
    /// Standard formats whose clipboard payload is a plain memory block, so the stored bytes mean the same
    /// thing when written back.
    /// <para>
    /// Deliberately an allow-list. <c>CF_BITMAP</c>, <c>CF_PALETTE</c>, <c>CF_METAFILEPICT</c> and
    /// <c>CF_ENHMETAFILE</c> are all GDI <em>handles</em> — their bytes are meaningless outside the process
    /// that created them, and writing them back would hand the shell a dangling handle. <c>CF_DIB</c> carries
    /// the same picture as a memory block, and Windows synthesises the handle forms from it on demand.
    /// </para>
    /// </summary>
    private static readonly uint[] PortableStandardFormats =
    [
        1,   // CF_TEXT
        7,   // CF_OEMTEXT
        8,   // CF_DIB
        13,  // CF_UNICODETEXT
        15,  // CF_HDROP
        16,  // CF_LOCALE
        17,  // CF_DIBV5
    ];

    /// <summary>Header size of one record: the format id and its length.</summary>
    private const int RecordHeaderSize = 12;

    /// <summary>
    /// Reads the portable payloads out of a clip file, or an empty list when the file holds nothing that can
    /// be replayed. Never throws for malformed content: a partially written clip from a crash is a file to
    /// skip, not a reason to abandon the import.
    /// </summary>
    public static IReadOnlyList<ClipPayload> TryReadPayloads(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var payloads = new List<ClipPayload>();
        var position = 0;

        while (position + 4 <= file.Length)
        {
            var format = BitConverter.ToUInt32(file, position);

            // Zero terminates the list. Anything after it is padding.
            if (format == 0)
            {
                break;
            }

            if (position + RecordHeaderSize > file.Length)
            {
                break;
            }

            var size = BitConverter.ToUInt64(file, position + 4);

            // Truncated: the declared length runs past the end of the file, so the rest cannot be trusted
            // either. Whatever was read before this point is still good and is kept.
            if (size > (ulong)(file.Length - position - RecordHeaderSize))
            {
                break;
            }

            var length = (int)size;

            if (PortableStandardFormats.Contains(format))
            {
                var data = new byte[length];
                Array.Copy(file, position + RecordHeaderSize, data, 0, length);

                // No FormatName: these are standard ids, which are durable, so there is nothing to
                // re-register on write.
                payloads.Add(new ClipPayload(format, null, data));
            }

            position += RecordHeaderSize + length;
        }

        // CF_LOCALE alone describes a codepage and nothing else - the same reason BookkeepingFormats treats it
        // as carrying no user content. A clip of only that is not worth a row in the stack.
        return payloads.Any(static p => p.FormatId != 16) ? payloads : [];
    }
}
