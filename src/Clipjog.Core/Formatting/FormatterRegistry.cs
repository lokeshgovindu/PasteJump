namespace Clipjog.Core.Formatting;

/// <summary>
/// The ordered set of formatters the <c>Z</c> key cycles through.
/// <para>
/// <see cref="OriginalFormatter"/> is always first and always present, so cycling is guaranteed
/// to return to a known-safe state. The original had a subtle version of this bug: its format
/// list came from whatever plugin files happened to be on disk, so deleting a plugin could leave
/// a persisted default pointing at nothing.
/// </para>
/// </summary>
public sealed class FormatterRegistry
{
    /// <summary>
    /// Id of the formatter used when nothing else is chosen. The canonical value to persist - see
    /// <see cref="OriginalFormatter.FormatterId"/>.
    /// </summary>
    public const string DefaultId = OriginalFormatter.FormatterId;

    private readonly List<IClipFormatter> _formatters;

    public FormatterRegistry(IEnumerable<IClipFormatter>? additional = null)
    {
        _formatters =
        [
            new OriginalFormatter(),
            new PlainTextFormatter(),
            new CollapseWhitespaceFormatter(),
            new SentenceCaseFormatter(),
            new UnindentFormatter(),
        ];

        if (additional is not null)
        {
            foreach (var formatter in additional)
            {
                if (!_formatters.Any(f => string.Equals(f.Id, formatter.Id, StringComparison.Ordinal)))
                {
                    _formatters.Add(formatter);
                }
            }
        }
    }

    public IReadOnlyList<IClipFormatter> All => _formatters;

    public IClipFormatter Default => _formatters[0];

    /// <summary>Resolves by id, falling back to <see cref="Default"/> for unknown or missing ids.</summary>
    public IClipFormatter Resolve(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return Default;
        }

        return _formatters.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? Default;
    }

    /// <summary>The next formatter in cycle order, wrapping back to <see cref="Default"/>.</summary>
    public IClipFormatter Next(IClipFormatter current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var index = _formatters.FindIndex(f => string.Equals(f.Id, current.Id, StringComparison.Ordinal));

        if (index < 0)
        {
            return Default;
        }

        return _formatters[(index + 1) % _formatters.Count];
    }
}
