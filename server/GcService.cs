using Wharf.Storage;

namespace Wharf;

/// <summary>
/// Periodic maintenance: purge tombstones past retention, advance the minimum
/// valid cursor, delete unreferenced blobs, and drop expired upload sessions.
/// </summary>
public sealed class GcService : BackgroundService
{
    private readonly SyncIndex _index;
    private readonly BlobStore _blobs;
    private readonly UploadStore _uploads;
    private readonly WharfOptions _options;
    private readonly ILogger<GcService> _logger;

    public GcService(
        SyncIndex index,
        BlobStore blobs,
        UploadStore uploads,
        WharfOptions options,
        ILogger<GcService> logger)
    {
        _index = index;
        _blobs = blobs;
        _uploads = uploads;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RunOnce();

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.GcIntervalMinutes));
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    RunOnce();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Garbage collection failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RunOnce()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.TombstoneRetentionDays);
        var purgedSeq = _index.PurgeTombstones(cutoff);
        if (purgedSeq > 0)
            _logger.LogInformation("Purged tombstones up to seq {Seq}", purgedSeq);

        var referenced = _index.ReferencedBlobs();
        var removed = _blobs.DeleteUnreferenced(referenced);
        if (removed > 0)
            _logger.LogInformation("Removed {Count} unreferenced blobs", removed);

        var expired = _uploads.PurgeExpired(DateTimeOffset.UtcNow);
        foreach (var session in expired)
            BlobStore.TryDelete(session.TempPath);

        if (expired.Count > 0)
            _logger.LogInformation("Removed {Count} expired upload sessions", expired.Count);
    }
}
