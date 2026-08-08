namespace PasteJump.Core.Model;

/// <summary>
/// Removes whole extra copies of the same image that Windows or the source application publishes alongside
/// the one worth keeping.
/// <para>
/// This is the difference between a 5 MB clip and a 500 KB one. The clipboard carries images uncompressed -
/// a 146 KB PNG arrives as megabytes of raw pixels - and a single copy typically turns up three times over
/// as <c>CF_DIB</c>, <c>CF_DIBV5</c> and <c>System.Drawing.Bitmap</c>. They differ only in header size
/// (+84 bytes for <c>BITMAPV5HEADER</c>, +14 for <c>BITMAPFILEHEADER</c>), so they are byte-different:
/// content addressing cannot dedupe them and compression cannot collapse them into one.
/// </para>
/// <para>
/// Verified against the original before being written: a Clipjump clip file for one screenshot contains a
/// single <c>CF_DIB</c> and no bitmap duplicate, which is a decade of shipped evidence that nothing real
/// depends on the copies.
/// </para>
/// <para>
/// Lives in <c>Core</c> rather than beside the Win32 clipboard code so it can be tested. The rules are
/// entirely about which formats subsume which, and none of that needs a clipboard to exercise.
/// </para>
/// </summary>
public static class RedundantImageFormats
{
    /// <summary><c>CF_DIB</c>. A <c>BITMAPINFOHEADER</c> followed by pixels.</summary>
    public const uint CfDib = 8;

    /// <summary><c>CF_DIBV5</c>. As <c>CF_DIB</c>, but the header can describe alpha and colour space.</summary>
    public const uint CfDibV5 = 17;

    /// <summary>
    /// Registered format names holding a second full-size copy of the image.
    /// <para>
    /// <c>System.Drawing.Bitmap</c> is what a .NET application publishes beside the standard formats: the
    /// same pixels with a <c>BITMAPFILEHEADER</c> in front. Unlike the DIB pair, Windows does <em>not</em>
    /// regenerate this one, so dropping it is a deliberate fidelity trade rather than a free saving. It is
    /// safe in practice because anything reading it can read <c>CF_BITMAP</c> or <c>CF_DIB</c> instead, and
    /// Windows synthesises both from what is kept.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> RedundantNames { get; } = ["System.Drawing.Bitmap"];

    /// <summary>
    /// Returns the payloads worth storing, dropping duplicate encodings of an image that is already present.
    /// <para>
    /// <c>CF_DIBV5</c> is preferred over <c>CF_DIB</c> when both are present. Windows synthesises either from
    /// the other, and the V5 header is the one able to describe alpha and colour space - so this keeps
    /// strictly more information than Clipjump, which keeps the plain <c>CF_DIB</c>, while saving the same
    /// bytes.
    /// </para>
    /// <para>
    /// Nothing is dropped unless a DIB survives. Without that guard, a clip carrying only
    /// <c>System.Drawing.Bitmap</c> would be stripped of its only image.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ClipPayload> Prune(IReadOnlyList<ClipPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        var hasDibV5 = false;
        var hasDib = false;

        foreach (var payload in payloads)
        {
            hasDibV5 |= payload.FormatId == CfDibV5;
            hasDib |= payload.FormatId == CfDib;
        }

        if (!hasDibV5 && !hasDib)
        {
            return payloads;
        }

        var pruned = new List<ClipPayload>(payloads.Count);

        foreach (var payload in payloads)
        {
            if (hasDibV5 && payload.FormatId == CfDib)
            {
                continue;
            }

            if (payload.FormatName is { } name && IsRedundantName(name))
            {
                continue;
            }

            pruned.Add(payload);
        }

        // Copy-on-change only where something was actually dropped, so the overwhelmingly common text clip
        // does not allocate a second list on every capture.
        return pruned.Count == payloads.Count ? payloads : pruned;
    }

    private static bool IsRedundantName(string name)
    {
        foreach (var redundant in RedundantNames)
        {
            if (string.Equals(name, redundant, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
