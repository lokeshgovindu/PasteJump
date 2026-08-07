using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;

namespace PasteJump.Core.Storage;

/// <summary>
/// Adapts <see cref="ClipStore"/> to the narrow <see cref="IClipCatalog"/> surface the state
/// machine needs. The indirection earns its keep: the controller's tests use a list-backed fake
/// and never touch SQLite.
/// </summary>
public sealed class ClipStoreCatalog(ClipStore store) : IClipCatalog
{
    private readonly ClipStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public IReadOnlyList<Clip> Snapshot() => _store.GetOrdered();

    public void Delete(long id) => _store.Delete(id);

    public void DeleteAllUnpinned() => _store.DeleteAll(includePinned: false);

    public void SetPinned(long id, bool pinned) => _store.SetPinned(id, pinned);

    public void MoveToFront(long id) => _store.MoveToFront(id);
}
