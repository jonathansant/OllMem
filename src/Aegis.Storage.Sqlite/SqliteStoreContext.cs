using Microsoft.Data.Sqlite;

namespace Aegis.Storage.Sqlite;

/// <summary>
/// Owns the SQLite connection string and creates the schema once. Connections
/// are short-lived; Microsoft.Data.Sqlite pools them per connection string, so
/// opening one per operation is cheap. WAL mode is enabled for concurrent reads
/// alongside writes.
/// </summary>
public sealed class SqliteStoreContext
{
    public string ConnectionString { get; }

    public SqliteStoreContext(string connectionString)
    {
        ConnectionString = connectionString;
        Initialize();
    }

    /// <summary>Build a context for a database file, creating parent directories.</summary>
    public static SqliteStoreContext ForFile(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        return new SqliteStoreContext(csb.ToString());
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS memories (
                id               TEXT PRIMARY KEY,
                namespace_key    TEXT NOT NULL,
                owner            TEXT NOT NULL,
                scope            INTEGER NOT NULL,
                scope_id         TEXT NOT NULL,
                statement        TEXT NOT NULL,
                subject          TEXT NOT NULL,
                predicate        TEXT NOT NULL,
                object           TEXT NOT NULL,
                embedding        BLOB NOT NULL,
                blob_ref         TEXT,
                created_at       TEXT NOT NULL,
                last_accessed_at TEXT NOT NULL,
                access_count     INTEGER NOT NULL,
                status           INTEGER NOT NULL,
                superseded_by    TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_memories_ns_status
                ON memories (namespace_key, status);
            CREATE INDEX IF NOT EXISTS ix_memories_ns_subject_status
                ON memories (namespace_key, subject, status);

            CREATE TABLE IF NOT EXISTS blobs (
                blob_ref TEXT PRIMARY KEY,
                data     BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS audit (
                sequence      INTEGER PRIMARY KEY,
                timestamp     TEXT NOT NULL,
                action        INTEGER NOT NULL,
                namespace_key TEXT NOT NULL,
                node_id       TEXT NOT NULL,
                detail        TEXT NOT NULL,
                prev_hash     TEXT NOT NULL,
                hash          TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_ns ON audit (namespace_key);
            """;
        cmd.ExecuteNonQuery();
    }
}
