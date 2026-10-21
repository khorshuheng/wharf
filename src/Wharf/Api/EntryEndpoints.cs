using Wharf.Storage;

namespace Wharf.Api;

public static class EntryEndpoints
{
    public static void MapEntryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/entries/{**path}",
            (HttpContext context, string path, SyncIndex index) =>
            {
                if (!PathCodec.TryNormalize(path, out var normalized, out var error))
                    return ApiError.BadRequest(error!);

                var entry = index.Get(normalized);
                if (entry is null || entry.Deleted)
                    return ApiError.NotFound("No such entry");

                context.Response.Headers.ETag = $"\"{entry.Seq}\"";
                context.Response.Headers.CacheControl = "no-cache";
                return Results.Json(entry, ApiJson.Options);
            });

        app.MapDelete("/api/v1/entries/{**path}",
            (HttpContext context, string path, bool? recursive, SyncIndex index, ChangeNotifier notifier) =>
            {
                if (!TokenAuth.CanWrite(context))
                    return ApiError.Forbidden("Write scope required");

                if (!PathCodec.TryNormalize(path, out var normalized, out var error))
                    return ApiError.BadRequest(error!);

                var (seq, any) = HeaderParsing.ParseIfMatch(context.Request.Headers["If-Match"].ToString());
                var result = index.Delete(normalized, recursive ?? false, any ? null : seq);

                switch (result.Status)
                {
                    case DeleteStatus.PreconditionFailed:
                        return ApiError.PreconditionFailed("If-Match does not match");

                    case DeleteStatus.NotEmpty:
                        return ApiError.Conflict("Directory is not empty; use recursive=true");

                    default:
                        if (result.Seq > 0)
                            notifier.Publish(result.Seq);
                        return Results.NoContent();
                }
            });

        app.MapPut("/api/v1/directories/{**path}",
            (HttpContext context, string path, SyncIndex index, ChangeNotifier notifier) =>
            {
                if (!TokenAuth.CanWrite(context))
                    return ApiError.Forbidden("Write scope required");

                if (!PathCodec.TryNormalize(path, out var normalized, out var error))
                    return ApiError.BadRequest(error!);

                try
                {
                    var entry = index.UpsertDirectory(
                        normalized,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        out var created);

                    notifier.Publish(entry.Seq);
                    context.Response.Headers.ETag = $"\"{entry.Seq}\"";
                    context.Response.Headers.Location = $"/api/v1/entries/{PathCodec.Encode(entry.Path)}";

                    return Results.Json(new { entry }, ApiJson.Options, statusCode: created ? 201 : 200);
                }
                catch (PathConflictException ex)
                {
                    return ApiError.Conflict("A file exists at this path", new { ConflictPath = ex.Path });
                }
            });
    }
}
