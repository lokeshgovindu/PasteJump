namespace PasteJump.Core.Model;

/// <summary>
/// Clipboard formats that describe a copy without carrying any of it.
/// <para>
/// These exist because of how OLE publishes a clipboard: <c>OleSetClipboard</c> announces the data object
/// first and <c>OleFlushClipboard</c> renders the actual formats afterwards, each raising its own
/// notification with its own sequence number. Read at the wrong instant, the clipboard therefore holds
/// nothing but <c>DataObject</c> - eight bytes of OLE bookkeeping and not one byte of what the user copied.
/// </para>
/// <para>
/// Storing that produced a clip reported as <c>[binary]</c>, 8 bytes, from the Snipping Tool and from
/// anything else OLE-based. Worse, every such copy publishes the <em>same</em> eight bytes, so they all
/// hash alike: rather than accumulating, they repeatedly promoted one ancient clip to the front of the
/// stack, so the newest clip after taking a screenshot was a years-old 8-byte blob. That is what made this
/// look like "the screenshot was saved as binary" - the image was captured correctly, by the second
/// notification, and then buried by the promote from the first.
/// </para>
/// <para>
/// Matched by <em>name</em> for registered formats, never by id. Ids from <c>RegisterClipboardFormat</c>
/// are stable only for the Windows session, so a hard-coded 49161 would eventually name something else
/// entirely - the same reason <see cref="ClipPayload"/> persists the name.
/// </para>
/// </summary>
public static class BookkeepingFormats
{
    /// <summary>
    /// <c>CF_LOCALE</c>. Standard, so matched by id: it records which codepage accompanying text is in,
    /// which says nothing on its own.
    /// </summary>
    public const uint CfLocale = 16;

    /// <summary>
    /// Registered formats that never constitute a clip by themselves.
    /// <para>
    /// Deliberately short. The cost of a false entry here is a genuine copy silently discarded, so this
    /// lists only formats that are pure metadata: the OLE data-object marker, OLE's own private state, and
    /// the two descriptors that exist to describe an accompanying payload. Notably absent are
    /// <c>Embed Source</c> and <c>Link Source</c>, which do carry content and can legitimately be all a
    /// clipboard offers.
    /// </para>
    /// </summary>
    private static readonly string[] Names =
    [
        "DataObject",
        "Ole Private Data",
        "Object Descriptor",
        "Link Source Descriptor",
    ];

    /// <summary>
    /// The registered names, for callers that must express this rule somewhere other than in C# - the
    /// clean-up query in <c>ClipStore</c> being the one case. Exposed so the list stays defined once.
    /// </summary>
    public static IReadOnlyList<string> RegisteredNames => Names;

    /// <summary>True when this payload is bookkeeping rather than content.</summary>
    public static bool IsBookkeeping(ClipPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.FormatName is { Length: > 0 } name)
        {
            return Names.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        return payload.FormatId == CfLocale;
    }

    /// <summary>
    /// True when every format present is bookkeeping, so there is nothing worth storing yet. An empty set
    /// is <em>not</em> reported as contentless - that is an empty clipboard, which the caller already
    /// distinguishes and handles.
    /// </summary>
    public static bool CarriesNoUserContent(IReadOnlyList<ClipPayload> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);

        return payloads.Count > 0 && payloads.All(IsBookkeeping);
    }
}
