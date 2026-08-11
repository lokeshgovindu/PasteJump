using System.Globalization;

namespace PasteJump.Core.PasteMode;

/// <summary>
/// Counts lines and characters for the overlay's facts line, so a text clip says as much about itself as an image
/// already did.
/// <para>
/// In <c>Core</c> because it is arithmetic with several edge cases worth pinning down, and because getting a line
/// count wrong is the sort of thing nobody notices until the number is obviously silly.
/// </para>
/// </summary>
public static class TextMetrics
{
    /// <summary>
    /// Lines and characters, as the overlay shows them - <c>12 lines · 843 chars</c>.
    /// </summary>
    /// <param name="text">The clip's stored preview, which may itself be shorter than what was copied.</param>
    /// <param name="truncated">
    /// Whether <paramref name="text"/> is known to be cut short. When it is, both numbers gain a <c>+</c>: the
    /// alternative is stating a count that is simply wrong, which is worse than admitting the limit. A clip's
    /// stored preview is capped at <c>PreviewMaxChars</c>, so this is the ordinary case for anything long.
    /// </param>
    public static string Describe(string? text, bool truncated = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "empty";
        }

        var lines = CountLines(text);
        var suffix = truncated ? "+" : string.Empty;

        return string.Format(
            CultureInfo.CurrentCulture,
            "{0:N0}{1} line{2} · {3:N0}{1} char{4}",
            lines,
            suffix,
            lines == 1 && !truncated ? string.Empty : "s",
            text.Length,
            text.Length == 1 && !truncated ? string.Empty : "s");
    }

    /// <summary>
    /// Lines in the way a person counts them: the number of rows the text occupies.
    /// <para>
    /// One separator ends one line, so "a\nb" is two. A trailing newline does <em>not</em> start a third - a file
    /// ending in a line break has as many lines as one that does not, which is how every editor counts and the
    /// opposite of what splitting on the separator gives you.
    /// </para>
    /// <para>
    /// <c>\r\n</c> counts once, and a lone <c>\r</c> counts too: clipboard text arrives from anywhere, including
    /// old Mac-style sources and anything that has been through a badly-written converter.
    /// </para>
    /// </summary>
    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var lines = 1;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
            else if (text[i] == '\r')
            {
                // Part of a \r\n pair, which the \n above will count.
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    continue;
                }

                lines++;
            }
        }

        // A single trailing break does not open a line of its own.
        if (EndsWithBreak(text))
        {
            lines--;
        }

        return Math.Max(1, lines);
    }

    private static bool EndsWithBreak(string text)
        => text.EndsWith('\n') || text.EndsWith('\r');
}
