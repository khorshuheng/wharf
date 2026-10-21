using Microsoft.Data.Sqlite;

namespace Wharf.Storage;

public enum DeleteStatus
{
    Ok,
    PreconditionFailed,
    NotEmpty,
}

public readonly record struct DeleteResult(DeleteStatus Status, long Seq);

/// <summary>
/// The mutable path index. Every mutation advances the single monotonic
/// <c>seq</c> cursor, and every row carries the seq of its last mutation, which
/// is all a cursor-based manifest needs.
/// </summary>
public sealed class SyncIndex
{
    public const string FileKind = "file";
    public const string DirectoryKind = "directory";

    private readonly Database _db;
    private readonly object _writeLock = new();

    public SyncIndex(Database db) => _db = db;

    public static string Now() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public long CurrentSeq()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'seq'";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// Identity of this database, generated when it is created. A client
    /// compares it with the value it last saw to detect that the store was
    /// reset, even if the new sequence has already grown past its cursor.
    /// </summary>
    public string InstanceId()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = 'instance_id'";
        return (string)command.ExecuteScalar()!;
    }

    public long MinValidCursor()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'min_valid_cursor'";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public EntryRecord? Get(string path)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " FROM entries WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<EntryRecord> QueryDelta(long since, int limit, bool liveOnly, out bool hasMore)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = liveOnly
            ? SelectColumns + " FROM entries WHERE deleted = 0 AND seq > $since ORDER BY seq LIMIT $limit"
            : SelectColumns + " FROM entries WHERE seq > $since ORDER BY seq LIMIT $limit";
        command.Parameters.AddWithValue("$since", since);
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<EntryRecord>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                entries.Add(Map(reader));
        }

        var next = entries.Count > 0 ? entries[^1].Seq : since;

        using var more = connection.CreateCommand();
        more.CommandText = liveOnly
            ? "SELECT 1 FROM entries WHERE deleted = 0 AND seq > $next LIMIT 1"
            : "SELECT 1 FROM entries WHERE seq > $next LIMIT 1";
        more.Parameters.AddWithValue("$next", next);
        hasMore = more.ExecuteScalar() is not null;

        return entries;
    }

    public EntryRecord UpsertFile(string path, string blob, long size, long mtime, string contentType)
    {
        lock (_writeLock)
        {
            using var connection = _db.Open();
            using var transaction = connection.BeginTransaction();

            EnsureParents(connection, transaction, path);

            var seq = NextSeq(connection, transaction);
            var now = Now();
            WriteEntry(connection, transaction, path, FileKind, blob, size, mtime, seq, false, contentType, now);

            transaction.Commit();
            return new EntryRecord(path, FileKind, blob, size, mtime, seq, false, contentType, now);
        }
    }

    public EntryRecord UpsertDirectory(string path, long mtime, out bool created)
    {
        lock (_writeLock)
        {
            using var connection = _db.Open();
            using var transaction = connection.BeginTransaction();

            EnsureParents(connection, transaction, path);

            var state = ReadState(connection, transaction, path);
            if (state is { Deleted: false, Kind: FileKind })
                throw new PathConflictException(path);

            if (state is { Deleted: false, Kind: DirectoryKind })
            {
                var existing = ReadEntry(connection, transaction, path)!;
                transaction.Commit();
                created = false;
                return existing;
            }

            var seq = NextSeq(connection, transaction);
            var now = Now();
            WriteEntry(connection, transaction, path, DirectoryKind, null, 0, mtime, seq, false, "inode/directory", now);

            transaction.Commit();
            created = true;
            return new EntryRecord(path, DirectoryKind, null, 0, mtime, seq, false, "inode/directory", now);
        }
    }

    public DeleteResult Delete(string path, bool recursive, long? ifMatch)
    {
        lock (_writeLock)
        {
            using var connection = _db.Open();
            using var transaction = connection.BeginTransaction();

            var entry = ReadEntry(connection, transaction, path);
            if (entry is null || entry.Deleted)
            {
                transaction.Commit();
                return new DeleteResult(DeleteStatus.Ok, 0);
            }

            if (ifMatch is long match && entry.Seq != match)
            {
                transaction.Commit();
                return new DeleteResult(DeleteStatus.PreconditionFailed, entry.Seq);
            }

            if (entry.Type == DirectoryKind && !recursive && HasLiveDescendants(connection, transaction, path))
            {
                transaction.Commit();
                return new DeleteResult(DeleteStatus.NotEmpty, entry.Seq);
            }

            var targets = new List<string> { path };
            if (entry.Type == DirectoryKind && recursive)
                targets.AddRange(ReadLiveDescendants(connection, transaction, path));

            var now = Now();
            long maxSeq = 0;
            foreach (var target in targets)
            {
                var seq = NextSeq(connection, transaction);
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE entries SET deleted = 1, seq = $seq, updated_at = $now WHERE path = $path";
                update.Parameters.AddWithValue("$seq", seq);
                update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$path", target);
                update.ExecuteNonQuery();
                maxSeq = Math.Max(maxSeq, seq);
            }

            transaction.Commit();
            return new DeleteResult(DeleteStatus.Ok, maxSeq);
        }
    }

    /// <summary>
    /// Removes tombstones older than the cutoff and advances the minimum valid
    /// cursor past them. Returns the highest purged seq, or zero.
    /// </summary>
    public long PurgeTombstones(DateTimeOffset cutoff)
    {
        lock (_writeLock)
        {
            using var connection = _db.Open();
            using var transaction = connection.BeginTransaction();
            var stamp = cutoff.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            long maxSeq;
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    "SELECT COALESCE(MAX(seq), 0) FROM entries " +
                    "WHERE deleted = 1 AND julianday(updated_at) < julianday($cutoff)";
                select.Parameters.AddWithValue("$cutoff", stamp);
                maxSeq = Convert.ToInt64(select.ExecuteScalar());
            }

            if (maxSeq > 0)
            {
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText =
                        "DELETE FROM entries WHERE deleted = 1 AND julianday(updated_at) < julianday($cutoff)";
                    delete.Parameters.AddWithValue("$cutoff", stamp);
                    delete.ExecuteNonQuery();
                }

                using (var cursor = connection.CreateCommand())
                {
                    cursor.Transaction = transaction;
                    cursor.CommandText =
                        "UPDATE meta SET value = $seq WHERE key = 'min_valid_cursor' AND value < $seq";
                    cursor.Parameters.AddWithValue("$seq", maxSeq);
                    cursor.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return maxSeq;
        }
    }

    public IReadOnlyCollection<string> ReferencedBlobs()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT blob FROM entries WHERE blob IS NOT NULL";

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            hashes.Add(reader.GetString(0));

        return hashes;
    }

    private const string SelectColumns =
        "SELECT path, kind, blob, size, mtime, seq, deleted, content_type, updated_at";

    private static EntryRecord Map(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6) != 0,
            reader.GetString(7),
            reader.GetString(8));

    private static long NextSeq(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE meta SET value = value + 1 WHERE key = 'seq' RETURNING value";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static (string Kind, bool Deleted)? ReadState(
        SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind, deleted FROM entries WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);

        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1) != 0) : null;
    }

    private static EntryRecord? ReadEntry(
        SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectColumns + " FROM entries WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static void EnsureParents(
        SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        var parts = path.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            var parent = string.Join('/', parts, 0, i);
            var state = ReadState(connection, transaction, parent);

            if (state is { Deleted: false, Kind: FileKind })
                throw new PathConflictException(parent);

            if (state is null or { Deleted: true })
            {
                var seq = NextSeq(connection, transaction);
                var now = Now();
                var mtime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                WriteEntry(connection, transaction, parent, DirectoryKind, null, 0, mtime, seq, false, "inode/directory", now);
            }
        }
    }

    private static void WriteEntry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path,
        string kind,
        string? blob,
        long size,
        long mtime,
        long seq,
        bool deleted,
        string contentType,
        string updatedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO entries(path, kind, blob, size, mtime, seq, deleted, content_type, updated_at)
            VALUES ($path, $kind, $blob, $size, $mtime, $seq, $deleted, $content_type, $updated_at)
            ON CONFLICT(path) DO UPDATE SET
                kind = excluded.kind,
                blob = excluded.blob,
                size = excluded.size,
                mtime = excluded.mtime,
                seq = excluded.seq,
                deleted = excluded.deleted,
                content_type = excluded.content_type,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$blob", (object?)blob ?? DBNull.Value);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$mtime", mtime);
        command.Parameters.AddWithValue("$seq", seq);
        command.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
        command.Parameters.AddWithValue("$content_type", contentType);
        command.Parameters.AddWithValue("$updated_at", updatedAt);
        command.ExecuteNonQuery();
    }

    private static bool HasLiveDescendants(
        SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM entries WHERE deleted = 0 AND path LIKE $like ESCAPE '\\' LIMIT 1";
        command.Parameters.AddWithValue("$like", LikePrefix(path));
        return command.ExecuteScalar() is not null;
    }

    private static IEnumerable<string> ReadLiveDescendants(
        SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT path FROM entries WHERE deleted = 0 AND path LIKE $like ESCAPE '\\'";
        command.Parameters.AddWithValue("$like", LikePrefix(path));

        var paths = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            paths.Add(reader.GetString(0));

        return paths;
    }

    private static string LikePrefix(string path) =>
        path.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "/%";
}

public sealed class PathConflictException : Exception
{
    public PathConflictException(string path) : base($"A file already exists at '{path}'")
        => Path = path;

    public string Path { get; }
}
