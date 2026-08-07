using PasteJump.Core.Formatting;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Tests.Fakes;
using Xunit;

namespace PasteJump.Core.Tests;

public class PasteModeSearchTests
{
    private static (PasteModeController Controller, FakeClipCatalog Catalog, RecordingPasteModeHost Host) Build()
    {
        var catalog = new FakeClipCatalog();
        catalog.Add("the quick brown fox");
        catalog.Add("connection string for staging", "work", "db");
        catalog.Add("lorem ipsum dolor");
        catalog.Add("SELECT * FROM users", "sql");

        var host = new RecordingPasteModeHost();
        var controller = new PasteModeController(
            catalog, host, new FormatterRegistry(),
            new PasteModeOptions { PreserveClipPosition = false });

        return (controller, catalog, host);
    }

    [Fact]
    public void Search_FiltersToMatchingClips()
    {
        var (controller, _, _) = Build();

        controller.Begin();
        controller.Handle(PasteAction.ToggleSearch);
        controller.SetSearchQuery("lorem");

        Assert.Single(controller.Window);
        Assert.Equal("lorem ipsum dolor", controller.Window[0].Preview);
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var (controller, _, _) = Build();

        controller.Begin();
        controller.Handle(PasteAction.ToggleSearch);
        controller.SetSearchQuery("SELECT");

        Assert.Single(controller.Window);

        controller.SetSearchQuery("select");
        Assert.Single(controller.Window);
    }

    [Fact]
    public void Search_RequiresAllTokens()
    {
        var (controller, _, _) = Build();

        controller.Begin();
        controller.Handle(PasteAction.ToggleSearch);

        controller.SetSearchQuery("connection staging");
        Assert.Single(controller.Window);

        controller.SetSearchQuery("connection nonexistent");
        Assert.Empty(controller.Window);
    }

    [Fact]
    public void Search_MatchesTagsAsWellAsContent()
    {
        var (controller, _, _) = Build();

        controller.Begin();
        controller.Handle(PasteAction.ToggleSearch);

        // "db" appears only in the tags, never in the preview text.
        controller.SetSearchQuery("db");

        Assert.Single(controller.Window);
        Assert.Equal("connection string for staging", controller.Window[0].Preview);
    }

    [Fact]
    public void Search_WithNoMatches_LeavesSessionActiveAndReportsZero()
    {
        var (controller, _, host) = Build();

        controller.Begin();
        controller.Handle(PasteAction.ToggleSearch);
        controller.SetSearchQuery("zzzzz-no-match");

        Assert.True(controller.IsActive);
        Assert.Empty(controller.Window);
        Assert.Null(controller.Current);
        Assert.Equal(0, host.LastFrame!.MatchCount);
        Assert.True(host.LastFrame!.IsEmpty);
    }

    [Fact]
    public void CommittingWithNoSearchMatches_PassesThroughRatherThanPastingSomethingElse()
    {
        var (controller, _, host) = Build();

        controller.Begin();
        controller.Handle(PasteAction.ToggleSearch);
        controller.SetSearchQuery("zzzzz-no-match");

        var kind = controller.CommitExplicitly();

        // Pasting an arbitrary clip because the filter matched nothing would be worse than
        // doing nothing, so this falls back to a native paste.
        Assert.Equal(PasteCommitKind.PassedThrough, kind);
        Assert.Empty(host.PastedClips);
    }

    [Fact]
    public void LeavingSearch_DropsFilterButKeepsCurrentClip()
    {
        var (controller, _, _) = Build();

        controller.Begin();
        controller.Handle(PasteAction.ToggleSearch);
        controller.SetSearchQuery("lorem");

        var landed = controller.Current!.Id;

        controller.Handle(PasteAction.ToggleSearch);

        Assert.Equal(PasteSessionState.Browsing, controller.State);
        Assert.Equal(4, controller.Window.Count);
        Assert.Equal(landed, controller.Current!.Id);
    }

    [Fact]
    public void SetSearchQuery_IsIgnoredWhenNotSearching()
    {
        var (controller, _, _) = Build();

        controller.Begin();
        controller.SetSearchQuery("lorem");

        Assert.Equal(4, controller.Window.Count);
        Assert.Equal(string.Empty, controller.SearchQuery);
    }

    [Fact]
    public void OpenSearchImmediately_StartsInSearchState()
    {
        var catalog = new FakeClipCatalog();
        catalog.Add("something");

        var controller = new PasteModeController(
            catalog,
            new RecordingPasteModeHost(),
            new FormatterRegistry(),
            new PasteModeOptions { OpenSearchImmediately = true, PreserveClipPosition = false });

        controller.Begin();

        Assert.Equal(PasteSessionState.Searching, controller.State);
    }
}
