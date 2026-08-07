namespace PasteJump.Core.Storage;

/// <summary>DDL for the PasteJump database, applied idempotently at startup.</summary>
internal static class PasteJumpSchema
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// Connection-level pragmas. WAL is the important one: it lets the History window read while
    /// a capture writes, without either blocking the other.
    /// </summary>
    public const string Pragmas = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 3000;
        """;

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS schema_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS clip (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            sort_key     REAL    NOT NULL,
            pinned       INTEGER NOT NULL DEFAULT 0,
            created_utc  TEXT    NOT NULL,
            preview      TEXT    NOT NULL,
            kind         INTEGER NOT NULL,
            source_exe   TEXT,
            total_bytes  INTEGER NOT NULL,
            content_hash TEXT    NOT NULL
        );

        -- Serves the one hot query: pinned first, then newest first.
        CREATE INDEX IF NOT EXISTS ix_clip_order ON clip(pinned DESC, sort_key DESC);
        CREATE INDEX IF NOT EXISTS ix_clip_hash  ON clip(content_hash);

        CREATE TABLE IF NOT EXISTS clip_format (
            clip_id     INTEGER NOT NULL REFERENCES clip(id) ON DELETE CASCADE,
            format_id   INTEGER NOT NULL,
            format_name TEXT,
            data        BLOB,
            blob_hash   TEXT,
            byte_len    INTEGER NOT NULL,
            PRIMARY KEY (clip_id, format_id)
        );

        CREATE TABLE IF NOT EXISTS tag (
            id   INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE
        );

        CREATE TABLE IF NOT EXISTS clip_tag (
            clip_id INTEGER NOT NULL REFERENCES clip(id) ON DELETE CASCADE,
            tag_id  INTEGER NOT NULL REFERENCES tag(id)  ON DELETE CASCADE,
            PRIMARY KEY (clip_id, tag_id)
        );

        CREATE TABLE IF NOT EXISTS history (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            captured_utc  TEXT    NOT NULL,
            kind          INTEGER NOT NULL,
            preview       TEXT    NOT NULL,
            blob_hash     TEXT,
            total_bytes   INTEGER NOT NULL,
            imported_from TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_history_time ON history(captured_utc DESC);

        -- FTS5 over history previews. The original scanned every row with Instr() in
        -- AutoHotkey (searchPasteMode.ahk:83), which is why its search crawled once the
        -- history grew. External-content mode avoids storing the text twice.
        CREATE VIRTUAL TABLE IF NOT EXISTS history_fts
            USING fts5(preview, content='history', content_rowid='id');

        CREATE TRIGGER IF NOT EXISTS history_fts_ai AFTER INSERT ON history BEGIN
            INSERT INTO history_fts(rowid, preview) VALUES (new.id, new.preview);
        END;

        CREATE TRIGGER IF NOT EXISTS history_fts_ad AFTER DELETE ON history BEGIN
            INSERT INTO history_fts(history_fts, rowid, preview)
                VALUES ('delete', old.id, old.preview);
        END;

        CREATE TRIGGER IF NOT EXISTS history_fts_au AFTER UPDATE ON history BEGIN
            INSERT INTO history_fts(history_fts, rowid, preview)
                VALUES ('delete', old.id, old.preview);
            INSERT INTO history_fts(rowid, preview) VALUES (new.id, new.preview);
        END;
        """;
}
