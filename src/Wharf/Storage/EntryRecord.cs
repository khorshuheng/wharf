namespace Wharf.Storage;

/// <summary>
/// One path in the namespace. <see cref="Type"/> is <c>file</c> or
/// <c>directory</c>; <see cref="Blob"/> is the lowercase hex SHA-256 digest for
/// files and <c>null</c> for directories.
/// </summary>
public sealed record EntryRecord(
    string Path,
    string Type,
    string? Blob,
    long Size,
    long Mtime,
    long Seq,
    bool Deleted,
    string ContentType,
    string UpdatedAt);
