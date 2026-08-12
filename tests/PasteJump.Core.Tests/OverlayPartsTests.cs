using PasteJump.Core.Model;
using PasteJump.Core.PasteMode;
using PasteJump.Core.Settings;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// Which parts of the paste overlay are drawn. Small, but two properties matter enough to pin: the defaults, since
/// they are what every existing install silently gets, and the line between cosmetic parts and state that changes
/// what a release does.
/// </summary>
public class OverlayPartsTests
{
    [Fact]
    public void Everything_is_on_by_default()
    {
        var parts = OverlayParts.All;

        Assert.True(parts.Position);
        Assert.True(parts.TextDetails);
        Assert.True(parts.TextSize);
        Assert.True(parts.ImageDetails);
        Assert.True(parts.ImageSize);
        Assert.True(parts.FileDetails);
        Assert.True(parts.FileSize);
        Assert.True(parts.Tags);
        Assert.True(parts.Source);
        Assert.True(parts.Formatter);
        Assert.True(parts.Pinned);
        Assert.True(parts.KeyHint);
    }

    /// <summary>
    /// A default-constructed value is the same as All, which is why this type is a record CLASS. As a record struct,
    /// new() zero-initialised and ignored the primary constructor's defaults, so All came out with every flag false -
    /// a fresh install would have shown an overlay with nothing on it.
    /// </summary>
    [Fact]
    public void A_default_value_shows_everything()
    {
        Assert.Equal(OverlayParts.All, new OverlayParts());
    }

    [Fact]
    public void Minimal_switches_everything_off()
    {
        var parts = OverlayParts.Minimal;

        Assert.False(parts.Position);
        Assert.False(parts.TextDetails);
        Assert.False(parts.TextSize);
        Assert.False(parts.ImageDetails);
        Assert.False(parts.ImageSize);
        Assert.False(parts.FileDetails);
        Assert.False(parts.FileSize);
        Assert.False(parts.Tags);
        Assert.False(parts.Source);
        Assert.False(parts.Formatter);
        Assert.False(parts.Pinned);
        Assert.False(parts.KeyHint);
    }

    /// <summary>
    /// The settings a fresh install has show everything, so switching the feature on for the first time changes
    /// nothing until the user asks for a change.
    /// </summary>
    [Fact]
    public void A_fresh_settings_object_shows_everything()
    {
        Assert.Equal(OverlayParts.All, new PasteJumpSettings().OverlayParts);
    }

    /// <summary>
    /// Each flag comes from its own setting. Written one at a time rather than all together, because the failure this
    /// guards is two of them wired to the same property - which no test asserting "all on" or "all off" would catch.
    /// </summary>
    [Fact]
    public void Each_setting_governs_its_own_part()
    {
        Assert.False(new PasteJumpSettings { ShowOverlayPosition = false }.OverlayParts.Position);
        Assert.False(new PasteJumpSettings { ShowOverlayTextDetails = false }.OverlayParts.TextDetails);
        Assert.False(new PasteJumpSettings { ShowOverlayTextSize = false }.OverlayParts.TextSize);
        Assert.False(new PasteJumpSettings { ShowOverlayImageDetails = false }.OverlayParts.ImageDetails);
        Assert.False(new PasteJumpSettings { ShowOverlayImageSize = false }.OverlayParts.ImageSize);
        Assert.False(new PasteJumpSettings { ShowOverlayFileDetails = false }.OverlayParts.FileDetails);
        Assert.False(new PasteJumpSettings { ShowOverlayFileSize = false }.OverlayParts.FileSize);
        Assert.False(new PasteJumpSettings { ShowOverlayTags = false }.OverlayParts.Tags);
        Assert.False(new PasteJumpSettings { ShowOverlaySource = false }.OverlayParts.Source);
        Assert.False(new PasteJumpSettings { ShowOverlayFormatter = false }.OverlayParts.Formatter);
        Assert.False(new PasteJumpSettings { ShowOverlayPinned = false }.OverlayParts.Pinned);
        Assert.False(new PasteJumpSettings { ShowOverlayKeyHint = false }.OverlayParts.KeyHint);
    }

