namespace PasteJump.Core.Model;

/// <summary>
/// A clip as stored. Note what is <em>not</em> here: any notion of position-as-identity.
/// <para>
/// In the original, a clip's identity <em>was</em> its array index - the file
/// <c>cache\clips\7.avc</c> was clip 7. Deleting or repositioning anything therefore
/// cascaded file renames (<c>renameCorrect</c>, <c>compacter</c>, and
/// <c>manageFIXATE</c> which performs three FileMove calls per pinned clip to bubble it
/// up). Here <see cref="Id"/> is immutable and ordering lives in <see cref="SortKey"/>,
/// so repositioning is a single UPDATE and nothing on disk moves.
/// </para>
/// </summary>
public sealed class Clip
{
    public required long Id { get; init; }

    /// <summary>
    /// Fractional ordering key. Higher sorts earlier (newest first). Inserting between two
    /// neighbours means picking a value between their keys - no renumbering of siblings.
    /// </summary>
    public required double SortKey { get; init; }

    /// <summary>Pinned ("fixated") clips sort ahead of everything else and survive eviction.</summary>
    public required bool Pinned { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Truncated text for the overlay and for search. Never the full payload.</summary>
    public required string Preview { get; init; }

    public required ClipKind Kind { get; init; }

    public string? SourceExecutable { get; init; }

    public required long TotalBytes { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Content hash, used for duplicate suppression on capture.</summary>
    public required string ContentHash { get; init; }

    public bool HasTags => Tags.Count > 0;
}
