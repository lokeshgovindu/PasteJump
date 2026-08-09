using System.Globalization;
using System.Text;
using PasteJump.Core.Abstractions;
using PasteJump.Core.Model;
using Microsoft.Data.Sqlite;

namespace PasteJump.Core.Storage;

/// <summary>
/// Persistence for clips, tags and history.
/// <para>
/// One connection, held open, guarded by a lock. This is a single-user tray utility - the
/// contention ceiling is "the History window is open while you copy something", which WAL
/// handles - so a connection pool would be complexity with no payoff.
/// </para>
/// </summary>
public sealed class ClipStore : IDisposable
{
    /// <summary>
    /// Default cap on preview text: the overlay shows a fraction of this and search works on prefixes. The live
    /// value is <see cref="PreviewMaxChars"/>, which the app sets from settings.
    /// </summary>
    public const int DefaultPreviewMaxChars = 4096;

    /// <summary>
    /// Characters of text kept in the <c>preview</c> column, from
    /// <see cref="Settings.PasteJumpSettings.PreviewMaxChars"/>.
    /// <para>
    /// Mutable rather than a constructor argument because the store is opened before settings are read - the
    /// settings file's own location can depend on a pointer read even earlier - and because Apply in the settings
    /// dialog has to be able to change it without reopening the database.
    /// </para>
    /// </summary>
    public int PreviewMaxChars { get; set; } = DefaultPreviewMaxChars;

    private const double SortKeyStep = 1.0;

    private readonly IClock _clock;
    private readonly BlobStore _blobs;
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private bool _disposed;

