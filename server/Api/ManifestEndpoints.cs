using Wharf.Storage;

namespace Wharf.Api;

public sealed record ManifestResponse(
    string InstanceId,
    IReadOnlyList<EntryRecord> Entries,
    long Next,
    bool HasMore);

public static class ManifestEndpoints
{
    public static void MapManifestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/manifest",
            (HttpContext context, long? since, int? limit, SyncIndex index, WharfOptions options) =>
            {
                var cursor = since ?? 0;
                var pageSize = limit ?? 500;

                if (cursor < 0)
                    return ApiError.BadRequest("since must be >= 0");

                if (pageSize < 1 || pageSize > options.MaxPageSize)
                    return ApiError.BadRequest($"limit must be between 1 and {options.MaxPageSize}");

                if (cursor > 0 && cursor < index.MinValidCursor())
                    return ApiError.CursorExpired(index.CurrentSeq());

                // A cursor ahead of the store is impossible in a monotonic log;
                // it means the database was reset or rolled back.
                if (cursor > index.CurrentSeq())
                    return ApiError.CursorExpired(index.CurrentSeq());

                var entries = index.QueryDelta(cursor, pageSize, liveOnly: cursor == 0, out var hasMore);
                var next = entries.Count > 0 ? entries[^1].Seq : cursor;

                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(
                    new ManifestResponse(index.InstanceId(), entries, next, hasMore),
                    ApiJson.Options);
            });
    }
}
