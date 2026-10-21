using System.Collections.Concurrent;
using System.Text;
using Wharf.Storage;

namespace Wharf.Api;

public sealed record UploadCreateRequest(
    string? Path,
    long? Length,
    string? ContentType,
    string? Blob,
    long? Mtime,
    string? OnConflict);

public sealed record UploadCreateResponse(
    string Id,
    string Path,
    long Offset,
    long? Length,
    int ChunkSize,
    string ExpiresAt);

public static class UploadEndpoints
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SessionLocks = new();

    public static void MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/v1/files/{**path}", PutFile);
        app.MapPost("/api/v1/uploads", CreateSession);
        app.MapMethods("/api/v1/uploads/{id}", new[] { "PATCH" }, AppendChunk);
        app.MapMethods("/api/v1/uploads/{id}", new[] { "HEAD" }, HeadSession);
        app.MapDelete("/api/v1/uploads/{id}", DeleteSession);
    }

    private static async Task<IResult> PutFile(
        HttpContext context,
        string path,
        SyncIndex index,
        BlobStore blobs,
        EntryWriter writer,
        WharfOptions options)
    {
        if (!TokenAuth.CanWrite(context))
            return ApiError.Forbidden("Write scope required");

        if (!PathCodec.TryNormalize(path, out var normalized, out var error))
            return ApiError.BadRequest(error!);

        if (context.Request.ContentLength is long contentLength && contentLength > options.MaxFileBytes)
            return ApiError.PayloadTooLarge(options.MaxFileBytes);

        if (!HeaderParsing.TryNormalizeOnConflict(context.Request.Query["on_conflict"].ToString(), out var onConflict))
            return ApiError.BadRequest("on_conflict must be replace, rename, or fail");

        var expected = DigestHeaders.Parse(context.Request);
        if (expected is not null && (expected.Length != 64 || !PathCodec.IsHex(expected)))
            return ApiError.BadRequest("Content-Digest must be a SHA-256 digest");

        var mtime = HeaderParsing.ParseMtime(
            context.Request.Headers["X-File-Mtime"].ToString(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var (ifMatch, ifMatchAny) = HeaderParsing.ParseIfMatch(context.Request.Headers["If-Match"].ToString());
        var ifNoneMatch = HeaderParsing.IfNoneMatchStar(context.Request.Headers["If-None-Match"].ToString());
        var contentType = HeaderParsing.NormalizeContentType(context.Request.ContentType);

        try
        {
            var (blob, size) = await blobs.WriteAsync(
                context.Request.Body, options.MaxFileBytes, expected, context.RequestAborted);

            var outcome = writer.Commit(new CommitRequest(
                normalized, blob, size, mtime, contentType, onConflict,
                ifMatch, ifMatchAny, ifNoneMatch));

            context.Response.Headers.ETag = $"\"{outcome.Entry.Seq}\"";
            context.Response.Headers.Location = $"/api/v1/entries/{PathCodec.Encode(outcome.Entry.Path)}";

            return Results.Json(new { entry = outcome.Entry }, ApiJson.Options,
                statusCode: outcome.Created ? 201 : 200);
        }
        catch (PayloadTooLargeException ex)
        {
            return ApiError.PayloadTooLarge(ex.Limit);
        }
        catch (DigestMismatchException)
        {
            return ApiError.BadRequest("Content-Digest does not match the uploaded bytes");
        }
        catch (PathConflictException ex)
        {
            return ApiError.Conflict("A parent path is a file", new { ConflictPath = ex.Path });
        }
        catch (ConflictException ex)
        {
            return ApiError.Conflict("Path already exists", new { ConflictPath = ex.Path });
        }
        catch (PreconditionFailedException)
        {
            return ApiError.PreconditionFailed("If-Match or If-None-Match did not match");
        }
    }

    private static IResult CreateSession(
        HttpContext context,
        UploadCreateRequest? body,
        UploadStore uploads,
        BlobStore blobs,
        WharfOptions options)
    {
        if (!TokenAuth.CanWrite(context))
            return ApiError.Forbidden("Write scope required");

        if (body is null)
            return ApiError.BadRequest("a JSON body with a path is required");

        if (!PathCodec.TryNormalize(body.Path, out var normalized, out var error))
            return ApiError.BadRequest(error!);

        if (body.Length is long length && length > options.MaxFileBytes)
            return ApiError.PayloadTooLarge(options.MaxFileBytes);

        if (!HeaderParsing.TryNormalizeOnConflict(body.OnConflict, out var onConflict))
            return ApiError.BadRequest("on_conflict must be replace, rename, or fail");

        string? expected = null;
        if (!string.IsNullOrWhiteSpace(body.Blob))
        {
            if (body.Blob.Length != 64 || !PathCodec.IsHex(body.Blob))
                return ApiError.BadRequest("blob must be a lowercase hex SHA-256 digest");

            expected = body.Blob.ToLowerInvariant();
        }

        var id = "up_" + Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(blobs.TempDirectory, id + ".part");
        var now = DateTimeOffset.UtcNow;

        var session = uploads.Create(
            id,
            normalized,
            body.Length,
            HeaderParsing.NormalizeContentType(body.ContentType),
            body.Mtime ?? now.ToUnixTimeMilliseconds(),
            onConflict,
            expected,
            tempPath,
            now.AddHours(options.SessionTtlHours));

        return Results.Json(
            new UploadCreateResponse(session.Id, session.Path, session.Offset, session.Length,
                options.ChunkSize, session.ExpiresAt),
            ApiJson.Options,
            statusCode: 201);
    }

    private static async Task<IResult> AppendChunk(
        HttpContext context,
        string id,
        UploadStore uploads,
        BlobStore blobs,
        EntryWriter writer,
        WharfOptions options)
    {
        if (!TokenAuth.CanWrite(context))
            return ApiError.Forbidden("Write scope required");

        if (uploads.Get(id) is null)
            return ApiError.NotFound("No such upload session");

        var gate = SessionLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(context.RequestAborted);

        try
        {
            var session = uploads.Get(id);
            if (session is null)
                return ApiError.SessionExpired();

            if (DateTimeOffset.TryParse(session.ExpiresAt, out var expires) && expires < DateTimeOffset.UtcNow)
            {
                uploads.Delete(id);
                BlobStore.TryDelete(session.TempPath);
                return ApiError.SessionExpired();
            }

            if (!long.TryParse(context.Request.Headers["Upload-Offset"].ToString(), out var offset))
                return ApiError.BadRequest("Upload-Offset header is required");

            if (offset != session.Offset)
            {
                context.Response.Headers["Upload-Offset"] = session.Offset.ToString();
                return ApiError.Conflict("Upload-Offset does not match", new { Offset = session.Offset });
            }

            if (session.Length is long declaredLength && offset > declaredLength)
                return ApiError.PayloadTooLarge(declaredLength);

            long limit = options.MaxFileBytes;
            if (session.Length is long declared && declared < limit)
                limit = declared;

            var written = 0L;
            var overflow = false;

            await using (var output = new FileStream(
                session.TempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous))
            {
                output.Seek(offset, SeekOrigin.Begin);

                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                {
                    if (offset + written + read > limit)
                    {
                        overflow = true;
                        break;
                    }

                    written += read;
                    await output.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                }

                await output.FlushAsync(context.RequestAborted);
                output.Flush(flushToDisk: true);

                if (overflow)
                    output.SetLength(offset);
            }

            if (overflow)
                return ApiError.PayloadTooLarge(limit);

            var newOffset = offset + written;
            uploads.UpdateOffset(id, newOffset);

            var complete = session.Length is long length && newOffset >= length;
            if (!complete)
                complete = context.Request.Query["complete"].ToString() is "true" or "1";

            if (!complete)
            {
                context.Response.Headers["Upload-Offset"] = newOffset.ToString();
                return Results.NoContent();
            }

            var hex = await BlobStore.HashFileAsync(session.TempPath, context.RequestAborted);

            if (session.ExpectedBlob is not null &&
                !hex.Equals(session.ExpectedBlob, StringComparison.OrdinalIgnoreCase))
                return ApiError.BadRequest("Content digest does not match");

            var size = new FileInfo(session.TempPath).Length;
            blobs.Adopt(session.TempPath, hex);
            uploads.Delete(id);

            try
            {
                var outcome = writer.Commit(new CommitRequest(
                    session.Path, hex, size, session.Mtime, session.ContentType,
                    session.OnConflict, null, false, false));

                context.Response.Headers.ETag = $"\"{outcome.Entry.Seq}\"";
                context.Response.Headers.Location = $"/api/v1/entries/{PathCodec.Encode(outcome.Entry.Path)}";

                return Results.Json(new { entry = outcome.Entry }, ApiJson.Options,
                    statusCode: outcome.Created ? 201 : 200);
            }
            catch (ConflictException ex)
            {
                return ApiError.Conflict("Path already exists", new { ConflictPath = ex.Path });
            }
            catch (PathConflictException ex)
            {
                return ApiError.Conflict("A parent path is a file", new { ConflictPath = ex.Path });
            }
            catch (PreconditionFailedException)
            {
                return ApiError.PreconditionFailed("If-Match or If-None-Match did not match");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static IResult HeadSession(HttpContext context, string id, UploadStore uploads)
    {
        var session = uploads.Get(id);
        if (session is null)
            return Results.NotFound();

        context.Response.Headers["Upload-Offset"] = session.Offset.ToString();
        if (session.Length is long length)
            context.Response.Headers["Upload-Length"] = length.ToString();
        context.Response.Headers["Upload-Expires"] = session.ExpiresAt;
        context.Response.Headers.CacheControl = "no-store";

        return Results.Ok();
    }

    private static IResult DeleteSession(HttpContext context, string id, UploadStore uploads)
    {
        if (!TokenAuth.CanWrite(context))
            return ApiError.Forbidden("Write scope required");

        var session = uploads.Get(id);
        uploads.Delete(id);
        if (session is not null)
            BlobStore.TryDelete(session.TempPath);

        return Results.NoContent();
    }
}
