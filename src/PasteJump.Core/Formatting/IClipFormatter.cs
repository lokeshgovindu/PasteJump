namespace PasteJump.Core.Formatting;

/// <summary>
/// A paste-time text transform, cycled with the <c>Z</c> key in paste mode. Replaces the
/// original's <c>pformat.*</c> plugin family.
/// </summary>
public interface IClipFormatter
{
    /// <summary>Stable identifier, persisted in settings. Never localise this.</summary>
    string Id { get; }

    /// <summary>Label shown in the overlay and the settings dropdown.</summary>
    string DisplayName { get; }

    /// <summary>
    /// True when the result should be pasted as plain text only, discarding every other
    /// clipboard format.
    /// <para>
    /// This is a separate concept from the transform itself, and it has to be: rewriting the text
    /// while leaving the original HTML and RTF formats in place would paste the <em>untransformed</em>
    /// content into any app that prefers a richer format - which is most of them. A formatter that
    /// changes the text must therefore also narrow the output.
    /// </para>
    /// </summary>
    bool TextOnlyOutput { get; }

    /// <summary>Transforms the clip's text.</summary>
    string Apply(string text);
}
