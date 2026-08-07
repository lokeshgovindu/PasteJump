using System.Globalization;
using System.Text;

namespace Clipjog.Core.Formatting;

/// <summary>Pastes the clip exactly as captured, with every format intact.</summary>
public sealed class OriginalFormatter : IClipFormatter
{
    /// <summary>
    /// Exposed as a constant so settings can store this id explicitly rather than relying on null to
    /// mean "the default". Two spellings of one value is how <c>DefaultFormatterId</c> came to report
    /// itself as modified when nothing had changed.
    /// </summary>
    public const string FormatterId = "original";

    public string Id => FormatterId;

    public string DisplayName => "Original";

    public bool TextOnlyOutput => false;

    public string Apply(string text) => text;
}

/// <summary>
/// Pastes the text with all rich formatting discarded - the most-used transform in practice,
/// and the equivalent of <c>pformat.noformatting.ahk</c>.
/// </summary>
public sealed class PlainTextFormatter : IClipFormatter
{
    public string Id => "plain";

    public string DisplayName => "Plain text";

    public bool TextOnlyOutput => true;

    public string Apply(string text) => text;
}

/// <summary>
/// Collapses runs of whitespace to single spaces and trims the ends. Useful for text copied out
/// of a PDF or a wrapped terminal, where line breaks are an artefact of the source layout.
/// </summary>
public sealed class CollapseWhitespaceFormatter : IClipFormatter
{
    public string Id => "collapse-whitespace";

    public string DisplayName => "Collapse whitespace";

    public bool TextOnlyOutput => true;

    public string Apply(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Sentence-cases the text: first letter of each sentence upper, the remainder lower.
/// Behavioural equivalent of <c>pformat.sentencecase.ahk</c>, written from scratch.
/// </summary>
public sealed class SentenceCaseFormatter : IClipFormatter
{
    public string Id => "sentence-case";

    public string DisplayName => "Sentence case";

    public bool TextOnlyOutput => true;

    public string Apply(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var culture = CultureInfo.CurrentCulture;
        var builder = new StringBuilder(text.Length);
        var atSentenceStart = true;

        foreach (var ch in text)
        {
            if (atSentenceStart && char.IsLetter(ch))
            {
                builder.Append(char.ToUpper(ch, culture));
                atSentenceStart = false;
                continue;
            }

            builder.Append(char.ToLower(ch, culture));

            if (ch is '.' or '!' or '?' or '\n')
            {
                atSentenceStart = true;
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// Removes the common leading indentation from every line, leaving relative indentation intact.
/// For pasting a block lifted out of nested code into a different nesting level.
/// </summary>
public sealed class UnindentFormatter : IClipFormatter
{
    public string Id => "unindent";

    public string DisplayName => "Unindent";

    public bool TextOnlyOutput => true;

    public string Apply(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // ReplaceLineEndings normalises CRLF/CR/LF so the split below cannot leave stray \r
        // clinging to the end of every line.
        var normalised = text.ReplaceLineEndings("\n");
        var lines = normalised.Split('\n');

        var commonIndent = int.MaxValue;

        foreach (var line in lines)
        {
            if (line.Length == 0 || line.All(char.IsWhiteSpace))
            {
                continue;
            }

            var indent = 0;

            while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
            {
                indent++;
            }

            commonIndent = Math.Min(commonIndent, indent);

            if (commonIndent == 0)
            {
                return normalised;
            }
        }

        if (commonIndent is 0 or int.MaxValue)
        {
            return normalised;
        }

        var builder = new StringBuilder(normalised.Length);

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            var line = lines[i];
            builder.Append(line.Length >= commonIndent ? line[commonIndent..] : line.TrimStart());
        }

        return builder.ToString();
    }
}
