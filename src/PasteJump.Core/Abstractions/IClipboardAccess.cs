using PasteJump.Core.Model;

namespace PasteJump.Core.Abstractions;

/// <summary>
/// The only door to the system clipboard. Implemented by <c>PasteJump.Interop</c>; faked in tests.
/// </summary>
public interface IClipboardAccess
{
    /// <summary>
    /// Reads every available format in one pass.
    /// <para>
    /// Returns null when the clipboard could not be opened. Implementations must use a
    /// <em>bounded</em> retry and then give up: the clipboard is a global lock that any process
    /// can hold, and the original's unbounded spins (<c>MakeClipboardAvailable</c> loops on
    /// <c>OpenClipboard</c> forever, <c>try_ClipboardfromFile</c> retries 100 times) are how it
    /// ends up wedged. A dropped capture is a far better failure than a hung UI thread.
    /// </para>
    /// </summary>
    ClipboardSnapshot? TryRead();

    /// <summary>Replaces the clipboard contents with the given formats. False if it could not be opened.</summary>
    bool TryWrite(IReadOnlyList<ClipPayload> payloads);

    /// <summary>
    /// <c>GetClipboardSequenceNumber</c>. A cheap monotonic counter for detecting that
    /// <em>something</em> changed without paying to open the clipboard.
    /// </summary>
    uint SequenceNumber { get; }
}
