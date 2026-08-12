using System.Text;

namespace PasteJump.Core.Paste;

/// <summary>What joining several clips produced, and what it had to leave out.</summary>
/// <param name="Text">The joined text. Empty when nothing could contribute.</param>
/// <param name="Joined">How many clips contributed text.</param>
/// <param name="Skipped">
/// How many were left out because they have no text - an image, in practice. Counted rather than silently
/// dropped: a selection of five rows that produces three lines needs to say so, or it reads as lost data.
/// </param>
public readonly record struct ClipJoinResult(string Text, int Joined, int Skipped);

/// <summary>
/// Joins the text of several clips into one, for pasting as a single block.
/// <para>
/// Distinct from <c>Enter</c> during the gesture, which pastes clips one after another as separate pastes -
/// that leaves whatever the target application does between them, and in a spreadsheet it means separate
/// cells. This produces <em>one</em> clip, so it lands as one paste.
/// </para>
/// <para>
/// Text only, and that is a limit rather than an oversight: two images cannot be concatenated into one image
/// without deciding on a layout, and no answer to that would be right often enough to guess. A file list does
/// join, because its text is its paths - which is one of the more useful cases, since copying three paths and
/// pasting them together is otherwise three round trips.
/// </para>
/// </summary>
public static class ClipJoiner
{
    /// <summary>
    /// What a separator setting holds when it has never been changed: one line break. Stored escaped, because
    /// a settings file holding a literal newline inside a JSON string is legal, unreadable and easy to mangle
    /// by hand.
    /// </summary>
    public const string DefaultSeparator = @"\n";

    /// <summary>
    /// Turns the stored form into the characters to put between clips.
    /// <para>
    /// Escapes are <c>\n</c>, <c>\r</c>, <c>\t</c> and <c>\\</c>. Anything else is taken literally, including a
    /// backslash that begins no escape - <c>\d</c> stays <c>\d</c> rather than becoming <c>d</c>, because a
    /// separator is arbitrary text and quietly eating a character the user typed is worse than passing it on.
    /// </para>
    /// <para>
    /// An empty setting means the default rather than "no separator at all". Joining with nothing runs clips
    /// together into one unreadable string, and it is what an accidentally cleared text box would produce -
    /// so it is not reachable by accident. It is still reachable on purpose, with <c>\\</c> removed... which it
    /// is not: there is deliberately no way to express "no separator", for the same reason.
    /// </para>
    /// </summary>
    public static string ParseSeparator(string? setting)
    {
        if (string.IsNullOrEmpty(setting))
        {
            return "\n";
        }

        var text = new StringBuilder(setting.Length);

        for (var i = 0; i < setting.Length; i++)
        {
            if (setting[i] != '\\' || i + 1 >= setting.Length)
            {
                text.Append(setting[i]);
                continue;
            }

            switch (setting[i + 1])
            {
                case 'n': text.Append('\n'); i++; break;
                case 'r': text.Append('\r'); i++; break;
                case 't': text.Append('\t'); i++; break;
                case '\\': text.Append('\\'); i++; break;
                default: text.Append(setting[i]); break;
            }
        }

        return text.Length == 0 ? "\n" : text.ToString();
    }

    /// <summary>
    /// Names a separator for a status line or a settings hint, so the app can say how it joined rather than
    /// leaving the user to infer it from the result.
    /// </summary>
    public static string Describe(string separator) => separator switch
    {
        "\n" or "\r\n" => "a new line",
        " " => "a space",
        "\t" => "a tab",
        ", " => "a comma and a space",
        "," => "a comma",
        "; " => "a semicolon and a space",
        _ => $"\"{separator.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t")}\"",
    };

    /// <summary>
    /// Joins in the order given, which the caller must have put in the order the user sees. Entries with no
    /// text are skipped and counted.
    /// </summary>
    /// <param name="texts">
    /// One entry per selected clip: its text, or null for a clip that has none. Nulls rather than a filtered
    /// list, so the count of what was left out is available without the caller tracking it separately.
    /// </param>
    public static ClipJoinResult Join(IEnumerable<string?> texts, string separator)
    {
        var joinable = new List<string>();
        var skipped = 0;

        foreach (var text in texts)
        {
            // Whitespace-only counts as text. It is odd to join, but it is something the user copied on
            // purpose, and dropping it would silently change a deliberate blank line into nothing.
            if (text is null)
            {
                skipped++;
                continue;
            }

            joinable.Add(text);
        }

        return new ClipJoinResult(string.Join(separator, joinable), joinable.Count, skipped);
    }
}
