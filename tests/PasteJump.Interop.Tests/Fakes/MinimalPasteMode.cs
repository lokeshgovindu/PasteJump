using PasteJump.Core.Formatting;
using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;

namespace PasteJump.Interop.Tests.Fakes;

/// <summary>
/// Just enough of a catalog and a host to drive a real <see cref="PasteModeController"/>.
/// <para>
/// Deliberately not shared with the richer recording fakes in PasteJump.Core.Tests: those are internal to that
/// assembly, and this project exists to test the seam <em>between</em> Interop's key table and Core's recogniser,
/// which needs almost nothing from a host. Duplicating twenty lines is cheaper than making Core.Tests' internals
/// public, and it keeps the two test projects independent.
/// </para>
/// </summary>
internal sealed class StubCatalog : IClipCatalog
{
    private readonly List<Clip> _clips = [];

    public StubCatalog(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            _clips.Add(new Clip
            {
                Id = i,
                SortKey = i,
                Pinned = false,
                CreatedUtc = DateTimeOffset.UnixEpoch.AddSeconds(i),
                Preview = $"clip {i}",
                Kind = ClipKind.Text,
                TotalBytes = 6,
                ContentHash = $"hash-{i}",
            });
        }
    }

    public IReadOnlyList<Clip> Snapshot() => [.. _clips.OrderByDescending(static c => c.SortKey)];

    public void Delete(long id) => _clips.RemoveAll(c => c.Id == id);

    public void DeleteAllUnpinned() => _clips.Clear();

    public void SetPinned(long id, bool pinned)
    {
    }

    public void MoveToFront(long id)
    {
    }
}

/// <summary>A host that records nothing but the overlay's visibility.</summary>
internal sealed class StubHost : IPasteModeHost
{
    public bool OverlayVisible { get; private set; }

    public void SnapshotExistingClipboard()
    {
    }

    public void RestoreExistingClipboard()
    {
    }

    public void PasteClip(Clip clip, IClipFormatter formatter)
    {
    }

    public void PasteJoined(IReadOnlyList<Clip> clips, IClipFormatter formatter)
    {
    }

    public void PassThroughPaste()
    {
    }

    public void PushToClipboard(Clip clip, IClipFormatter formatter)
    {
    }

    public void ShowOverlay(PasteOverlayModel model) => OverlayVisible = true;

    public void HideOverlay() => OverlayVisible = false;

    public void RequestTagEditor(Clip clip)
    {
    }

    public void RequestClipEditor(Clip clip)
    {
    }

    public void RequestExport(Clip clip)
    {
    }

    public void ShowShortcutHelp()
    {
    }

    public void RequestHistoryWindow()
    {
    }

    public void RequestDeleteAllConfirmation(int unpinnedCount, Action confirmed)
    {
    }

    public void ShowTransientMessage(string message)
    {
    }
}
