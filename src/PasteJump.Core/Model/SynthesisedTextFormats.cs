namespace PasteJump.Core.Model;

/// <summary>
/// The text formats Windows regenerates by itself, given <c>CF_UNICODETEXT</c> - and therefore the formats
/// that cannot be relied on to survive a write and come back unchanged.
/// <para>
/// Two things need this list and they must agree, which is why it is one list and not two. The clipboard
/// writer drops these formats deliberately (a stale <c>CF_TEXT</c> captured under a different system codepage
/// can contradict the <c>CF_UNICODETEXT</c> beside it, and whichever the target application happens to prefer
/// decides what the user gets). Windows then synthesises them again on the way out - <em>from the pasting
/// thread's own locale and codepage</em>, not from whatever the copying application published. So the bytes on
/// the clipboard after our write are genuinely not the bytes we handed over, and anything identifying a
/// payload set by its bytes has to leave these out or it will fail to recognise its own writing.
/// </para>
/// <para>
/// That is not hypothetical: it is the whole of the bug reported 2026-08-21 as a copy notification appearing
/// immediately after a paste in Edge. See <see cref="ClipboardSnapshot.SelfWriteKey"/> for the measurement.
/// </para>
/// <para>
/// Lives in <c>Core</c>, beside <see cref="RedundantImageFormats"/> and for the same reason: which format
/// subsumes which is pure knowledge about the clipboard's rules, and testing it needs no clipboard. The Win32
/// writer reads the list from here rather than keeping its own copy - the write filter and the identity rule
/// drifting apart is exactly the failure this describes.
/// </para>
/// </summary>
public static class SynthesisedTextFormats
{
    /// <summary><c>CF_TEXT</c>. The system-codepage rendering of the text.</summary>
    public const uint CfText = 1;

    /// <summary><c>CF_OEMTEXT</c>. The OEM-codepage rendering.</summary>
    public const uint CfOemText = 7;

    /// <summary><c>CF_UNICODETEXT</c>. The authoritative form, and the one the others are derived from.</summary>
    public const uint CfUnicodeText = 13;

    /// <summary>
    /// <c>CF_LOCALE</c>. Four bytes naming the locale the text was copied in.
    /// <para>
    /// The one that actually bit, because it varies by <em>keyboard layout</em> rather than by anything about
    /// the text: a clip copied under English (India) carries <c>0x4009</c>, and Windows synthesises
    /// <c>0x0409</c> for the same text when it is pasted under English (US). Same characters, same lengths,
    /// four different bytes.
    /// </para>
    /// </summary>
    public const uint CfLocale = 16;

    /// <summary>
    /// Formats Windows fills in for us when <see cref="CfUnicodeText"/> is present.
    /// </summary>
    public static IReadOnlyList<uint> FromUnicodeText { get; } = [CfText, CfOemText, CfLocale];

    /// <summary>Whether Windows would regenerate this format given <see cref="CfUnicodeText"/>.</summary>
    public static bool IsDerivedFromUnicodeText(uint formatId)
        => formatId is CfText or CfOemText or CfLocale;

    /// <summary>
    /// Drops the derived formats when the format they are derived from is present, leaving the payloads that
    /// genuinely carry what was copied.
    /// <para>
    /// Nothing is dropped without <see cref="CfUnicodeText"/> to derive it from, which matters: a clip holding
    /// only <c>CF_TEXT</c> would otherwise be reduced to nothing at all, and an empty set identifies every
    /// such clip as the same one.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ClipPayload> DropDerived(IReadOnlyList<ClipPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        var hasUnicodeText = false;
        var hasDerived = false;

        foreach (var payload in payloads)
        {
            hasUnicodeText |= payload.FormatId == CfUnicodeText;
            hasDerived |= IsDerivedFromUnicodeText(payload.FormatId);
        }

        // Copy-on-change only, so an image clip or a clip with nothing to drop does not allocate.
        if (!hasUnicodeText || !hasDerived)
        {
            return payloads;
        }

        var kept = new List<ClipPayload>(payloads.Count);

        foreach (var payload in payloads)
        {
            if (!IsDerivedFromUnicodeText(payload.FormatId))
            {
                kept.Add(payload);
            }
        }

        return kept;
    }
}
