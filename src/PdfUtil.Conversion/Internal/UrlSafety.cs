namespace PdfUtil.Conversion.Internal;

/// <summary>
/// Decides which link addresses may be written: http, https, mailto, ftp and tel URIs, and relative references.
/// Script URLs (<c>javascript:</c>, <c>vbscript:</c>, <c>data:</c>) and local file or launch schemes are refused.
/// </summary>
internal static class UrlSafety
{
    private static readonly string[] AllowedSchemes = { "http", "https", "mailto", "ftp", "tel" };

    /// <summary>The trimmed address when it is safe to link to, otherwise null.</summary>
    public static string? Check(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 8192)
        {
            return null;
        }

        var trimmed = uri.Trim();

        // Browsers ignore control characters and whitespace inside a scheme ("java\tscript:"), so they are refused.
        if (trimmed.Any(c => char.IsControl(c)))
        {
            return null;
        }

        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        var delimiter = trimmed.IndexOfAny(new[] { '/', '?', '#' });
        if (colon < 0 || (delimiter >= 0 && delimiter < colon))
        {
            // A relative reference: a path, a query or a fragment.
            return trimmed;
        }

        var scheme = trimmed[..colon];
        return AllowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase) && scheme.All(char.IsAsciiLetter) ? trimmed : null;
    }
}
