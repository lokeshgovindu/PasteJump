namespace PasteJump.Core.PasteMode;

/// <summary>
/// Which parts of the paste overlay are drawn.
/// <para>
/// The overlay accumulated a lot of true things to say about a clip - where you are in the stack, its tags, which
/// application it came from, how many lines it has, how many bytes it occupies - and not everybody wants all of
/// them a foot from the caret every time they paste. This is the switchboard.
/// </para>
/// <para>
/// <b>Only the cosmetic parts are here, and that line is deliberate.</b> Anything that changes what releasing
/// <c>Ctrl</c> will do stays on screen whatever this says: the <c>POP</c> chip, the <c>JOIN</c> count, the kind
/// filter, and the Cancel / Delete / Delete All banner. A user who hides those has not tidied the overlay, they have
/// armed a deletion they cannot see - which is the one failure this overlay exists to prevent. The preview itself is
/// not optional either; an overlay that shows nothing about the clip is not a quieter overlay, it is a broken one.
/// </para>
/// </summary>
/// <param name="Position">The <c>Clip 3 of 41</c> line at the top left.</param>
/// <param name="Details">
/// The facts on the left of the row under the preview: lines and characters for text, pixel dimensions for an image,
/// the line count for a copied text file.
/// </param>
/// <param name="Size">The byte count on the right of that same row.</param>
/// <param name="Tags">The <c>#tag</c> chip.</param>
/// <param name="Source">The chip naming the application the clip was copied from.</param>
/// <param name="Formatter">The chip naming the paste format - Original, Plain text, and so on.</param>
/// <param name="Pinned">The <c>PINNED</c> chip.</param>
/// <param name="KeyHint">The row of key reminders along the bottom.</param>
/// <remarks>
/// A record <b>class</b>, not a record struct, and that is not a style choice. With a struct, <c>new OverlayParts()</c>
/// zero-initialises and <em>ignores</em> the default parameter values above - so <c>All</c> came out with every flag
/// <c>false</c> and a fresh install would have rendered an overlay with nothing on it. Two tests caught it
/// immediately. As a class the defaults genuinely apply, and there is no <c>default</c> value quietly meaning
/// "hide everything".
/// </remarks>
public sealed record OverlayParts(
    bool Position = true,
    bool Details = true,
    bool Size = true,
    bool Tags = true,
    bool Source = true,
    bool Formatter = true,
    bool Pinned = true,
    bool KeyHint = true)
{
    /// <summary>Everything on, which is what a fresh install gets and what every existing install had.</summary>
    public static OverlayParts All { get; } = new();

    /// <summary>
    /// Nothing but the preview and the state that changes what a release does. Not a setting in itself - it exists
    /// so tests and the smoke harness can render the quietest overlay the settings allow.
    /// </summary>
    public static OverlayParts Minimal { get; } = new(
        Position: false,
        Details: false,
        Size: false,
        Tags: false,
        Source: false,
        Formatter: false,
        Pinned: false,
        KeyHint: false);
}
