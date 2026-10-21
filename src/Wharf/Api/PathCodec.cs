using System.Text;

namespace Wharf.Api;

/// <summary>Validates and canonicalizes the user-visible path namespace.</summary>
public static class PathCodec
{
    public const int MaxPathBytes = 4096;
    public const int MaxSegmentBytes = 255;

    public static bool TryNormalize(string? raw, out string path, out string? error)
    {
        path = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "path is required";
            return false;
        }

        if (raw.Contains('\0'))
        {
            error = "path contains NUL";
            return false;
        }

        if (raw.Contains('\\'))
        {
            error = "path must use '/' separators";
            return false;
        }

        var segments = raw.Split('/');
        var normalized = new string[segments.Length];

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment.Length == 0)
            {
                error = "path contains an empty segment";
                return false;
            }

            if (segment is "." or "..")
            {
                error = "path contains a relative segment";
                return false;
            }

            if (segment.Any(c => c < 0x20 || c == 0x7f))
            {
                error = "path contains a control character";
                return false;
            }

            var nfc = segment.Normalize(NormalizationForm.FormC);
            if (Encoding.UTF8.GetByteCount(nfc) > MaxSegmentBytes)
            {
                error = $"path segment exceeds {MaxSegmentBytes} bytes";
                return false;
            }

            normalized[i] = nfc;
        }

        var joined = string.Join('/', normalized);

        if (Encoding.UTF8.GetByteCount(joined) > MaxPathBytes)
        {
            error = $"path exceeds {MaxPathBytes} bytes";
            return false;
        }

        if (normalized[0].Equals(".wharf", StringComparison.OrdinalIgnoreCase))
        {
            error = "path uses the reserved .wharf prefix";
            return false;
        }

        path = joined;
        return true;
    }

    public static string Encode(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public static bool IsHex(string value) =>
        value.Length > 0 && value.All(Uri.IsHexDigit);
}
