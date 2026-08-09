using System.Text;
using PasteJump.Core.Model;
using PasteJump.Core.Storage;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Idempotent history writes, and the repair for stores written before they were.
/// <para>
/// This is a bug that shipped: the import dialog said "entries already imported are skipped" and nothing
/// checked, so a Clipjump history imported four times held 28,488 rows where 7,122 were meant. The tests that
/// matter here are the ones proving what must NOT be collapsed - the destructive half of the fix.
/// </para>
/// </summary>
public sealed class HistoryDeduplicationTests : IDisposable
{
    private readonly string _root;
    private readonly ClipStore _store;

    private static readonly DateTimeOffset Moment = new(2026, 6, 2, 13, 40, 0, TimeSpan.Zero);

    public HistoryDeduplicationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pastejump-tests", Guid.NewGuid().ToString("n"));
        _store = new ClipStore(AppPaths.At(_root));
    }

    public void Dispose()
    {
        _store.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked WAL file must not fail the run.
        }
    }

    // ------------------------------------------------------- AddHistoryIfAbsent

    [Fact]
    public void An_identical_entry_is_not_added_twice()
    {
        Assert.NotNull(_store.AddHistoryIfAbsent(Moment, ClipKind.Text, "same", null, 4));
        Assert.Null(_store.AddHistoryIfAbsent(Moment, ClipKind.Text, "same", null, 4));

        Assert.Equal(1, _store.HistoryCount);
    }

    /// <summary>
    /// Replaying the same source four times is the reported case, and it has to converge on one row per entry.
    /// </summary>
    [Fact]
    public void Replaying_a_whole_source_four_times_leaves_one_of_each()
    {
        for (var run = 0; run < 4; run++)
        {
            for (var entry = 0; entry < 25; entry++)
            {
                _store.AddHistoryIfAbsent(
                    Moment.AddSeconds(entry),
                    ClipKind.Text,
                    $"entry {entry}",
                    null,
                    10);
            }
        }

        Assert.Equal(25, _store.HistoryCount);
    }

    [Fact]
    public void Entries_differing_only_in_time_are_both_kept()
    {
        _store.AddHistoryIfAbsent(Moment, ClipKind.Text, "same", null, 4);
        _store.AddHistoryIfAbsent(Moment.AddSeconds(1), ClipKind.Text, "same", null, 4);

        Assert.Equal(2, _store.HistoryCount);
    }

    /// <summary>
    /// The case the natural key exists for. Every image entry previews as <c>[image]</c>, so two different
    /// screenshots taken in the same second are indistinguishable on time, kind and preview alone - the blob
    /// hash is what tells them apart, and without it one of the two would be thrown away.
    /// </summary>
    [Fact]
    public void Two_different_images_captured_in_the_same_second_are_both_kept()
    {
        _store.AddHistoryIfAbsent(Moment, ClipKind.Image, "[image]", [1, 2, 3], 3);
        _store.AddHistoryIfAbsent(Moment, ClipKind.Image, "[image]", [9, 9, 9], 3);

        Assert.Equal(2, _store.HistoryCount);
    }

    [Fact]
    public void The_same_image_captured_in_the_same_second_is_added_once()
    {
        Assert.NotNull(_store.AddHistoryIfAbsent(Moment, ClipKind.Image, "[image]", [1, 2, 3], 3));
        Assert.Null(_store.AddHistoryIfAbsent(Moment, ClipKind.Image, "[image]", [1, 2, 3], 3));

        Assert.Equal(1, _store.HistoryCount);
    }

    /// <summary>
    /// A skipped row must not leave its blob behind. The blob is content-addressed, so the first write already
    /// put the bytes there - what this pins down is that a duplicate does not create an orphan that
    /// CollectGarbage would have to find later.
    /// </summary>
    [Fact]
    public void A_skipped_entry_leaves_the_blob_store_as_it_was()
    {
        byte[] image = [4, 5, 6];

        var entry = _store.AddHistoryIfAbsent(Moment, ClipKind.Image, "[image]", image, 3);
        Assert.NotNull(entry);

        var hash = BlobStore.ComputeHash(image);
        Assert.True(_store.Blobs.Exists(hash));

        Assert.Null(_store.AddHistoryIfAbsent(Moment, ClipKind.Image, "[image]", image, 3));

        // Still there and still referenced by the one surviving row, so a sweep must not reclaim it.
        _store.CollectGarbage();
        Assert.True(_store.Blobs.Exists(hash));
    }

    /// <summary>
    /// Text rows carry no blob, and SQLite's <c>=</c> never matches NULL against NULL - so the check has to use
    /// <c>IS</c>. With the wrong operator nothing textual would ever be recognised as a duplicate, which is most
    /// of a real history.
    /// </summary>
    [Fact]
    public void A_null_blob_hash_still_matches_itself()
    {
        _store.AddHistoryIfAbsent(Moment, ClipKind.Text, "no blob here", null, 12);

        Assert.Null(_store.AddHistoryIfAbsent(Moment, ClipKind.Text, "no blob here", null, 12));
    }

    /// <summary>
    /// Comparison is against the stored preview, which is truncated - so two long entries that differ only past
    /// the cap are one row. That is a consequence of the cap rather than of the dedupe, and it is deliberate:
    /// the alternative is comparing text the store does not keep.
    /// </summary>
    [Fact]
    public void Entries_differing_only_beyond_the_preview_cap_are_treated_as_one()
    {
        _store.PreviewMaxChars = 300;

        var head = new string('a', 300);

        Assert.NotNull(_store.AddHistoryIfAbsent(Moment, ClipKind.Text, head + "first", null, 0));
        Assert.Null(_store.AddHistoryIfAbsent(Moment, ClipKind.Text, head + "second", null, 0));
    }

    // ------------------------------------------------------- DeduplicateHistory

    [Fact]
    public void Deduplicate_removes_the_copies_and_reports_how_many()
    {
        for (var i = 0; i < 4; i++)
        {
            _store.AddHistory(Moment, ClipKind.Text, "duplicated", null, 10);
        }

        Assert.Equal(4, _store.HistoryCount);
        Assert.Equal(3, _store.DeduplicateHistory());
        Assert.Equal(1, _store.HistoryCount);
    }

    /// <summary>Oldest survives, so ids stay stable for whatever was imported first.</summary>
    [Fact]
    public void Deduplicate_keeps_the_earliest_row_of_each_group()
    {
        var first = _store.AddHistory(Moment, ClipKind.Text, "duplicated", null, 10);
        _store.AddHistory(Moment, ClipKind.Text, "duplicated", null, 10);

        _store.DeduplicateHistory();

        Assert.Equal(first, _store.SearchHistory(null).Single().Id);
    }

    [Fact]
    public void Deduplicate_leaves_entries_that_differ_alone()
    {
        _store.AddHistory(Moment, ClipKind.Text, "one", null, 3);
        _store.AddHistory(Moment, ClipKind.Text, "two", null, 3);
        _store.AddHistory(Moment.AddSeconds(1), ClipKind.Text, "one", null, 3);
        _store.AddHistory(Moment, ClipKind.Image, "[image]", [1], 1);
        _store.AddHistory(Moment, ClipKind.Image, "[image]", [2], 1);

        Assert.Equal(0, _store.DeduplicateHistory());
        Assert.Equal(5, _store.HistoryCount);
    }

    [Fact]
    public void Deduplicate_on_an_empty_history_does_nothing()
        => Assert.Equal(0, _store.DeduplicateHistory());

    /// <summary>
    /// The full-text index has to follow, or search keeps returning rows that are gone. It does because
    /// history_fts is external-content with an AFTER DELETE trigger - this is the test that would catch that
    /// wiring being lost.
    /// </summary>
    [Fact]
    public void Deduplicate_keeps_the_search_index_in_step()
    {
        for (var i = 0; i < 3; i++)
        {
            _store.AddHistory(Moment, ClipKind.Text, "findable text", null, 13);
        }

        Assert.Equal(3, _store.SearchHistory("findable").Count);

        _store.DeduplicateHistory();

        Assert.Single(_store.SearchHistory("findable"));
    }

    // --------------------------------------------------------- DeduplicateClips

    [Fact]
    public void Duplicate_clips_collapse_to_the_newest()
    {
        var first = _store.Add(TextSnapshot("same clip"), allowDuplicates: true);
        var second = _store.Add(TextSnapshot("same clip"), allowDuplicates: true);

        Assert.Equal(2, _store.Count);
        Assert.Equal(1, _store.DeduplicateClips());

        var survivor = Assert.Single(_store.GetOrdered());
        Assert.Equal(second.Id, survivor.Id);
        Assert.NotEqual(first.Id, survivor.Id);
    }

    /// <summary>
    /// Pinning is a deliberate act, so it outranks recency. Losing the pinned row and keeping the newer copy
    /// would silently discard it.
    /// </summary>
    [Fact]
    public void A_pinned_duplicate_survives_a_newer_unpinned_one()
    {
        var pinned = _store.Add(TextSnapshot("same clip"), allowDuplicates: true);
        _store.SetPinned(pinned.Id, pinned: true);
        _store.Add(TextSnapshot("same clip"), allowDuplicates: true);

        Assert.Equal(1, _store.DeduplicateClips());

        var survivor = Assert.Single(_store.GetOrdered());
        Assert.Equal(pinned.Id, survivor.Id);
        Assert.True(survivor.Pinned);
    }

    [Fact]
    public void Clips_that_differ_are_left_alone()
    {
        _store.Add(TextSnapshot("one"), allowDuplicates: true);
        _store.Add(TextSnapshot("two"), allowDuplicates: true);

        Assert.Equal(0, _store.DeduplicateClips());
        Assert.Equal(2, _store.Count);
    }

    /// <summary>The payload rows go with the clip, or the next paste reads formats belonging to a deleted row.</summary>
    [Fact]
    public void Removing_a_duplicate_clip_removes_its_payloads()
    {
        var first = _store.Add(TextSnapshot("same clip"), allowDuplicates: true);
        _store.Add(TextSnapshot("same clip"), allowDuplicates: true);

        _store.DeduplicateClips();

        Assert.Empty(_store.GetPayloads(first.Id));
    }

    [Fact]
    public void Deduplicate_clips_on_an_empty_stack_does_nothing()
        => Assert.Equal(0, _store.DeduplicateClips());

    private static ClipboardSnapshot TextSnapshot(string text)
    {
        var payload = new ClipPayload(13 /* CF_UNICODETEXT */, null, Encoding.Unicode.GetBytes(text));
        return new ClipboardSnapshot([payload], text, ClipKind.Text, null);
    }
}
