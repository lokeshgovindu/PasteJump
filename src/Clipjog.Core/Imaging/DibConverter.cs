using System.Buffers.Binary;

namespace Clipjog.Core.Imaging;

/// <summary>
/// Wraps a raw <c>CF_DIB</c> / <c>CF_DIBV5</c> payload in a BMP file header so WPF's
/// <c>BitmapDecoder</c> can read it.
/// <para>
/// The clipboard stores a device-independent bitmap starting at the info header, with no file
/// header - that 14-byte prefix only exists in .bmp files on disk. Handing the bare DIB to a
/// decoder fails, which is why clipboard image previews are so often missing in home-grown tools.
/// </para>
/// </summary>
public static class DibConverter
{
    private const int FileHeaderSize = 14;
    private const uint BI_BITFIELDS = 3;

    public static byte[]? TryCreateBitmapFile(byte[] dib)
    {
        if (dib is null || dib.Length < 12)
        {
            return null;
        }

        var infoHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);

        // 40 = BITMAPINFOHEADER, 108 = V4, 124 = V5. Anything else is not a DIB we understand.
        if (infoHeaderSize is not (40 or 108 or 124) || dib.Length < infoHeaderSize)
        {
            return null;
        }

        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));

        var paletteBytes = 0L;

        if (bitCount <= 8)
        {
            var entries = colorsUsed != 0 ? colorsUsed : 1u << bitCount;
            paletteBytes = entries * 4L;
        }
        else if (compression == BI_BITFIELDS && infoHeaderSize == 40)
        {
            // BI_BITFIELDS with the plain info header is followed by three channel masks, which
            // sit between the header and the pixels and must be counted in bfOffBits.
            paletteBytes = 12;
        }

        var pixelOffset = FileHeaderSize + infoHeaderSize + paletteBytes;

        if (pixelOffset > FileHeaderSize + dib.Length)
        {
            return null;
        }

        var result = new byte[FileHeaderSize + dib.Length];

        result[0] = (byte)'B';
        result[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2), (uint)result.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(10), (uint)pixelOffset);

        dib.CopyTo(result, FileHeaderSize);

        return result;
    }

    /// <summary>
    /// The inverse: recovers the bare <c>CF_DIB</c> payload from a BMP file by dropping the 14-byte
    /// file header.
    /// <para>
    /// Needed to put a history image back on the clipboard. History deliberately keeps only a rendered
    /// BMP rather than the original multi-format clip, so restoring an image means undoing exactly the
    /// wrapping <see cref="TryCreateBitmapFile"/> applied. Without this, the History window's Copy
    /// button had no image path at all and fell through to copying the preview text - which for an
    /// image is the literal string "[image]".
    /// </para>
    /// </summary>
    public static byte[]? TryExtractDib(byte[] bitmapFile)
    {
        if (bitmapFile is null || bitmapFile.Length <= FileHeaderSize + 12)
        {
            return null;
        }

        if (bitmapFile[0] != (byte)'B' || bitmapFile[1] != (byte)'M')
        {
            return null;
        }

        var infoHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(bitmapFile.AsSpan(FileHeaderSize));

        if (infoHeaderSize is not (40 or 108 or 124))
        {
            return null;
        }

        return bitmapFile[FileHeaderSize..];
    }

    /// <summary>
    /// Makes a 32bpp DIB opaque if - and only if - every one of its alpha bytes is zero. Returns null
    /// when nothing needed changing, so callers can keep the original array.
    /// <para>
    /// A fully-zero alpha channel cannot be a real image: it would be completely invisible, so nobody
    /// ever intends one. It happens in practice because plenty of producers fill 32bpp pixel data and
    /// simply never set the fourth byte - screenshot tools that go via PNG are a common source. Any
    /// consumer that honours alpha then renders the paste as nothing, which looks exactly like a
    /// clipboard manager that lost the image.
    /// </para>
    /// <para>
    /// The all-or-nothing condition is the important part. Genuinely transparent images have a mix of
    /// alpha values, so they are left strictly alone; normalising anything less than a wholly empty
    /// channel would flatten real transparency.
    /// </para>
    /// </summary>
    public static byte[]? TryMakeOpaqueIfFullyTransparent(byte[] dib)
    {
        if (dib is null || dib.Length < 40)
        {
            return null;
        }

        var infoHeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);

        if (infoHeaderSize is not (40 or 108 or 124) || dib.Length < infoHeaderSize)
        {
            return null;
        }

        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));

        if (bitCount != 32)
        {
            return null;
        }

        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));

        // BI_BITFIELDS with the plain info header puts three channel masks before the pixels.
        var pixelOffset = (int)infoHeaderSize + (compression == BI_BITFIELDS && infoHeaderSize == 40 ? 12 : 0);

        if (pixelOffset >= dib.Length || (dib.Length - pixelOffset) % 4 != 0)
        {
            return null;
        }

        for (var i = pixelOffset + 3; i < dib.Length; i += 4)
        {
            if (dib[i] != 0)
            {
                // At least one pixel carries alpha, so this channel is meaningful. Leave it be.
                return null;
            }
        }

        var fixedUp = (byte[])dib.Clone();

        for (var i = pixelOffset + 3; i < fixedUp.Length; i += 4)
        {
            fixedUp[i] = 0xFF;
        }

        return fixedUp;
    }
}
