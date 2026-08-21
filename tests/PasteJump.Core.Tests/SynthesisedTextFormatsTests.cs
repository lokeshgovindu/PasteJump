using System.Text;
using PasteJump.Core.Model;
using Xunit;

namespace PasteJump.Core.Tests;

/// <summary>
/// The formats Windows refills for itself, and the identity key that has to ignore them.
/// <para>
/// These exist because of a reported bug, and the numbers in them are measured rather than invented: pressing
/// Ctrl+V in Edge showed the paste overlay and then a <em>copy</em> notification, because PasteJump did not
/// recognise the clipboard it had written a moment earlier. The two clips involved were read out of the live
/// store - same 66 characters, same four formats, same 2,044 bytes, differing in exactly one byte pair:
/// <c>CF_LOCALE</c> held <c>0x4009</c> (English, India) as captured and <c>0x0409</c> (English, US) as Windows
/// synthesised it on the way back out.
/// </para>
/// </summary>
public sealed class SynthesisedTextFormatsTests
{
    /// <summary>English (India), which is what the reported clip was copied under.</summary>
    private static readonly byte[] LocaleEnIn = [0x09, 0x40, 0x00, 0x00];

    /// <summary>English (US), which is what Windows synthesised when the same clip was pasted.</summary>
    private static readonly byte[] LocaleEnUs = [0x09, 0x04, 0x00, 0x00];

    [Fact]
    public void DerivedFormatsAreDroppedWhenUnicodeTextIsPresent()
    {
        var kept = SynthesisedTextFormats.DropDerived(
        [
            Text("hello"),
            Ansi("hello"),
            Oem("hello"),
            Locale(LocaleEnIn),
        ]);

        Assert.Equal([SynthesisedTextFormats.CfUnicodeText], kept.Select(static p => p.FormatId));
    }

    /// <summary>
    /// Nothing is dropped without the format they are derived from. A clip holding only <c>CF_TEXT</c> would
    /// otherwise be reduced to an empty set - and an empty set identifies every such clip as the same one, which
    /// would make one paste suppress the capture of an unrelated copy.
    /// </summary>
    [Fact]
    public void NothingIsDroppedWithoutUnicodeTextToDeriveItFrom()
    {
        IReadOnlyList<ClipPayload> payloads = [Ansi("hello"), Locale(LocaleEnIn)];

        Assert.Same(payloads, SynthesisedTextFormats.DropDerived(payloads));
    }

    [Fact]
    public void RicherFormatsBesideTheTextSurvive()
    {
        var kept = SynthesisedTextFormats.DropDerived(
        [
            Text("hello"),
            Ansi("hello"),
            new ClipPayload(49384, "HTML Format", Encoding.UTF8.GetBytes("<b>hello</b>")),
        ]);

        Assert.Equal([SynthesisedTextFormats.CfUnicodeText, 49384u], kept.Select(static p => p.FormatId));
    }

    /// <summary>An image clip has nothing to drop, and must not pay for a copy of its payload list.</summary>
    [Fact]
    public void APayloadSetWithNothingToDropIsReturnedUnchanged()
    {
        IReadOnlyList<ClipPayload> payloads = [new ClipPayload(8, null, [1, 2, 3])];

        Assert.Same(payloads, SynthesisedTextFormats.DropDerived(payloads));
    }

    /// <summary>
    /// The reported failure, stated as the two hashes disagreeing on purpose. The content hash must still see a
    /// difference - it identifies a clip, and these genuinely are different bytes - while the self-write key
    /// must not, because that difference is Windows' own doing rather than anything the user copied.
    /// </summary>
    [Fact]
    public void ADifferentLocaleIsTheSameWriteButNotTheSameContent()
    {
        var captured = Snapshot(Text("clip"), Ansi("clip"), Oem("clip"), Locale(LocaleEnIn));
        var readBack = Snapshot(Text("clip"), Ansi("clip"), Oem("clip"), Locale(LocaleEnUs));

        Assert.Equal(captured.SelfWriteKey, readBack.SelfWriteKey);
        Assert.NotEqual(captured.ContentHash, readBack.ContentHash);
    }

    /// <summary>
    /// The same reasoning for <c>CF_TEXT</c>: it is the text in the system codepage, so a machine whose codepage
    /// differs from the one the clip was captured under regenerates different bytes for the same characters.
    /// </summary>
    [Fact]
    public void ADifferentAnsiRenderingIsStillTheSameWrite()
    {
        var captured = Snapshot(Text("café"), new ClipPayload(1, null, [0x63, 0x61, 0x66, 0xE9, 0x00]));
        var readBack = Snapshot(Text("café"), new ClipPayload(1, null, [0x63, 0x61, 0x66, 0x3F, 0x00]));

        Assert.Equal(captured.SelfWriteKey, readBack.SelfWriteKey);
    }

    /// <summary>
    /// Two different clips must not share a self-write key. This is the property the whole guard rests on: the
    /// key decides whether a clipboard change is ignored, so one that collided would silently swallow a real copy.
    /// </summary>
    [Fact]
    public void DifferentTextIsADifferentWrite()
    {
        var one = Snapshot(Text("one"), Locale(LocaleEnIn));
        var other = Snapshot(Text("other"), Locale(LocaleEnIn));

        Assert.NotEqual(one.SelfWriteKey, other.SelfWriteKey);
    }

    /// <summary>
    /// Where there is nothing to drop the key is the content hash itself, not a second hash of the same bytes.
    /// Worth pinning: it is what makes this change a no-op for images, file lists and every existing store.
    /// </summary>
    [Fact]
    public void WithNothingToDropTheKeyIsTheContentHash()
    {
        var snapshot = Snapshot(Text("plain"));

        Assert.Equal(snapshot.ContentHash, snapshot.SelfWriteKey);
    }

    private static ClipPayload Text(string text)
        => new(SynthesisedTextFormats.CfUnicodeText, null, Encoding.Unicode.GetBytes(text + '\0'));

    private static ClipPayload Ansi(string text)
        => new(SynthesisedTextFormats.CfText, null, Encoding.ASCII.GetBytes(text + '\0'));

    private static ClipPayload Oem(string text)
        => new(SynthesisedTextFormats.CfOemText, null, Encoding.ASCII.GetBytes(text + '\0'));

    private static ClipPayload Locale(byte[] lcid) => new(SynthesisedTextFormats.CfLocale, null, lcid);

    private static ClipboardSnapshot Snapshot(params ClipPayload[] payloads)
    {
        var text = Encoding.Unicode
            .GetString(payloads.First(static p => p.FormatId == SynthesisedTextFormats.CfUnicodeText).Data)
            .TrimEnd('\0');

        return new ClipboardSnapshot(payloads, text, ClipKind.Text, "msedge.exe");
    }
}
