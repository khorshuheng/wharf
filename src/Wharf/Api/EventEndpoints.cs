using Wharf.Storage;

namespace Wharf.Api;

public static class EventEndpoints
{
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/events", async (HttpContext context, SyncIndex index, ChangeNotifier notifier) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Connection = "keep-alive";

            await context.Response.WriteAsync("retry: 3000\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);

            var current = index.CurrentSeq();
            var min = index.MinValidCursor();

            var last = long.TryParse(context.Request.Headers["Last-Event-ID"].ToString(), out var lastId)
                ? lastId
                : (long?)null;

            if (last is long cursor && cursor < min)
                await WriteEvent(context, "reset", current,
                    $"{{\"seq\":{current},\"reason\":\"cursor_expired\"}}");
            else
                await WriteEvent(context, "change", current, $"{{\"seq\":{current}}}");

            using var subscription = notifier.Subscribe();

            while (!context.RequestAborted.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));

                try
                {
                    if (!await subscription.Reader.WaitToReadAsync(timeout.Token))
                        break;

                    long seq = 0;
                    while (subscription.Reader.TryRead(out var value))
                        seq = value;

                    await WriteEvent(context, "change", seq, $"{{\"seq\":{seq}}}");
                }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
                {
                    await context.Response.WriteAsync(": ping\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }

            return Results.Empty;
        });
    }

    private static async Task WriteEvent(HttpContext context, string name, long seq, string data)
    {
        await context.Response.WriteAsync(
            $"id: {seq}\nevent: {name}\ndata: {data}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}
