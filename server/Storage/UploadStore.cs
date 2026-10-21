using Microsoft.Data.Sqlite;

namespace Wharf.Storage;

public sealed record UploadSession(
    string Id,
    string Path,
    long? Length,
    long Offset,
    string ContentType,
    long Mtime,
    string OnConflict,
    string? ExpectedBlob,
    string TempPath,
    string CreatedAt,
    string ExpiresAt);

/// <summary>Persists resumable upload sessions so they survive a restart.</summary>
public sealed class UploadStore
{
    private readonly Database _db;

    public UploadStore(Database db) => _db = db;

    public UploadSession Create(
        string id,
        string path,
        long? length,
        string contentType,
        long mtime,
        string onConflict,
        string? expectedBlob,
        string tempPath,
        DateTimeOffset expiresAt)
    {
        var now = SyncIndex.Now();
        var expires = expiresAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO uploads(id, path, length, offset, content_type, mtime, on_conflict,
                                expected_blob, temp_path, created_at, expires_at)
            VALUES ($id, $path, $length, 0, $content_type, $mtime, $on_conflict,
                    $expected_blob, $temp_path, $created_at, $expires_at)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$length", (object?)length ?? DBNull.Value);
        command.Parameters.AddWithValue("$content_type", contentType);
        command.Parameters.AddWithValue("$mtime", mtime);
        command.Parameters.AddWithValue("$on_conflict", onConflict);
        command.Parameters.AddWithValue("$expected_blob", (object?)expectedBlob ?? DBNull.Value);
        command.Parameters.AddWithValue("$temp_path", tempPath);
        command.Parameters.AddWithValue("$created_at", now);
        command.Parameters.AddWithValue("$expires_at", expires);
        command.ExecuteNonQuery();

        return new UploadSession(id, path, length, 0, contentType, mtime, onConflict,
            expectedBlob, tempPath, now, expires);
    }

    public UploadSession? Get(string id)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, path, length, offset, content_type, mtime, on_conflict,
                   expected_blob, temp_path, created_at, expires_at
            FROM uploads WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void UpdateOffset(string id, long offset)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE uploads SET offset = $offset WHERE id = $id";
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Delete(string id)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM uploads WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<UploadSession> PurgeExpired(DateTimeOffset now)
    {
        var stamp = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        using var connection = _db.Open();

        var expired = new List<UploadSession>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT id, path, length, offset, content_type, mtime, on_conflict,
                       expected_blob, temp_path, created_at, expires_at
                FROM uploads WHERE julianday(expires_at) < julianday($now)
                """;
            select.Parameters.AddWithValue("$now", stamp);

            using var reader = select.ExecuteReader();
            while (reader.Read())
                expired.Add(Map(reader));
        }

        if (expired.Count == 0)
            return expired;

        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM uploads WHERE julianday(expires_at) < julianday($now)";
        delete.Parameters.AddWithValue("$now", stamp);
        delete.ExecuteNonQuery();

        return expired;
    }

    private static UploadSession Map(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10));
}
