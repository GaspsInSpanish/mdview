using System.Net;
using System.Text.RegularExpressions;

namespace MdView.Rendering;

/// <summary>Restricts rendered HTML to known attributes and safe URI forms.</summary>
internal static class UriSanitizer
{
    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "alt", "title", "class", "id", "data-line", "data-document",
        "checked", "type", "disabled", "colspan", "rowspan", "align", "start", "loading"
    };

    private static readonly Regex OpeningTag = new(
        """<(?<name>[A-Za-z][A-Za-z0-9:-]*)(?<attributes>(?:[^"'<>]|"[^"]*"|'[^']*')*)>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Attribute = new(
        """(?<space>\s+)(?<name>[^\s=/>]+)(?<assignment>\s*=\s*(?<value>"[^"]*"|'[^']*'|[^\s>]+))?""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string SanitizeHtml(string html) => OpeningTag.Replace(html, SanitizeTag);

    private static string SanitizeTag(Match tag)
    {
        var tagName = tag.Groups["name"].Value;
        var attributes = tag.Groups["attributes"].Value;
        var sanitized = new System.Text.StringBuilder().Append('<').Append(tagName);
        foreach (Match attribute in Attribute.Matches(attributes))
        {
            var name = attribute.Groups["name"].Value;
            if (!AllowedAttributes.Contains(name)) continue;
            if ((name.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("src", StringComparison.OrdinalIgnoreCase)) &&
                !IsAllowed(GetAttributeValue(attribute), tagName.Equals("img", StringComparison.OrdinalIgnoreCase)))
                continue;
            sanitized.Append(attribute.Value);
        }

        if (attributes.TrimEnd().EndsWith('/')) sanitized.Append(" /");
        return sanitized.Append('>').ToString();
    }

    private static string GetAttributeValue(Match attribute)
    {
        if (!attribute.Groups["assignment"].Success) return string.Empty;
        var value = attribute.Groups["value"].Value;
        return value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') ||
            (value[0] == '\'' && value[^1] == '\'')) ? value[1..^1] : value;
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
