namespace Wharf;

/// <summary>
/// Locates the PWA directory. In development it is <c>../web</c> next to the
/// server project; in a publish it is the copied <c>wwwroot</c>.
/// </summary>
public static class WebRoot
{
    public static string? Resolve()
    {
        var configured = Environment.GetEnvironmentVariable("WHARF_WEB_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            return Directory.Exists(full) ? full : null;
        }

        foreach (var candidate in Candidates())
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(Path.Combine(full, "index.html")))
                return full;
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.Combine(cwd, "web");
        yield return Path.Combine(cwd, "..", "web");

        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "wwwroot");
        yield return Path.Combine(baseDir, "..", "..", "..", "..", "web");
    }
}
