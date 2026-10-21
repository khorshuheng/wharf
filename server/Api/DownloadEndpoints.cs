using Microsoft.Net.Http.Headers;
using Wharf.Storage;

namespace Wharf.Api;

public static class DownloadEndpoints
{
    public static void MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/files/{**path}",
            (HttpContext context, string path, SyncIndex index, BlobStore blobs) =>
            {
                if (!PathCodec.TryNormalize(path, out var normalized, out var error))
                    return ApiError.BadRequest(error!);

                var entry = index.Get(normalized);
                if (entry is null || entry.Deleted)
                    return ApiError.NotFound("No such file");

                if (entry.Type != SyncIndex.FileKind)
                    return ApiError.Conflict("Path is a directory");

                if (entry.Blob is null || !blobs.Exists(entry.Blob))
                    return ApiError.NotFound("Content is no longer available");

                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "sandbox";
                context.Response.Headers.CacheControl = "no-cache";

                var etag = new EntityTagHeaderValue($"\"{entry.Blob}\"");
                return Results.File(
                    blobs.PathFor(entry.Blob),
                    entry.ContentType,
                    fileDownloadName: Path.GetFileName(entry.Path),
                    lastModified: DateTimeOffset.FromUnixTimeMilliseconds(entry.Mtime),
                    entityTag: etag,
                    enableRangeProcessing: true);
            });

        app.MapGet("/api/v1/blobs/sha256/{hex}",
            (HttpContext context, string hex, BlobStore blobs) =>
            {
                if (hex.Length != 64 || !PathCodec.IsHex(hex))
                    return ApiError.BadRequest("Invalid blob digest");

                hex = hex.ToLowerInvariant();
                if (!blobs.Exists(hex))
                    return ApiError.NotFound("No such blob");

                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Content-Security-Policy"] = "sandbox";
                context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

                var etag = new EntityTagHeaderValue($"\"{hex}\"");
                return Results.File(
                    blobs.PathFor(hex),
                    "application/octet-stream",
                    entityTag: etag,
                    enableRangeProcessing: true);
            });
    }
}
