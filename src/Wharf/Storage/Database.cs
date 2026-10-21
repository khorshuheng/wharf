using Microsoft.Data.Sqlite;

namespace Wharf.Storage;

/// <summary>Owns the SQLite connection string and schema.</summary>
public sealed class Database
{
    public string Path { get; }
    public string ConnectionString { get; }

    public Database(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();

        Initialize();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO meta(key, value) VALUES ('seq', 0), ('min_valid_cursor', 0);

            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            INSERT OR IGNORE INTO settings(key, value)
            VALUES ('instance_id', lower(hex(randomblob(16))));

            CREATE TABLE IF NOT EXISTS entries (
                path         TEXT PRIMARY KEY,
                kind         TEXT NOT NULL,
                blob         TEXT,
                size         INTEGER NOT NULL,
                mtime        INTEGER NOT NULL,
                seq          INTEGER NOT NULL,
                deleted      INTEGER NOT NULL DEFAULT 0,
                content_type TEXT NOT NULL,
                updated_at   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_entries_seq ON entries(seq);
            CREATE INDEX IF NOT EXISTS ix_entries_live ON entries(deleted, seq);

            CREATE TABLE IF NOT EXISTS uploads (
                id            TEXT PRIMARY KEY,
                path          TEXT NOT NULL,
                length        INTEGER,
                offset        INTEGER NOT NULL,
                content_type  TEXT NOT NULL,
                mtime         INTEGER NOT NULL,
                on_conflict   TEXT NOT NULL,
                expected_blob TEXT,
                temp_path     TEXT NOT NULL,
                created_at    TEXT NOT NULL,
                expires_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_uploads_expires ON uploads(expires_at);
            """;
        command.ExecuteNonQuery();
    }
}
