using PasteJump.Core.Model;

namespace PasteJump.Core.PasteMode;

/// <summary>
/// Narrows the stack to one kind of clip while browsing.
/// <para>
/// It exists because images are sparse in a stack dominated by text, and they are the clips most worth
/// <em>looking</em> at before pasting - so reaching the screenshot from twenty minutes ago meant tapping past a
/// dozen text clips. Clipjump has no equivalent; Ditto does, which is the closest precedent.
/// </para>
/// <para>
/// There was already a crude route: an image clip's stored preview is the literal string <c>[image]</c>, so
/// searching for <c>image</c> filtered the window. That worked by accident, was documented nowhere, and rested on
/// display text behaving like an API - this replaces it with something meant.
/// </para>
/// </summary>
public enum PasteKindFilter
{
    /// <summary>Everything, and where every session starts.</summary>
    All = 0,

    Text,
    Images,
    Files,
}

public static class PasteKindFilterExtensions
{
    /// <summary>
    /// The next filter in the cycle. Wraps back to <see cref="PasteKindFilter.All"/>, unlike the <c>X</c> commit
    /// cycle which deliberately never returns to pasting - the difference being that this one is not
    /// destructive, so getting back to seeing everything must not require three more taps.
    /// </summary>
    public static PasteKindFilter Next(this PasteKindFilter filter) => filter switch
    {
        PasteKindFilter.All => PasteKindFilter.Text,
        PasteKindFilter.Text => PasteKindFilter.Images,
        PasteKindFilter.Images => PasteKindFilter.Files,
        _ => PasteKindFilter.All,
    };

    /// <summary>Whether a clip survives the filter.</summary>
    public static bool Admits(this PasteKindFilter filter, ClipKind kind) => filter switch
    {
        PasteKindFilter.Text => kind == ClipKind.Text,
        PasteKindFilter.Images => kind == ClipKind.Image,
        PasteKindFilter.Files => kind == ClipKind.Files,

        // All, and anything unrecognised: show it. Erring towards showing a clip is the safe direction - a filter
        // that hid something would read as the clip having been lost. Note ClipKind.Other therefore has no filter
        // of its own and appears only under All, which is deliberate: a "binary" filter would be a menu entry for
        // clips nobody deliberately keeps.
        _ => true,
    };

    /// <summary>How the overlay names it. Null for <see cref="PasteKindFilter.All"/>, which needs no chip.</summary>
    public static string? Describe(this PasteKindFilter filter) => filter switch
    {
        PasteKindFilter.Text => "text only",
        PasteKindFilter.Images => "images only",
        PasteKindFilter.Files => "files only",
        _ => null,
    };
}
