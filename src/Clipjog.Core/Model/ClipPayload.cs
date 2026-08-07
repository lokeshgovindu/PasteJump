namespace Clipjog.Core.Model;

/// <summary>
/// One clipboard format and its bytes, exactly as they came off (or will go back onto)
/// the clipboard.
/// </summary>
/// <param name="FormatId">
/// The Win32 clipboard format id - either a standard <c>CF_*</c> constant or a
/// dynamically registered id.
/// </param>
/// <param name="FormatName">
/// The registered name for non-standard formats ("HTML Format", "Rich Text Format",
/// "Biff12", ...), otherwise null.
/// <para>
/// This matters for correctness, not convenience: ids handed out by
/// <c>RegisterClipboardFormat</c> are only stable for the lifetime of a Windows
/// session. Persisting the numeric id alone and replaying it tomorrow would put the
/// bytes back under a completely unrelated format. So the NAME is the durable
/// identity and gets re-registered on write; the id is only meaningful in-process.
/// </para>
/// </param>
/// <param name="Data">The raw bytes of this format's clipboard data.</param>
public sealed record ClipPayload(uint FormatId, string? FormatName, byte[] Data)
{
    public int ByteLength => Data.Length;

    /// <summary>True when this format has a registered name rather than a standard CF_* id.</summary>
    public bool IsRegisteredFormat => !string.IsNullOrEmpty(FormatName);
}
