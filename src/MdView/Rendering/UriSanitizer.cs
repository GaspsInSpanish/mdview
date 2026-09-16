using System.Net;
using System.Text.RegularExpressions;

namespace MdView.Rendering;

/// <summary>Restricts rendered link and image targets to safe URI forms.</summary>
internal static class UriSanitizer
{
    private static readonly Regex LinkOrImageTag = new(
        @"<(?<tag>a|img)\b(?<attributes>[^<>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex UriAttribute = new(
        "\\s+(?<name>href|src)\\s*=\\s*\\\"(?<value>[^\\\"]*)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string SanitizeHtml(string html) => LinkOrImageTag.Replace(html, SanitizeTag);

    private static string SanitizeTag(Match tag)
    {
        var isImage = tag.Groups["tag"].Value.Equals("img", StringComparison.OrdinalIgnoreCase);
        return UriAttribute.Replace(tag.Value, attribute => SanitizeAttribute(attribute, isImage));
    }

    private static string SanitizeAttribute(Match attribute, bool isImage)
    {
        return IsAllowed(attribute.Groups["value"].Value, isImage) ? attribute.Value : string.Empty;
    }

    private static bool IsAllowed(string target, bool isImage)
    {
        var normalized = Normalize(target);
        var colon = normalized.IndexOf(':');

        if (colon < 0 || !IsScheme(normalized.AsSpan(0, colon)))
        {
            return true;
        }

        var scheme = normalized[..colon];
        if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase) ||
            scheme.Equals("file", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return isImage && scheme.Equals("data", StringComparison.OrdinalIgnoreCase) &&
            IsAllowedDataImage(normalized[(colon + 1)..]);
    }

    private static bool IsAllowedDataImage(string dataContent)
    {
        var commaOrParameter = dataContent.IndexOfAny([',', ';']);
        var mediaType = commaOrParameter < 0 ? dataContent : dataContent[..commaOrParameter];

        return mediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("image/gif", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("image/webp", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string target)
    {
        var normalized = target;
        for (var i = 0; i < 3; i++)
        {
            normalized = RemoveBrowserIgnoredCharacters(WebUtility.HtmlDecode(normalized).Trim());

            try
            {
                normalized = Uri.UnescapeDataString(normalized);
            }
            catch (UriFormatException)
            {
                // Invalid percent-encoding cannot turn into an allowlisted scheme.
            }
        }

        return RemoveBrowserIgnoredCharacters(normalized.Trim());
    }

    private static string RemoveBrowserIgnoredCharacters(string value) => value
        .Replace("\t", string.Empty, StringComparison.Ordinal)
        .Replace("\n", string.Empty, StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\0", string.Empty, StringComparison.Ordinal);

    private static bool IsScheme(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (var character in value[1..])
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '+' and not '-' and not '.')
            {
                return false;
            }
        }

        return true;
    }
}