    /// <summary>
    /// Turning one off leaves the rest alone - the shape of bug where a flags value is rebuilt and one field is
    /// dropped on the way through.
    /// </summary>
    [Fact]
    public void Switching_one_part_off_leaves_the_others_on()
    {
        var parts = new PasteJumpSettings { ShowOverlayTextSize = false }.OverlayParts;

        Assert.False(parts.TextSize);
        Assert.True(parts.TextDetails);
        Assert.True(parts.ImageSize);
        Assert.True(parts.Position);
        Assert.True(parts.KeyHint);
    }

    /// <summary>
    /// Each kind asks its own switches. This is the whole feature: "resolution for pictures, nothing for text" has to
    /// be expressible, and one pair of switches for all three kinds could not say it.
    /// </summary>
    [Fact]
    public void Each_kind_reads_its_own_switches()
    {
        var parts = new PasteJumpSettings
        {
            ShowOverlayTextDetails = false,
            ShowOverlayTextSize = false,
            ShowOverlayImageDetails = true,
            ShowOverlayImageSize = false,
            ShowOverlayFileDetails = false,
            ShowOverlayFileSize = true,
        }.OverlayParts;

        Assert.False(parts.DetailsFor(ClipKind.Text));
        Assert.False(parts.SizeFor(ClipKind.Text));

        Assert.True(parts.DetailsFor(ClipKind.Image));
        Assert.False(parts.SizeFor(ClipKind.Image));

        Assert.False(parts.DetailsFor(ClipKind.Files));
        Assert.True(parts.SizeFor(ClipKind.Files));
    }

    /// <summary>
    /// A binary clip follows the file switches rather than having a pair of its own. It has no details worth printing,
    /// so what this really decides is whether its size is shown - and giving it its own row would cost every user a
    /// decision to save one user a line.
    /// </summary>
    [Fact]
    public void A_binary_clip_follows_the_file_switches()
    {
        var parts = new PasteJumpSettings { ShowOverlayFileSize = false, ShowOverlayFileDetails = false }.OverlayParts;

        Assert.False(parts.SizeFor(ClipKind.Other));
        Assert.False(parts.DetailsFor(ClipKind.Other));

        var on = new PasteJumpSettings { ShowOverlayFileSize = true }.OverlayParts;

        Assert.True(on.SizeFor(ClipKind.Other));
    }

    /// <summary>
    /// Every <see cref="ClipKind"/> resolves to something, so a kind added later cannot fall through into "hidden"
    /// silently - the switch expression's fallback is the file pair, which is the safe answer rather than false.
    /// </summary>
    [Fact]
    public void Every_kind_is_answered()
    {
        foreach (var kind in Enum.GetValues<ClipKind>())
        {
            Assert.True(OverlayParts.All.DetailsFor(kind), kind.ToString());
            Assert.True(OverlayParts.All.SizeFor(kind), kind.ToString());
            Assert.False(OverlayParts.Minimal.DetailsFor(kind), kind.ToString());
            Assert.False(OverlayParts.Minimal.SizeFor(kind), kind.ToString());
        }
    }

    /// <summary>
    /// The switchboard covers only cosmetic parts, and there is deliberately nothing here for the POP chip, the JOIN
    /// count, the kind filter or the commit-mode banner. Each of those changes what releasing Ctrl will do, and a user
    /// who could hide one would not have tidied the overlay - they would have armed a deletion they cannot see.
    /// <para>
    /// Asserted as a count so that adding a switch forces this to be read: if the new one is cosmetic, raise the
    /// number; if it is not, it does not belong here at all.
    /// </para>
    /// </summary>
    [Fact]
    public void Only_cosmetic_parts_can_be_switched_off()
    {
        var switches = typeof(OverlayParts).GetProperties().Where(static p => p.PropertyType == typeof(bool)).ToList();

        Assert.Equal(12, switches.Count);

        foreach (var forbidden in new[] { "Pop", "Join", "Marked", "KindFilter", "CommitMode", "Preview", "Banner" })
        {
            Assert.DoesNotContain(forbidden, switches.Select(static p => p.Name));
        }
    }
}
