namespace PasteJump.Core.Model;

/// <summary>
/// A long-term history record. Distinct from <see cref="Clip"/> on purpose: clips are the
/// working stack and get evicted once the configured ceiling is reached, whereas history is
/// the searchable archive kept for a configured number of days.
/// </summary>
public sealed class HistoryEntry
{
    public required long Id { get; init; }

    public required DateTimeOffset CapturedUtc { get; init; }

    public required ClipKind Kind { get; init; }

    public required string Preview { get; init; }

    /// <summary>Hash of the externalised blob for image/binary entries, otherwise null.</summary>
    public string? BlobHash { get; init; }

    public required long TotalBytes { get; init; }

    /// <summary>
    /// Provenance marker. Rows migrated from Clipjump 12.5 carry <c>clipjump-12.5</c> so a
    /// bad import can be identified and rolled back without touching native rows.
    /// </summary>
    public string? ImportedFrom { get; init; }
}