    public ClipStore(AppPaths paths, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        paths.EnsureCreated();
        _clock = clock ?? SystemClock.Instance;
        _blobs = new BlobStore(paths.BlobsDirectory);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        Execute(PasteJumpSchema.Pragmas);
        Execute(PasteJumpSchema.Ddl);
        SetMeta("schema_version", PasteJumpSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture));
    }

    public BlobStore Blobs => _blobs;

    // ---------------------------------------------------------------- clips

    /// <summary>
    /// Stores a captured snapshot.
    /// <para>
    /// When <paramref name="allowDuplicates"/> is false and identical content is already
    /// present, the existing clip is promoted to the front rather than duplicated. That mirrors
    /// the original's <c>is_duplicate_copied</c> setting, and is what people actually want:
    /// re-copying something should surface it, not litter the stack.
    /// </para>
    /// </summary>
    public Clip Add(ClipboardSnapshot snapshot, bool allowDuplicates = false)
        => Add(snapshot, allowDuplicates, out _);

    /// <summary>
    /// As <see cref="Add(ClipboardSnapshot, bool)"/>, additionally reporting whether a new row was
    /// inserted or an existing identical clip was promoted.
    /// <para>
    /// Callers need this to avoid double-counting. A single logical copy can raise more than one
    /// <c>WM_CLIPBOARDUPDATE</c> with <em>different</em> clipboard sequence numbers - anything
    /// using OLE does <c>OleSetClipboard</c> then <c>OleFlushClipboard</c>, which is two real
    /// changes carrying identical content. The clip stack absorbs that through hash matching, but
    /// an append-only log like history would otherwise record the same capture twice.
    /// </para>
    /// </summary>
    public Clip Add(ClipboardSnapshot snapshot, bool allowDuplicates, out bool wasNewCapture)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            if (!allowDuplicates)
            {
                var existingId = FindIdByHash(snapshot.ContentHash);

                if (existingId is { } id)
                {
                    MoveToFrontCore(id);
                    wasNewCapture = false;
                    return GetByIdCore(id)!;
                }
            }

            wasNewCapture = true;

            using var tx = _connection.BeginTransaction();

            var sortKey = NextSortKeyCore();
            var createdUtc = _clock.UtcNow;
            var preview = BuildPreview(snapshot);

            long clipId;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO clip (sort_key, pinned, created_utc, preview, kind, source_exe, total_bytes, content_hash)
                    VALUES ($sort, 0, $created, $preview, $kind, $exe, $bytes, $hash);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$sort", sortKey);
                cmd.Parameters.AddWithValue("$created", ToDb(createdUtc));
                cmd.Parameters.AddWithValue("$preview", preview);
                cmd.Parameters.AddWithValue("$kind", (int)snapshot.Kind);
                cmd.Parameters.AddWithValue("$exe", (object?)snapshot.SourceExecutable ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$bytes", snapshot.TotalBytes);
                cmd.Parameters.AddWithValue("$hash", snapshot.ContentHash);

                clipId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            foreach (var payload in snapshot.Payloads)
            {
                InsertPayload(tx, clipId, payload);
            }

            tx.Commit();

            return new Clip
            {
                Id = clipId,
                SortKey = sortKey,
                Pinned = false,
                CreatedUtc = createdUtc,
                Preview = preview,
                Kind = snapshot.Kind,
                SourceExecutable = snapshot.SourceExecutable,
                TotalBytes = snapshot.TotalBytes,
                ContentHash = snapshot.ContentHash,
                Tags = [],
            };
        }
    }

    /// <summary>
    /// Id of the most recently added clip, ignoring pinning, or null when the stack is empty.
    /// <para>
    /// Used by consecutive-duplicate suppression to check that the clip it is suppressing against is
    /// still there. Without that check, deleting a clip and re-copying the same text would be
    /// silently swallowed.
    /// </para>
    /// </summary>
    public long? NewestClipId()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id FROM clip ORDER BY sort_key DESC LIMIT 1;";

            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Clips in display order: pinned first, then newest first.</summary>
    public IReadOnlyList<Clip> GetOrdered(int limit = int.MaxValue)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, sort_key, pinned, created_utc, preview, kind, source_exe, total_bytes, content_hash
                FROM clip
                ORDER BY pinned DESC, sort_key DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit == int.MaxValue ? -1 : limit);

            var clips = new List<Clip>();
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                clips.Add(ReadClip(reader));
            }

            AttachTagsCore(clips);
            return clips;
        }
    }

    public Clip? GetById(long id)
    {
        lock (_gate)
        {
            return GetByIdCore(id);
        }
    }

    /// <summary>Rehydrates every clipboard format for a clip, ready to be written back.</summary>
    public IReadOnlyList<ClipPayload> GetPayloads(long clipId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT format_id, format_name, data, blob_hash, byte_len
                FROM clip_format
                WHERE clip_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", clipId);

            var payloads = new List<ClipPayload>();
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var formatId = (uint)reader.GetInt64(0);
                var formatName = reader.IsDBNull(1) ? null : reader.GetString(1);
                byte[]? data;

                if (!reader.IsDBNull(2))
                {
                    data = (byte[])reader[2];
                }
                else if (!reader.IsDBNull(3))
                {
                    data = _blobs.TryRead(reader.GetString(3));
                }
                else
                {
                    data = null;
                }

                // A missing blob means the file was removed underneath us. Skipping the format is
                // right: writing a zero-length payload would claim we have data we do not.
                if (data is not null)
                {
                    payloads.Add(new ClipPayload(formatId, formatName, data));
                }
            }

            return payloads;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM clip;";
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }

    public void Delete(long id)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM clip WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Clears the clip stack. Pinned clips are kept by default - the whole point of pinning is
    /// that a bulk delete does not take them.
    /// </summary>
    public void DeleteAll(bool includePinned = false)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = includePinned
                ? "DELETE FROM clip;"
                : "DELETE FROM clip WHERE pinned = 0;";
            cmd.ExecuteNonQuery();
        }
    }

    public void SetPinned(long id, bool pinned)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE clip SET pinned = $pinned WHERE id = $id;";
            cmd.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Moves a clip to the front of the stack - the <c>Q</c> key in paste mode.
    /// <para>
    /// One UPDATE. The equivalent in the original (<c>manageFIXATE</c>, Clipjump.ahk:820) is a
    /// loop of three FileMove calls per affected clip, plus parallel juggling of the thumbnail
    /// files and the in-memory mirror arrays.
    /// </para>
    /// </summary>
    public void MoveToFront(long id)
    {
        lock (_gate)
        {
            MoveToFrontCore(id);
        }
    }

    /// <summary>
    /// Deletes clips whose every format is OLE bookkeeping, and returns how many went.
    /// <para>
    /// The corollary of <see cref="Model.BookkeepingFormats"/>: that stops new ones being stored, but a store
    /// built before it holds however many were already captured - 134 in the store that surfaced this - and
    /// they are not inert. Because they all carry the identical eight bytes they hash alike, so every OLE copy
    /// promoted one of them to the front of the stack, meaning the newest clip after taking a screenshot was
    /// an ancient 8-byte blob. Deleting them is what actually clears the reported symptom.
    /// </para>
    /// <para>
    /// Pinned clips are left alone regardless. Nothing about pinning implies the content is useful, and
    /// silently deleting something the user deliberately kept is worse than leaving one odd entry.
    /// </para>
    /// </summary>
    public int PurgeContentlessClips()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();

            // Names come from BookkeepingFormats so the rule is defined once, and are parameterised rather
            // than interpolated - they are internal constants today, but building SQL by concatenation is a
            // habit that eventually meets a value that is not.
            var names = Model.BookkeepingFormats.RegisteredNames;
            var placeholders = string.Join(", ", names.Select((_, i) => $"$n{i}"));

            for (var i = 0; i < names.Count; i++)
            {
                cmd.Parameters.AddWithValue($"$n{i}", names[i].ToLowerInvariant());
            }

            cmd.Parameters.AddWithValue("$locale", Model.BookkeepingFormats.CfLocale);

            // "No format that is not bookkeeping" rather than "every format is bookkeeping", because the
            // latter is not expressible over rows without a grouping. A clip with no formats at all cannot
            // occur - Add always writes at least one - so the double negative has no empty-set trap here.
            cmd.CommandText = $"""
                DELETE FROM clip
                WHERE pinned = 0
                  AND NOT EXISTS (
                      SELECT 1 FROM clip_format f
                      WHERE f.clip_id = clip.id
                        AND NOT (
                            (f.format_name IS NOT NULL AND LOWER(f.format_name) IN ({placeholders}))
                            OR (f.format_name IS NULL AND f.format_id = $locale)
                        )
                  );
                """;

            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Trims the stack to <paramref name="maxClips"/>, oldest unpinned first. Returns how many went.
    /// </summary>
    /// <summary>
    /// Removes clips whose content duplicates another, keeping the newest of each and preferring a pinned one.
    /// Returns how many were deleted.
    /// <para>
    /// The counterpart to <see cref="DeduplicateHistory"/>, and needed for the same reason: the clip half of the
    /// Clipjump import passed <c>allowDuplicates: true</c>, so importing twice made a second copy of every clip.
    /// Content hash is the whole key here - it is what "the same clip" means everywhere else in this class, so
    /// nothing distinguishable is lost.
    /// </para>
    /// <para>
    /// Newest wins rather than oldest, unlike history. A history entry is a record of when something was copied,
    /// so the first occurrence is the true one; a clip is a thing to paste, and its position in the stack is
    /// what the user navigates by - keeping the older row would send a duplicate to the back of the stack.
    /// Pinned beats unpinned regardless, since discarding the pin would lose a deliberate act.
    /// </para>
    /// </summary>
    public int DeduplicateClips()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();

            // MAX over (pinned, sort_key) is not expressible directly, so the survivor is chosen by ordering
            // within each hash group: pinned first, then newest.
            cmd.CommandText = """
                DELETE FROM clip
                WHERE id NOT IN (
                    SELECT id FROM (
                        SELECT id, ROW_NUMBER() OVER (
                            PARTITION BY content_hash
                            ORDER BY pinned DESC, sort_key DESC
                        ) AS rank_in_group
                        FROM clip
                    )
                    WHERE rank_in_group = 1
                );
                """;

            // clip_format and clip_tag cascade on delete, so the payload rows go with them. The blobs they
            // referenced are left to CollectGarbage, which is where every other delete path leaves them too.
            return cmd.ExecuteNonQuery();
        }
    }

    public int EvictBeyond(int maxClips)
    {
        if (maxClips <= 0)
        {
            return 0;
        }

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM clip
                WHERE id IN (
                    SELECT id FROM clip
                    WHERE pinned = 0
                    ORDER BY sort_key DESC
                    LIMIT -1 OFFSET $keep
                );
                """;
            cmd.Parameters.AddWithValue("$keep", maxClips);
            return cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- tags

    public void SetTags(long clipId, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var normalised = tags
            .Select(static t => t.Trim())
            .Where(static t => t.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();

            using (var clear = _connection.CreateCommand())
            {
                clear.Transaction = tx;
                clear.CommandText = "DELETE FROM clip_tag WHERE clip_id = $id;";
                clear.Parameters.AddWithValue("$id", clipId);
                clear.ExecuteNonQuery();
            }

            foreach (var tag in normalised)
            {
                long tagId;

                using (var upsert = _connection.CreateCommand())
                {
                    upsert.Transaction = tx;
                    upsert.CommandText = """
                        INSERT INTO tag (name) VALUES ($name)
                        ON CONFLICT(name) DO UPDATE SET name = excluded.name;
                        SELECT id FROM tag WHERE name = $name;
                        """;
                    upsert.Parameters.AddWithValue("$name", tag);
                    tagId = Convert.ToInt64(upsert.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                using var link = _connection.CreateCommand();
                link.Transaction = tx;
                link.CommandText = "INSERT OR IGNORE INTO clip_tag (clip_id, tag_id) VALUES ($clip, $tag);";
                link.Parameters.AddWithValue("$clip", clipId);
                link.Parameters.AddWithValue("$tag", tagId);
                link.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public IReadOnlyList<string> GetTags(long clipId)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT t.name FROM tag t
                JOIN clip_tag ct ON ct.tag_id = t.id
                WHERE ct.clip_id = $id
                ORDER BY t.name;
                """;
            cmd.Parameters.AddWithValue("$id", clipId);

            var tags = new List<string>();
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                tags.Add(reader.GetString(0));
            }

            return tags;
        }
    }

    // ---------------------------------------------------------------- history

    public long AddHistory(
        DateTimeOffset capturedUtc,
        ClipKind kind,
        string preview,
        byte[]? blob,
        long totalBytes,
        string? importedFrom = null)
    {
        ArgumentNullException.ThrowIfNull(preview);

        string? blobHash = null;

        if (blob is { Length: > 0 })
        {
            blobHash = _blobs.Write(blob);
        }

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO history (captured_utc, kind, preview, blob_hash, total_bytes, imported_from)
                VALUES ($time, $kind, $preview, $hash, $bytes, $from);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$time", ToDb(capturedUtc));
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            cmd.Parameters.AddWithValue("$preview", Truncate(preview, PreviewMaxChars));
            cmd.Parameters.AddWithValue("$hash", (object?)blobHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bytes", totalBytes);
            cmd.Parameters.AddWithValue("$from", (object?)importedFrom ?? DBNull.Value);

            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Adds a history entry unless an identical one is already there, returning its new id or null when it was
    /// skipped. This is what makes re-importing safe.
    /// <para>
    /// The natural key is captured time, kind, preview <em>and</em> blob hash. The hash is part of it on purpose:
    /// every image row previews as <c>[image]</c>, so two different screenshots taken in the same second are
    /// indistinguishable without it and a dedupe on the first three columns alone would throw one of them away.
    /// A re-import cannot be fooled by that, because blobs are content-addressed - the same picture always
    /// hashes the same.
    /// </para>
    /// <para>
    /// The hash is computed rather than written when the row turns out to be a duplicate, so a skipped row does
    /// not leave an orphan blob behind for <c>CollectGarbage</c> to find later.
    /// </para>
    /// </summary>
    public long? AddHistoryIfAbsent(
        DateTimeOffset capturedUtc,
        ClipKind kind,
        string preview,
        byte[]? blob,
        long totalBytes,
        string? importedFrom = null)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var blobHash = blob is { Length: > 0 } ? BlobStore.ComputeHash(blob) : null;
        var truncated = Truncate(preview, PreviewMaxChars);

        lock (_gate)
        {
            using var check = _connection.CreateCommand();

            // IS rather than =, which is SQLite's null-safe comparison. With = a text row's NULL blob_hash
            // would never match itself, so nothing textual would ever be recognised as a duplicate.
            check.CommandText = """
                SELECT 1 FROM history
                WHERE captured_utc = $time AND kind = $kind AND preview = $preview AND blob_hash IS $hash
                LIMIT 1;
                """;
            check.Parameters.AddWithValue("$time", ToDb(capturedUtc));
            check.Parameters.AddWithValue("$kind", (int)kind);
            check.Parameters.AddWithValue("$preview", truncated);
            check.Parameters.AddWithValue("$hash", (object?)blobHash ?? DBNull.Value);

            if (check.ExecuteScalar() is not null)
            {
                return null;
            }
        }

        // Outside the lock, and via AddHistory so there is one insert statement in this class rather than two
        // that could drift. The gap between the check and the insert is not a race worth guarding: every write
        // goes through this instance, and an import is the only caller.
        return AddHistory(capturedUtc, kind, preview, blob, totalBytes, importedFrom);
    }

    /// <summary>
    /// Removes history entries that duplicate an earlier one exactly, keeping the oldest row of each group.
    /// Returns how many were deleted.
    /// <para>
    /// Needed because imports were not idempotent before <see cref="AddHistoryIfAbsent"/> existed: the dialog
    /// said entries already imported were skipped, and nothing checked - so a history imported four times held
    /// four of everything. Same natural key as the insert-time check, for the same reason.
    /// </para>
    /// <para>
    /// The FTS index follows automatically: <c>history_fts</c> is external-content with an AFTER DELETE trigger,
    /// so deleting here removes the index entry too. Doing this with raw SQL rather than row by row matters at
    /// the size that provokes it - tens of thousands of rows.
    /// </para>
    /// </summary>
    public int DeduplicateHistory()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();

            // MIN(id) keeps the earliest row of each group, so ids stay stable for whatever was imported first
            // rather than the survivor changing on every run.
            cmd.CommandText = """
                DELETE FROM history
                WHERE id NOT IN (
                    SELECT MIN(id) FROM history
                    GROUP BY captured_utc, kind, preview, blob_hash
                );
                """;

            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Default cap on a history query. See <see cref="SearchHistory"/> for why it is this high.</summary>
    public const int DefaultHistoryLimit = 50_000;

    /// <summary>
    /// Searches history. An empty term returns the most recent entries; otherwise this is an
    /// FTS5 prefix-AND match, which is what the original's "partial" checkbox meant.
    /// <para>
    /// The cap used to be 500, which was low enough to be a bug rather than a safeguard: importing a Clipjump
    /// history of 11,000 entries produced a window that could only ever show the newest 500 of them, and the
    /// status line reporting "500 of 11,108" was easy to read as a failed import. 50,000 is a backstop against
    /// a pathological store rather than a display decision - the grid virtualises rows, so the cost of a large
    /// result is the row objects alone.
    /// </para>
    /// </summary>
    public IReadOnlyList<HistoryEntry> SearchHistory(string? term, int limit = DefaultHistoryLimit)
    {
        var fts = BuildFtsQuery(term);

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();

            if (fts is null)
            {
                cmd.CommandText = """
                    SELECT id, captured_utc, kind, preview, blob_hash, total_bytes, imported_from
                    FROM history
                    ORDER BY captured_utc DESC
                    LIMIT $limit;
                    """;
            }
            else
            {
                cmd.CommandText = """
                    SELECT h.id, h.captured_utc, h.kind, h.preview, h.blob_hash, h.total_bytes, h.imported_from
                    FROM history h
                    JOIN history_fts f ON f.rowid = h.id
                    WHERE history_fts MATCH $q
                    ORDER BY h.captured_utc DESC
                    LIMIT $limit;
                    """;
                cmd.Parameters.AddWithValue("$q", fts);
            }

            cmd.Parameters.AddWithValue("$limit", limit);

            var entries = new List<HistoryEntry>();
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                entries.Add(new HistoryEntry
                {
                    Id = reader.GetInt64(0),
                    CapturedUtc = FromDb(reader.GetString(1)),
                    Kind = (ClipKind)reader.GetInt32(2),
                    Preview = reader.GetString(3),
                    BlobHash = reader.IsDBNull(4) ? null : reader.GetString(4),
                    TotalBytes = reader.GetInt64(5),
                    ImportedFrom = reader.IsDBNull(6) ? null : reader.GetString(6),
                });
            }

            return entries;
        }
    }

    public int HistoryCount
    {
        get
        {
            lock (_gate)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM history;";
                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }

    public void DeleteHistory(long id)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM history WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM history;";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Retention. <paramref name="days"/> of 0 or less means keep everything.</summary>
    public int PruneHistoryOlderThan(int days)
    {
        if (days <= 0)
        {
            return 0;
        }

        var cutoff = _clock.UtcNow.AddDays(-days);

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM history WHERE captured_utc < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", ToDb(cutoff));
            return cmd.ExecuteNonQuery();
        }
    }

    // ---------------------------------------------------------------- maintenance

    /// <summary>Drops blob files no longer referenced by any clip or history row.</summary>
    public int CollectGarbage()
    {
        HashSet<string> live;

        lock (_gate)
        {
            live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT blob_hash FROM clip_format WHERE blob_hash IS NOT NULL
                UNION
                SELECT blob_hash FROM history WHERE blob_hash IS NOT NULL;
                """;

            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                live.Add(reader.GetString(0));
            }
        }

        return _blobs.CollectGarbage(live);
    }

    /// <summary>
    /// Compresses blobs written by a version that stored them raw. Returns how many were converted.
    /// <para>
    /// Safe to call on every start: it is idempotent, bounded, and reaches zero work once the store has been
    /// converted. Nothing depends on it having run, because a raw blob still reads correctly.
    /// </para>
    /// </summary>
    public int CompactBlobs() => _blobs.CompactLegacyBlobs();

    public void Checkpoint()
    {
        lock (_gate)
        {
            Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }

    // ---------------------------------------------------------------- internals

    private void InsertPayload(SqliteTransaction tx, long clipId, ClipPayload payload)
    {
        var inline = payload.ByteLength <= BlobStore.InlineThresholdBytes;
        string? blobHash = inline ? null : _blobs.Write(payload.Data);

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO clip_format (clip_id, format_id, format_name, data, blob_hash, byte_len)
            VALUES ($clip, $fmt, $name, $data, $hash, $len);
            """;
        cmd.Parameters.AddWithValue("$clip", clipId);

        // Widened to long deliberately: SQLite has no unsigned integer type, and letting the
        // provider guess a mapping for uint invites a silent round-trip mismatch on ids above
        // int.MaxValue (registered clipboard format ids live in 0xC000-0xFFFF, but CF_* private
        // and GDIOBJ ranges go higher).
        cmd.Parameters.AddWithValue("$fmt", (long)payload.FormatId);
        cmd.Parameters.AddWithValue("$name", (object?)payload.FormatName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$data", inline ? payload.Data : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", (object?)blobHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$len", payload.ByteLength);
        cmd.ExecuteNonQuery();
    }

    private long? FindIdByHash(string contentHash)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM clip WHERE content_hash = $hash ORDER BY sort_key DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$hash", contentHash);

        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private Clip? GetByIdCore(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, sort_key, pinned, created_utc, preview, kind, source_exe, total_bytes, content_hash
            FROM clip WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = cmd.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        var clip = ReadClip(reader);
        reader.Close();

        var list = new List<Clip> { clip };
        AttachTagsCore(list);
        return list[0];
    }

    private void MoveToFrontCore(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE clip SET sort_key = $sort WHERE id = $id;";
        cmd.Parameters.AddWithValue("$sort", NextSortKeyCore());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private double NextSortKeyCore()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT IFNULL(MAX(sort_key), 0.0) FROM clip;";
        var max = Convert.ToDouble(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        return max + SortKeyStep;
    }

    /// <summary>
    /// Loads tags for a batch in one query. Done as a second pass rather than a join so the main
    /// ordered query stays a single index scan with no row fan-out.
    /// </summary>
    private void AttachTagsCore(List<Clip> clips)
    {
        if (clips.Count == 0)
        {
            return;
        }

        var ids = string.Join(',', clips.Select(static c => c.Id));

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT ct.clip_id, t.name FROM clip_tag ct
            JOIN tag t ON t.id = ct.tag_id
            WHERE ct.clip_id IN ({ids})
            ORDER BY t.name;
            """;

        var byClip = new Dictionary<long, List<string>>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var clipId = reader.GetInt64(0);

            if (!byClip.TryGetValue(clipId, out var list))
            {
                list = [];
                byClip[clipId] = list;
            }

            list.Add(reader.GetString(1));
        }

        if (byClip.Count == 0)
        {
            return;
        }

        for (var i = 0; i < clips.Count; i++)
        {
            if (byClip.TryGetValue(clips[i].Id, out var tags))
            {
                clips[i] = CloneWithTags(clips[i], tags);
            }
        }
    }

    private static Clip CloneWithTags(Clip clip, IReadOnlyList<string> tags) => new()
    {
        Id = clip.Id,
        SortKey = clip.SortKey,
        Pinned = clip.Pinned,
        CreatedUtc = clip.CreatedUtc,
        Preview = clip.Preview,
        Kind = clip.Kind,
        SourceExecutable = clip.SourceExecutable,
        TotalBytes = clip.TotalBytes,
        ContentHash = clip.ContentHash,
        Tags = tags,
    };

    private static Clip ReadClip(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        SortKey = reader.GetDouble(1),
        Pinned = reader.GetInt32(2) != 0,
        CreatedUtc = FromDb(reader.GetString(3)),
        Preview = reader.GetString(4),
        Kind = (ClipKind)reader.GetInt32(5),
        SourceExecutable = reader.IsDBNull(6) ? null : reader.GetString(6),
        TotalBytes = reader.GetInt64(7),
        ContentHash = reader.GetString(8),
        Tags = [],
    };

    private string BuildPreview(ClipboardSnapshot snapshot)
    {
        if (!string.IsNullOrEmpty(snapshot.Text))
        {
            return Truncate(snapshot.Text, PreviewMaxChars);
        }

        // Names the files rather than storing "[files]", which is what makes a file copy findable: history_fts
        // indexes this column. Null only when the CF_HDROP is missing or malformed.
        if (snapshot.Kind == ClipKind.Files
            && FileListPreview.TryDescribe(snapshot.Payloads) is { Length: > 0 } files)
        {
            return Truncate(files, PreviewMaxChars);
        }

        return snapshot.Kind switch
        {
            ClipKind.Image => "[image]",
            ClipKind.Files => "[files]",
            _ => "[binary]",
        };
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    /// <summary>
    /// Turns free text into an FTS5 MATCH expression: every token becomes a quoted prefix term
    /// and they are ANDed. Quoting is what makes this safe - raw user input containing FTS
    /// operators such as <c>NEAR</c>, <c>*</c> or an unbalanced quote would otherwise be a
    /// syntax error thrown in the user's face mid-keystroke.
    /// </summary>
    internal static string? BuildFtsQuery(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        var tokens = term.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder();

        foreach (var token in tokens)
        {
            // Strip characters FTS treats as structure; the quoted form handles the rest.
            var cleaned = token.Replace("\"", string.Empty, StringComparison.Ordinal);

            if (cleaned.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append('"').Append(cleaned).Append("\"*");
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void SetMeta(string key, string value)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_meta (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static string ToDb(DateTimeOffset value)
        => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset FromDb(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
