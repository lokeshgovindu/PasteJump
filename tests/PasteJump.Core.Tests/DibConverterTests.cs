using System.Buffers.Binary;
using PasteJump.Core.Imaging;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// BMP file-header synthesis. The clipboard hands over a DIB starting at the info header with no
/// file header, so getting <c>bfOffBits</c> wrong silently produces an image that decodes to
/// garbage rather than failing outright - worth pinning down.
/// </summary>
public class DibConverterTests
{
    /// <summary>Builds a minimal BITMAPINFOHEADER-based DIB.</summary>
    private static byte[] BuildDib(
        uint headerSize = 40,
        ushort bitCount = 24,
        uint compression = 0,
        uint colorsUsed = 0,
        int pixelBytes = 64,
        int paletteBytes = 0)
    {
        var dib = new byte[headerSize + paletteBytes + pixelBytes];

        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(0), headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 4);    // width
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 4);    // height
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);  // planes
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), colorsUsed);

        return dib;
    }

    private static uint PixelOffsetOf(byte[] bitmapFile)
        => BinaryPrimitives.ReadUInt32LittleEndian(bitmapFile.AsSpan(10));

    [Fact]
    public void PrependsAValidBitmapFileHeader()
    {
        var result = DibConverter.TryCreateBitmapFile(BuildDib());

        Assert.NotNull(result);
        Assert.Equal((byte)'B', result![0]);
        Assert.Equal((byte)'M', result[1]);
        Assert.Equal((uint)result.Length, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(2)));
    }

    [Fact]
    public void FileIsExactlyFourteenBytesLongerThanTheDib()
    {
        var dib = BuildDib();
        var result = DibConverter.TryCreateBitmapFile(dib);

        Assert.Equal(dib.Length + 14, result!.Length);
    }

    [Fact]
    public void TrueColour_PixelsStartRightAfterTheInfoHeader()
    {
        var result = DibConverter.TryCreateBitmapFile(BuildDib(bitCount: 24));

        Assert.Equal(14u + 40u, PixelOffsetOf(result!));
    }

    [Fact]
    public void PalettedImage_CountsThePaletteInThePixelOffset()
    {
        // 8bpp with 256 entries of 4 bytes each: the palette sits between header and pixels.
        var result = DibConverter.TryCreateBitmapFile(
            BuildDib(bitCount: 8, colorsUsed: 256, paletteBytes: 256 * 4));

        Assert.Equal(14u + 40u + (256u * 4u), PixelOffsetOf(result!));
    }

    [Fact]
    public void PalettedImage_WithoutExplicitColourCount_AssumesTheFullPalette()
    {
        // colorsUsed of 0 means "all of them", so 4bpp implies 16 entries.
        var result = DibConverter.TryCreateBitmapFile(
            BuildDib(bitCount: 4, colorsUsed: 0, paletteBytes: 16 * 4));

        Assert.Equal(14u + 40u + (16u * 4u), PixelOffsetOf(result!));
    }

    [Fact]
    public void BitfieldsCompression_AccountsForTheThreeChannelMasks()
    {
        // BI_BITFIELDS with the plain 40-byte header is followed by three DWORD masks that sit
        // before the pixel data and must be included in bfOffBits.
        var result = DibConverter.TryCreateBitmapFile(
            BuildDib(bitCount: 32, compression: 3, paletteBytes: 12));

        Assert.Equal(14u + 40u + 12u, PixelOffsetOf(result!));
    }

    [Fact]
    public void BitmapV5Header_IsAccepted()
    {
        var result = DibConverter.TryCreateBitmapFile(BuildDib(headerSize: 124, bitCount: 32));

        Assert.NotNull(result);
        Assert.Equal(14u + 124u, PixelOffsetOf(result!));
    }

    [Theory]
    [InlineData(12)]   // BITMAPCOREHEADER - not supported
    [InlineData(999)]  // nonsense
    public void UnknownHeaderSize_IsRejectedRatherThanProducingJunk(uint headerSize)
    {
        var dib = new byte[200];
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(0), headerSize);

        Assert.Null(DibConverter.TryCreateBitmapFile(dib));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(11)]
    public void TruncatedInput_ReturnsNull(int length)
        => Assert.Null(DibConverter.TryCreateBitmapFile(new byte[length]));

    [Fact]
    public void NullInput_ReturnsNull() => Assert.Null(DibConverter.TryCreateBitmapFile(null!));

    [Fact]
    public void DibBytesArePreservedVerbatimAfterTheHeader()
    {
        var dib = BuildDib();
        Random.Shared.NextBytes(dib.AsSpan(40));

        var result = DibConverter.TryCreateBitmapFile(dib);

        Assert.Equal(dib, result!.AsSpan(14).ToArray());
    }
}
