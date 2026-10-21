using Wharf.Storage;

namespace Wharf.Api;

public sealed record CommitRequest(
    string Path,
    string Blob,
    long Size,
    long Mtime,
    string ContentType,
    string OnConflict,
    long? IfMatch,
    bool IfMatchAny,
    bool IfNoneMatch);

public sealed record CommitOutcome(EntryRecord Entry, bool Created);

/// <summary>
/// Applies optimistic-concurrency and name-collision policy, then writes the
/// entry and pokes SSE subscribers.
/// </summary>
public sealed class EntryWriter
{
    private readonly SyncIndex _index;
    private readonly ChangeNotifier _notifier;

    public EntryWriter(SyncIndex index, ChangeNotifier notifier)
    {
        _index = index;
        _notifier = notifier;
    }

    public CommitOutcome Commit(CommitRequest request)
    {
        var path = request.Path;
        var existing = _index.Get(path);
        var live = existing is { Deleted: false };

        if (request.IfMatchAny && !live)
            throw new PreconditionFailedException();

        if (request.IfMatch is long match && (existing is null || existing.Seq != match))
            throw new PreconditionFailedException();

        if (request.IfNoneMatch && live)
            throw new PreconditionFailedException();

        if (live)
        {
            switch (request.OnConflict)
            {
                case "fail":
                    throw new ConflictException(path);
                case "rename":
                    path = UniquePath(path);
                    break;
            }
        }

        var created = existing is null || existing.Deleted;
        var entry = _index.UpsertFile(path, request.Blob, request.Size, request.Mtime, request.ContentType);
        _notifier.Publish(entry.Seq);

        return new CommitOutcome(entry, created);
    }

    private string UniquePath(string path)
    {
        var directory = Path.GetDirectoryName(path)?.Replace('\\', '/');
        var name = Path.GetFileName(path);
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;

        for (var n = 2; n < 10_000; n++)
        {
            var candidate = string.IsNullOrEmpty(directory)
                ? $"{stem} ({n}){extension}"
                : $"{directory}/{stem} ({n}){extension}";

            var existing = _index.Get(candidate);
            if (existing is null || existing.Deleted)
                return candidate;
        }

        throw new ConflictException(path);
    }
}

public sealed class ConflictException : Exception
{
    public ConflictException(string path) : base($"Path already exists: '{path}'")
        => Path = path;

    public string Path { get; }
}

public sealed class PreconditionFailedException : Exception
{
    public PreconditionFailedException() : base("Precondition failed")
    {
    }
}
