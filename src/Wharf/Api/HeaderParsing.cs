namespace Wharf.Api;

public static class HeaderParsing
{
    /// <summary>
    /// Parses <c>If-Match</c>. Returns the seq to match, and whether the
    /// wildcard form (<c>*</c>) was used.
    /// </summary>
    public static (long? Seq, bool Any) ParseIfMatch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, false);

        value = value.Trim();
        if (value == "*")
            return (null, true);

        var first = value.Split(',')[0].Trim();
        if (first.Length >= 2 && first[0] == '"' && first[^1] == '"')
            first = first[1..^1];

        return long.TryParse(first, out var seq) ? (seq, false) : (null, false);
    }

    public static bool IfNoneMatchStar(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Split(',').Any(part => part.Trim() == "*");

    public static string NormalizeContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "application/octet-stream";

        var semicolon = value.IndexOf(';');
        var media = (semicolon >= 0 ? value[..semicolon] : value).Trim();
        return media.Length is > 0 and <= 255 ? media : "application/octet-stream";
    }

    public static bool TryNormalizeOnConflict(string? value, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? "replace" : value.Trim().ToLowerInvariant();
        return normalized is "replace" or "rename" or "fail";
    }

    public static long ParseMtime(string? raw, long fallback) =>
        long.TryParse(raw, out var value) && value > 0 ? value : fallback;
}

/// <summary>Parses RFC 9530 <c>Content-Digest</c> (and the older <c>Digest</c>).</summary>
public static class DigestHeaders
{
    public static string? Parse(HttpRequest request)
    {
        var value = request.Headers["Content-Digest"].ToString();
        if (string.IsNullOrEmpty(value))
            value = request.Headers["Digest"].ToString();
        if (string.IsNullOrEmpty(value))
            return null;

        foreach (var part in value.Split(','))
        {
            var trimmed = part.Trim();
            var equals = trimmed.IndexOf('=');
            if (equals <= 0)
                continue;

            var algorithm = trimmed[..equals].Trim();
            if (!algorithm.Equals("sha-256", StringComparison.OrdinalIgnoreCase))
                continue;

            var digest = trimmed[(equals + 1)..].Trim();
            if (digest.Length >= 2 && digest[0] == ':' && digest[^1] == ':')
                digest = digest[1..^1];

            try
            {
                var bytes = Convert.FromBase64String(digest);
                if (bytes.Length == 32)
                    return Convert.ToHexString(bytes).ToLowerInvariant();
            }
            catch (FormatException)
            {
            }
        }

        return null;
    }
}
