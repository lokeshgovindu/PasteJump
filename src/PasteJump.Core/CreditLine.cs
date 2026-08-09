namespace PasteJump.Core;

/// <summary>
/// A copyright line split into the part before the author's name, the name itself, and the part after - so the
/// name can be rendered as a link and the rest as plain text.
/// <para>
/// <see cref="Author"/> is empty when the name does not appear in the line, in which case <see cref="Prefix"/>
/// holds the whole thing. Callers then render one plain run, which is the correct degradation: a link on nothing
/// would be invisible, and a link on the whole copyright line would be wrong.
/// </para>
/// </summary>
public readonly record struct CreditLine(string Prefix, string Author, string Suffix)
{
    /// <summary>Whether the author's name was found and is worth rendering as a link.</summary>
    public bool HasAuthor => Author.Length > 0;
}

/// <summary>
/// Splits a copyright line around the author's name.
/// <para>
/// In <c>Core</c> and tested rather than done inline in the About window, for the usual reason: it is string
/// handling with an off-by-one in it (the suffix offset), and a mistake shows up as a duplicated or a missing
/// word on screen rather than as a failure.
/// </para>
/// </summary>
public static class CreditLineSplitter
{
    /// <param name="copyright">The full line, e.g. <c>Copyright (c) 2026 Lokesh Govindu</c>.</param>
    /// <param name="author">The name to isolate, e.g. <c>Lokesh Govindu</c>.</param>
    public static CreditLine Split(string? copyright, string? author)
    {
        var line = copyright ?? string.Empty;

        if (string.IsNullOrWhiteSpace(author))
        {
            return new CreditLine(line, string.Empty, string.Empty);
        }

        // Ordinal: this is matching a literal that both strings were built from in the same build, so a
        // culture-sensitive comparison could only introduce surprises.
        var index = line.IndexOf(author, StringComparison.Ordinal);

        return index < 0
            ? new CreditLine(line, string.Empty, string.Empty)
            : new CreditLine(line[..index], author, line[(index + author.Length)..]);
    }
}
