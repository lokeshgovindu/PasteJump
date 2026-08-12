namespace PasteJump.Core.Imaging;

/// <summary>One image inside an <c>.ico</c>, and where its bytes are.</summary>
/// <param name="Width">Pixels. 256 for a frame whose stored width byte is 0, which is how the format says 256.</param>
/// <param name="BitCount">Colour depth, used only to break ties between two frames of the same size.</param>
/// <param name="Offset">Where this frame's image data starts within the file.</param>
/// <param name="Length">How many bytes it occupies.</param>
public readonly record struct IconFrame(int Width, int Height, int BitCount, int Offset, int Length);

/// <summary>
/// Reads the frame table of an <c>.ico</c> so one frame can be handed to Windows at a chosen size.
/// <para>
/// This exists because the notification area asks for a specific size - 16 px at 100% scaling, 24 at 150% -
/// and every way of getting an icon has a different answer to "which size?". <c>ExtractIconEx</c> offers only
/// the two system sizes, 32 and 16, so it cannot reach 24 at all; <c>LoadImage</c> honours a requested size
/// but only from a loose file on disk. <c>CreateIconFromResourceEx</c> takes a size <em>and</em> raw bytes,
/// which is what lets the icons be embedded in the executable - and it wants the bytes of a single frame,
/// not the whole file. Hence this.
/// </para>
/// <para>
/// Pure byte parsing, so it lives in <c>Core</c> and is tested here rather than being verified by eye at the
/// far end of a P/Invoke. Nothing in it knows what a window is.
/// </para>
/// </summary>
public static class IconFile
{
    /// <summary>Bytes in an ICONDIR: reserved, type, count.</summary>
    private const int DirectoryHeaderLength = 6;

    /// <summary>Bytes in an ICONDIRENTRY.</summary>
    private const int EntryLength = 16;

    /// <summary>
    /// Reads every frame, newest format first. Returns an empty list for anything that is not a plausible
    /// icon file rather than throwing.
    /// <para>
    /// Lenient by choice: this runs during start-up to draw the tray icon, and an exception there would take
    /// down the application over a decoration. A caller that gets nothing back leaves the current icon alone.
    /// </para>
    /// </summary>
    public static IReadOnlyList<IconFrame> ReadFrames(ReadOnlySpan<byte> ico)
    {
        if (ico.Length < DirectoryHeaderLength)
        {
            return [];
        }

        // idReserved must be 0 and idType 1 for an icon (2 is a cursor). Checked because a .png or a .bmp
        // renamed to .ico would otherwise parse as a frame count of whatever its first bytes happen to say,
        // and then produce offsets pointing anywhere.
        var reserved = ReadUInt16(ico, 0);
        var type = ReadUInt16(ico, 2);
        var count = ReadUInt16(ico, 4);

        if (reserved != 0 || type != 1 || count == 0)
        {
            return [];
        }

        if (ico.Length < DirectoryHeaderLength + (count * EntryLength))
        {
            return [];
        }

        var frames = new List<IconFrame>(count);

        for (var i = 0; i < count; i++)
        {
            var entry = DirectoryHeaderLength + (i * EntryLength);

            // A stored 0 means 256: the field is one byte, so 256 does not fit and the format spends the
            // only spare value on it. Getting this wrong yields a 0x0 frame that sorts before every real one.
            var width = ico[entry] == 0 ? 256 : ico[entry];
            var height = ico[entry + 1] == 0 ? 256 : ico[entry + 1];

            var bitCount = ReadUInt16(ico, entry + 6);
            var length = (int)ReadUInt32(ico, entry + 8);
            var offset = (int)ReadUInt32(ico, entry + 12);

            // Bounds-checked per frame rather than trusting the table. A truncated download or a partly
            // written file gives offsets past the end, and the result would be a read out of the buffer.
            if (length <= 0 || offset < 0 || (long)offset + length > ico.Length)
            {
                continue;
            }

            frames.Add(new IconFrame(width, height, bitCount, offset, length));
        }

        return frames;
    }

    /// <summary>
    /// Picks the frame to render at <paramref name="size"/> pixels, or null if there is nothing usable.
    /// <para>
    /// Prefers an exact match, then the smallest frame <em>larger</em> than asked for, and only then the
    /// largest smaller one. That order is the point: shrinking an image keeps its detail, while enlarging one
    /// invents it - and an upscaled tray icon looking soft is a bug this project has already shipped twice, in
    /// the About window's logo and in the program picker's list.
    /// </para>
    /// <para>
    /// Depth breaks ties, since a 32-bit frame and an 8-bit frame of the same size are both exact matches and
    /// the deeper one is what a modern taskbar should show.
    /// </para>
    /// </summary>
    public static IconFrame? SelectFrame(IReadOnlyList<IconFrame> frames, int size)
    {
        if (frames.Count == 0 || size <= 0)
        {
            return null;
        }

        IconFrame? best = null;
        var bestRank = (int.MaxValue, int.MaxValue, int.MinValue);

        foreach (var frame in frames)
        {
            // Ranked on three keys in order: whether it needs enlarging at all, how far it is from the
            // requested size, and then depth. Written as a tuple comparison rather than a sort so the first
            // frame of an equal pair wins, which keeps the choice stable for a given file.
            var rank = (frame.Width >= size ? 0 : 1, Math.Abs(frame.Width - size), frame.BitCount);

            if (best is null
                || rank.Item1 < bestRank.Item1
                || (rank.Item1 == bestRank.Item1 && rank.Item2 < bestRank.Item2)
                || (rank.Item1 == bestRank.Item1 && rank.Item2 == bestRank.Item2 && rank.Item3 > bestRank.Item3))
            {
                best = frame;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>Reads and selects in one step, which is all any caller here wants.</summary>
    public static IconFrame? SelectFrame(ReadOnlySpan<byte> ico, int size)
        => SelectFrame(ReadFrames(ico), size);

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
        => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
        => (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
}
