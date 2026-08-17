namespace PasteJump.Core.Model;

/// <summary>
/// Names what a clip that is neither text, an image nor files actually holds.
/// </summary>
/// <remarks>
/// <para>
/// Written from a real report: two screenshots were taken and one of them arrived as <c>Other</c>, 708 bytes,
/// previewed as <c>[binary]</c> - which says only that PasteJump could not make anything of it, and leaves the
/// obvious question (what went wrong?) answerable solely by reading the database. It had one real payload,
/// <c>System.Drawing.Bitmap</c> at 484 bytes: a .NET object handed to the clipboard instead of a bitmap, with no
/// <c>CF_DIB</c> beside it and far too few bytes to be the picture. Naming the format in the preview says that
/// much on the row itself.
/// </para>
/// <para>
/// The largest payload wins, with no list of formats to ignore. The OLE bookkeeping that accompanies such a clip -
/// <c>Ole Private Data</c>, <c>DataObject</c>, the cloud-clipboard flags - is small by nature, so size already
/// sorts the subject of the clip from the paperwork about it, and a name list would be one more thing to keep.
/// </para>
/// </remarks>
public static class BinaryPreview
{
    /// <summary>How much of a format name is kept. Long enough for the real ones, short enough for a row.</summary>
    public const int MaxNameChars = 48;

    /// <summary>What an unnameable clip is called. Also the answer when there is nothing to name.</summary>
    public const string Fallback = "[binary]";

    /// <summary>
    /// <c>[binary: System.Drawing.Bitmap]</c>, or <see cref="Fallback"/> when no payload can be named.
    /// </summary>
    /// <remarks>
    /// Standard formats have no registered name, so they are numbered instead: <c>[binary: format #8]</c> beats
    /// <c>[binary]</c> for the same reason the whole method exists. Note this string is what <c>history_fts</c>
    /// indexes, which makes these clips findable by format name - the search for "System.Drawing" now has
    /// something to match.
    /// </remarks>
    public static string Describe(IReadOnlyList<ClipPayload>? payloads)
    {
        if (payloads is null || payloads.Count == 0)
        {
            return Fallback;
        }

        ClipPayload? biggest = null;

        foreach (var payload in payloads)
        {
            if (biggest is null || payload.Data.Length > biggest.Data.Length)
            {
                biggest = payload;
            }
        }

        if (biggest is null)
        {
            return Fallback;
        }

        var name = string.IsNullOrWhiteSpace(biggest.FormatName)
            ? $"format #{biggest.FormatId}"
            : biggest.FormatName.Trim();

        if (name.Length > MaxNameChars)
        {
            name = name[..MaxNameChars];
        }

        return $"[binary: {name}]";
    }
}
