namespace Wharf;

/// <summary>
/// Server configuration. Values come from appsettings.json, the environment
/// (<c>Wharf__Token</c>), and the command line (<c>--Wharf:Token=...</c>), with
/// <c>WHARF_DATA_DIR</c> taking precedence for the data directory.
/// </summary>
public sealed class WharfOptions
{
    public required string DataDirectory { get; init; }

    /// <summary>Full-access bearer token. Empty disables authentication.</summary>
    public string? Token { get; init; }

    /// <summary>Optional read-only bearer token.</summary>
    public string? ReadToken { get; init; }

    public long MaxFileBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public int MaxPageSize { get; init; } = 5000;
    public int TombstoneRetentionDays { get; init; } = 30;
    public int SessionTtlHours { get; init; } = 24;
    public int GcIntervalMinutes { get; init; } = 60;
    public int ChunkSize { get; init; } = 8 * 1024 * 1024;

    /// <summary>Development-only cross-origin allowlist. Empty means same-origin only.</summary>
    public string? DevOrigin { get; init; }

    public bool AuthEnabled => !string.IsNullOrEmpty(Token) || !string.IsNullOrEmpty(ReadToken);

    public static WharfOptions FromConfiguration(IConfiguration config, string contentRoot)
    {
        var data = Environment.GetEnvironmentVariable("WHARF_DATA_DIR")
            ?? config["Wharf:DataDirectory"]
            ?? Path.Combine(contentRoot, "data");

        if (!Path.IsPathRooted(data))
            data = Path.GetFullPath(Path.Combine(contentRoot, data));

        return new WharfOptions
        {
            DataDirectory = data,
            Token = NullIfEmpty(config["Wharf:Token"]),
            ReadToken = NullIfEmpty(config["Wharf:ReadToken"]),
            MaxFileBytes = GetLong(config, "Wharf:MaxFileBytes", 10L * 1024 * 1024 * 1024),
            MaxPageSize = GetInt(config, "Wharf:MaxPageSize", 5000),
            TombstoneRetentionDays = GetInt(config, "Wharf:TombstoneRetentionDays", 30),
            SessionTtlHours = GetInt(config, "Wharf:SessionTtlHours", 24),
            GcIntervalMinutes = GetInt(config, "Wharf:GcIntervalMinutes", 60),
            ChunkSize = GetInt(config, "Wharf:ChunkSize", 8 * 1024 * 1024),
            DevOrigin = NullIfEmpty(config["Wharf:DevOrigin"]),
        };
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static int GetInt(IConfiguration config, string key, int fallback) =>
        int.TryParse(config[key], out var value) ? value : fallback;

    private static long GetLong(IConfiguration config, string key, long fallback) =>
        long.TryParse(config[key], out var value) ? value : fallback;
}
