using System.Buffers;
using System.Security.Cryptography;

namespace Wharf.Storage;

/// <summary>
/// Immutable, content-addressed blob storage. A blob is named by its lowercase
/// hex SHA-256 digest and sharded by the first two characters; it never changes
/// once written.
/// </summary>
public sealed class BlobStore
{
    private const int BufferSize = 64 * 1024;

    private readonly string _root;
    private readonly string _temp;

    public BlobStore(string dataDirectory)
    {
        _root = Path.Combine(dataDirectory, "objects", "sha256");
        _temp = Path.Combine(dataDirectory, "tmp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    public string TempDirectory => _temp;

    public string PathFor(string hex) => Path.Combine(_root, hex[..2], hex);

    public bool Exists(string hex) => File.Exists(PathFor(hex));

    public FileStream OpenRead(string hex) => new(
        PathFor(hex),
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// Streams the body to a temporary file, hashes it, and moves it into the
    /// object store. On any failure the temporary file is removed and no blob is
    /// visible.
    /// </summary>
    public async Task<(string Hex, long Size)> WriteAsync(
        Stream input, long maxBytes, string? expectedHex, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".part");
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            long size = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            await using (var output = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                {
                    size += read;
                    if (size > maxBytes)
                        throw new PayloadTooLargeException(maxBytes);

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            var hex = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            if (expectedHex is not null && !FixedEquals(hex, expectedHex))
                throw new DigestMismatchException();

            MoveIntoPlace(temp, hex);
            return (hex, size);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Moves a fully written temporary file into the object store.</summary>
    public void Adopt(string tempPath, string hex)
    {
        try
        {
            MoveIntoPlace(tempPath, hex);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                hash.AppendData(buffer, 0, read);

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Deletes every blob not in <paramref name="referenced"/>.</summary>
    public int DeleteUnreferenced(IReadOnlyCollection<string> referenced)
    {
        if (!Directory.Exists(_root))
            return 0;

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var hex = Path.GetFileName(file);
            if (referenced.Contains(hex))
                continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (IOException)
            {
                // Another process is reading it; try again next sweep.
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (IOException)
            {
            }
        }

        return removed;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private void MoveIntoPlace(string temp, string hex)
    {
        var final = PathFor(hex);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);

        if (File.Exists(final))
        {
            TryDelete(temp);
            return;
        }

        try
        {
            File.Move(temp, final);
        }
        catch (IOException) when (File.Exists(final))
        {
            // Lost a race with an identical upload; the content is the same.
            TryDelete(temp);
        }
    }

    private static bool FixedEquals(string a, string b)
    {
        if (a.Length != b.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(a),
            System.Text.Encoding.ASCII.GetBytes(b));
    }
}

public sealed class PayloadTooLargeException : Exception
{
    public PayloadTooLargeException(long limit) : base($"Payload exceeds {limit} bytes")
        => Limit = limit;

    public long Limit { get; }
}

public sealed class DigestMismatchException : Exception
{
    public DigestMismatchException() : base("Content digest does not match")
    {
    }
}
