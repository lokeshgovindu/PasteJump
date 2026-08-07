using Clipjog.Core.Model;
using Clipjog.Core.PasteMode;

namespace Clipjog.Core.Tests.Fakes;

/// <summary>In-memory catalog so controller tests never touch SQLite.</summary>
internal sealed class FakeClipCatalog : IClipCatalog
{
    private readonly List<Clip> _clips = [];
    private double _nextSortKey = 1.0;

    public int DeleteCallCount { get; private set; }

    public int DeleteAllCallCount { get; private set; }

    /// <summary>Adds a clip as the newest, with optional tags. Returns its id.</summary>
    public long Add(string preview, params string[] tags) => AddCore(preview, pinned: false, tags);

    /// <summary>Adds an already-pinned clip as the newest. Returns its id.</summary>
    public long AddPinned(string preview, params string[] tags) => AddCore(preview, pinned: true, tags);

    private long AddCore(string preview, bool pinned, string[] tags)
    {
        var id = _clips.Count == 0 ? 1 : _clips.Max(static c => c.Id) + 1;

        _clips.Add(new Clip
        {
            Id = id,
            SortKey = _nextSortKey++,
            Pinned = pinned,
            CreatedUtc = DateTimeOffset.UnixEpoch.AddSeconds(id),
            Preview = preview,
            Kind = ClipKind.Text,
            TotalBytes = preview.Length,
            ContentHash = "hash-" + id,
            Tags = tags,
        });

        return id;
    }

    public IReadOnlyList<Clip> Snapshot() =>
    [
        .. _clips
            .OrderByDescending(static c => c.Pinned)
            .ThenByDescending(static c => c.SortKey)
    ];

    public void Delete(long id)
    {
        DeleteCallCount++;
        _clips.RemoveAll(c => c.Id == id);
    }

    public void DeleteAllUnpinned()
    {
        DeleteAllCallCount++;
        _clips.RemoveAll(static c => !c.Pinned);
    }

    public void SetPinned(long id, bool pinned)
        => Replace(id, clip => Clone(clip, pinned: pinned, sortKey: clip.SortKey));

    public void MoveToFront(long id)
        => Replace(id, clip => Clone(clip, pinned: clip.Pinned, sortKey: _nextSortKey++));

    private void Replace(long id, Func<Clip, Clip> transform)
    {
        var index = _clips.FindIndex(c => c.Id == id);

        if (index >= 0)
        {
            _clips[index] = transform(_clips[index]);
        }
    }

    private static Clip Clone(Clip clip, bool pinned, double sortKey) => new()
    {
        Id = clip.Id,
        SortKey = sortKey,
        Pinned = pinned,
        CreatedUtc = clip.CreatedUtc,
        Preview = clip.Preview,
        Kind = clip.Kind,
        SourceExecutable = clip.SourceExecutable,
        TotalBytes = clip.TotalBytes,
        ContentHash = clip.ContentHash,
        Tags = clip.Tags,
    };
}
