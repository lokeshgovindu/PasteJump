using System.Text;
using PasteJump.Core;
using PasteJump.Core.Model;
using PasteJump.Core.Storage;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Storage behaviour, exercised against a real SQLite file in a temp directory. These are
/// integration tests by nature - mocking SQLite would test nothing worth testing.
/// </summary>
public sealed class ClipStoreTests : IDisposable
{
    private readonly string _root;
    private readonly ClipStore _store;

    public ClipStoreTests()
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
            // Temp cleanup is best-effort; a locked WAL file must not fail the test run.
        }
    }

    private static ClipboardSnapshot TextSnapshot(string text, string? sourceExe = null)
    {
        var payload = new ClipPayload(13 /* CF_UNICODETEXT */, null, Encoding.Unicode.GetBytes(text));
        return new ClipboardSnapshot([payload], text, ClipKind.Text, sourceExe);
    }

    [Fact]
    public void Add_ThenGetOrdered_ReturnsNewestFirst()
    {
        _store.Add(TextSnapshot("first"));
        _store.Add(TextSnapshot("second"));
        _store.Add(TextSnapshot("third"));

        var clips = _store.GetOrdered();

        Assert.Equal(3, clips.Count);
        Assert.Equal("third", clips[0].Preview);
        Assert.Equal("first", clips[2].Preview);
    }

    [Fact]
    public void Add_RoundTripsEveryClipboardFormat()
    {
        var text = new ClipPayload(13, null, Encoding.Unicode.GetBytes("hello"));
        var html = new ClipPayload(49392, "HTML Format", Encoding.UTF8.GetBytes("<b>hello</b>"));
        var rtf = new ClipPayload(49393, "Rich Text Format", Encoding.ASCII.GetBytes(@"{\rtf1 hello}"));

        var clip = _store.Add(new ClipboardSnapshot([text, html, rtf], "hello", ClipKind.Text, "devenv.exe"));
        var payloads = _store.GetPayloads(clip.Id);

        Assert.Equal(3, payloads.Count);

        var restoredHtml = payloads.Single(p => p.FormatName == "HTML Format");
        Assert.Equal("<b>hello</b>", Encoding.UTF8.GetString(restoredHtml.Data));

        // Registered format names must survive, since their numeric ids are only valid for the
        // lifetime of a Windows session.
        Assert.Contains(payloads, p => p.FormatName == "Rich Text Format");
    }

    [Fact]
    public void Add_DuplicateContent_PromotesExistingInsteadOfInserting()
    {
        _store.Add(TextSnapshot("alpha"));
        _store.Add(TextSnapshot("beta"));
        _store.Add(TextSnapshot("alpha"));

        var clips = _store.GetOrdered();

        Assert.Equal(2, clips.Count);
        Assert.Equal("alpha", clips[0].Preview);
    }

    [Fact]
    public void Add_WithDuplicatesAllowed_InsertsSeparateRows()
    {
        _store.Add(TextSnapshot("alpha"));
        _store.Add(TextSnapshot("alpha"), allowDuplicates: true);

        Assert.Equal(2, _store.Count);
    }

    [Fact]
    public void Add_ReportsWhetherTheCaptureWasNew()
    {
        _store.Add(TextSnapshot("alpha"), allowDuplicates: false, out var firstWasNew);
        _store.Add(TextSnapshot("alpha"), allowDuplicates: false, out var secondWasNew);

        // A repeat notification for identical content promotes rather than inserts. Callers rely
        // on this to avoid double-logging history when a single OLE copy raises two clipboard
        // change notifications with different sequence numbers.
        Assert.True(firstWasNew);
        Assert.False(secondWasNew);
    }

    [Fact]
    public void Add_WithDuplicatesAllowed_AlwaysReportsANewCapture()
    {
        _store.Add(TextSnapshot("alpha"), allowDuplicates: true, out var firstWasNew);
        _store.Add(TextSnapshot("alpha"), allowDuplicates: true, out var secondWasNew);

        Assert.True(firstWasNew);
        Assert.True(secondWasNew);
    }

    [Fact]
    public void SetPinned_FloatsClipAboveNewerOnes()
    {
        var pinned = _store.Add(TextSnapshot("keep me"));
        _store.Add(TextSnapshot("newer"));
        _store.Add(TextSnapshot("newest"));

        _store.SetPinned(pinned.Id, true);

        var clips = _store.GetOrdered();

        Assert.Equal(pinned.Id, clips[0].Id);
        Assert.True(clips[0].Pinned);
    }

    [Fact]
    public void MoveToFront_ReordersWithASingleUpdate()
    {
        var target = _store.Add(TextSnapshot("promote me"));
        _store.Add(TextSnapshot("b"));
        _store.Add(TextSnapshot("c"));

        _store.MoveToFront(target.Id);

        Assert.Equal(target.Id, _store.GetOrdered()[0].Id);
    }

    [Fact]
    public void DeleteAll_KeepsPinnedByDefault()
    {
        var pinned = _store.Add(TextSnapshot("important"));
        _store.Add(TextSnapshot("throwaway"));
        _store.SetPinned(pinned.Id, true);

        _store.DeleteAll();

        var clips = _store.GetOrdered();
        Assert.Single(clips);
        Assert.Equal(pinned.Id, clips[0].Id);
    }

    [Fact]
    public void DeleteAll_IncludingPinned_ClearsEverything()
    {
        var pinned = _store.Add(TextSnapshot("important"));
        _store.SetPinned(pinned.Id, true);

        _store.DeleteAll(includePinned: true);

        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void Delete_CascadesToFormats()
    {
        var clip = _store.Add(TextSnapshot("gone soon"));

        _store.Delete(clip.Id);

        Assert.Empty(_store.GetPayloads(clip.Id));
        Assert.Null(_store.GetById(clip.Id));
    }

    [Fact]
    public void EvictBeyond_TrimsOldestUnpinnedOnly()
    {
        var pinned = _store.Add(TextSnapshot("pinned"));
        _store.SetPinned(pinned.Id, true);

        for (var i = 0; i < 10; i++)
        {
            _store.Add(TextSnapshot($"clip {i}"));
        }

        var removed = _store.EvictBeyond(3);

        Assert.Equal(7, removed);

        var clips = _store.GetOrdered();

        // Three unpinned survivors plus the pinned one, which is exempt from the ceiling.
        Assert.Equal(4, clips.Count);
        Assert.Contains(clips, c => c.Id == pinned.Id);
    }

    [Fact]
    public void Tags_RoundTripAndDeduplicate()
    {
        var clip = _store.Add(TextSnapshot("tagged"));

        _store.SetTags(clip.Id, ["work", "Work", "  db  ", string.Empty]);

        var tags = _store.GetTags(clip.Id);

        Assert.Equal(2, tags.Count);
        Assert.Contains("db", tags);
    }

    [Fact]
    public void Tags_AppearOnOrderedClips()
    {
        var clip = _store.Add(TextSnapshot("tagged"));
        _store.SetTags(clip.Id, ["alpha", "beta"]);

        var loaded = _store.GetOrdered().Single();

        Assert.Equal(2, loaded.Tags.Count);
        Assert.True(loaded.HasTags);
    }

    [Fact]
    public void Tags_CanBeReplaced()
    {
        var clip = _store.Add(TextSnapshot("tagged"));

        _store.SetTags(clip.Id, ["old"]);
        _store.SetTags(clip.Id, ["new"]);

        Assert.Equal(["new"], _store.GetTags(clip.Id));
    }

    [Fact]
    public void LargePayloads_GoToBlobStorageAndStillRoundTrip()
    {
        var big = new byte[BlobStore.InlineThresholdBytes + 4096];
        Random.Shared.NextBytes(big);

        var clip = _store.Add(new ClipboardSnapshot(
            [new ClipPayload(8 /* CF_DIB */, null, big)],
            null,
            ClipKind.Image,
            null));

        var payloads = _store.GetPayloads(clip.Id);

        Assert.Single(payloads);
        Assert.Equal(big, payloads[0].Data);
    }

    [Fact]
    public void ImageClipWithoutText_GetsAPlaceholderPreview()
    {
        var clip = _store.Add(new ClipboardSnapshot(
            [new ClipPayload(8, null, [1, 2, 3])],
            null,
            ClipKind.Image,
            null));

        Assert.Equal("[image]", clip.Preview);
    }

    [Fact]
    public void SourceExecutable_IsPersisted()
    {
        _store.Add(TextSnapshot("from vs", "devenv.exe"));

        Assert.Equal("devenv.exe", _store.GetOrdered()[0].SourceExecutable);
    }

    // ---------------------------------------------------------------- history

    [Fact]
    public void History_SearchFindsByPrefix()
    {
        _store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Text, "connection string for staging", null, 28);
        _store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Text, "unrelated content", null, 17);

        var hits = _store.SearchHistory("connect");

        Assert.Single(hits);
        Assert.Contains("connection", hits[0].Preview);
    }

    [Fact]
    public void History_SearchWithEmptyTermReturnsRecent()
    {
        _store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Text, "one", null, 3);
        _store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Text, "two", null, 3);

        Assert.Equal(2, _store.SearchHistory(null).Count);
        Assert.Equal(2, _store.SearchHistory("   ").Count);
    }

    [Fact]
    public void History_SearchRequiresAllTokens()
    {
        _store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Text, "alpha beta gamma", null, 16);

        Assert.Single(_store.SearchHistory("alpha gamma"));
        Assert.Empty(_store.SearchHistory("alpha zeta"));
    }

    [Fact]
    public void History_SearchSurvivesFtsOperatorCharactersInUserInput()
    {
        _store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Text, "a quoted \"value\" here", null, 21);

        // Raw FTS syntax in the search box must not throw a SqliteException at the user.
        var terms = new[] { "\"", "NEAR", "value\"", "*", "AND OR NOT", "a*b" };

        foreach (var term in terms)
        {
            _ = _store.SearchHistory(term);
        }
    }

    [Fact]
    public void History_DeleteRemovesFromSearchIndexToo()
    {
        var id = _store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Text, "findable text", null, 13);

        Assert.Single(_store.SearchHistory("findable"));

        _store.DeleteHistory(id);

        Assert.Empty(_store.SearchHistory("findable"));
    }

    [Fact]
    public void History_ClearEmptiesEverything()
    {
        _store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Text, "one", null, 3);
        _store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Text, "two", null, 3);

        _store.ClearHistory();

        Assert.Equal(0, _store.HistoryCount);
        Assert.Empty(_store.SearchHistory("one"));
    }

    [Fact]
    public void History_PruneRemovesOnlyEntriesOlderThanRetention()
    {
        var now = DateTimeOffset.UtcNow;

        _store.AddHistory(now.AddDays(-400), ClipKind.Text, "ancient", null, 7);
        _store.AddHistory(now.AddDays(-5), ClipKind.Text, "recent", null, 6);

        var removed = _store.PruneHistoryOlderThan(180);

        Assert.Equal(1, removed);
        Assert.Single(_store.SearchHistory(null));
    }

    [Fact]
    public void History_PruneWithZeroDaysKeepsEverything()
    {
        _store.AddHistory(DateTimeOffset.UtcNow.AddDays(-5000), ClipKind.Text, "ancient", null, 7);

        Assert.Equal(0, _store.PruneHistoryOlderThan(0));
        Assert.Equal(1, _store.HistoryCount);
    }

    [Fact]
    public void History_ImageEntryStoresBlobAndRecordsProvenance()
    {
        var bytes = new byte[2048];
        Random.Shared.NextBytes(bytes);

        _store.AddHistory(DateTimeOffset.UtcNow, ClipKind.Image, "[image]", bytes, bytes.Length, "clipjump-12.5");

        var entry = _store.SearchHistory(null).Single();

        Assert.NotNull(entry.BlobHash);
        Assert.Equal("clipjump-12.5", entry.ImportedFrom);
        Assert.Equal(bytes, _store.Blobs.TryRead(entry.BlobHash!));
    }

    // ---------------------------------------------------------------- maintenance

    [Fact]
    public void CollectGarbage_RemovesUnreferencedBlobsButKeepsLiveOnes()
    {
        var live = new byte[BlobStore.InlineThresholdBytes + 16];
        Random.Shared.NextBytes(live);

        var clip = _store.Add(new ClipboardSnapshot(
            [new ClipPayload(8, null, live)], null, ClipKind.Image, null));

        var orphanHash = _store.Blobs.Write([9, 9, 9]);

        Assert.True(_store.Blobs.Exists(orphanHash));

        var removed = _store.CollectGarbage();

        Assert.True(removed >= 1);
        Assert.False(_store.Blobs.Exists(orphanHash));
        Assert.Single(_store.GetPayloads(clip.Id));
    }

    [Fact]
    public void ReopeningTheStore_SeesPreviouslyPersistedClips()
    {
        _store.Add(TextSnapshot("durable"));
        _store.Checkpoint();

        using var reopened = new ClipStore(AppPaths.At(_root));

        Assert.Equal("durable", reopened.GetOrdered()[0].Preview);
    }
}
