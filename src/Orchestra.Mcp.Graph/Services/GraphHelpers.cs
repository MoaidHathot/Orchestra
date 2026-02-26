using System.Text.RegularExpressions;

namespace Orchestra.Mcp.Graph.Services;

/// <summary>
/// Helper utilities for Graph API responses.
/// </summary>
public static partial class GraphHelpers
{
    /// <summary>
    /// Strips HTML tags from a string.
    /// </summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        // Remove HTML tags
        var text = HtmlTagRegex().Replace(html, string.Empty);

        // Replace HTML entities
        text = text.Replace("&nbsp;", " ")
                   .Replace("&#160;", " ")
                   .Replace("&amp;", "&")
                   .Replace("&lt;", "<")
                   .Replace("&gt;", ">")
                   .Replace("&quot;", "\"")
                   .Replace("&#39;", "'");

        // Collapse multiple whitespace
        text = WhitespaceRegex().Replace(text, " ");

        return text.Trim();
    }

    /// <summary>
    /// Formats an ISO time string to a readable format (MM/dd HH:mm).
    /// </summary>
    public static string FormatTime(string? isoTime)
    {
        if (string.IsNullOrEmpty(isoTime))
        {
            return string.Empty;
        }

        try
        {
            // Handle 'Z' suffix
            var normalized = isoTime.Replace("Z", "+00:00");

            // Handle fractional seconds with varying precision
            if (normalized.Contains('.'))
            {
                var dotIndex = normalized.IndexOf('.');
                var tzIndex = normalized.IndexOfAny(['+', '-'], dotIndex);
                if (tzIndex > 0)
                {
                    var frac = normalized[(dotIndex + 1)..tzIndex];
                    var normalizedFrac = frac.PadRight(7, '0')[..7]; // Ensure 7 digits for ticks
                    normalized = normalized[..dotIndex] + "." + normalizedFrac + normalized[tzIndex..];
                }
            }

            if (DateTimeOffset.TryParse(normalized, out var dt))
            {
                return dt.ToString("MM/dd HH:mm");
            }

            // Fallback: return first 16 chars
            return isoTime.Length > 16 ? isoTime[..16] : isoTime;
        }
        catch
        {
            return isoTime.Length > 16 ? isoTime[..16] : isoTime;
        }
    }

    /// <summary>
    /// Truncates a string to the specified length with ellipsis.
    /// </summary>
    public static string Truncate(string? text, int maxLength = 120)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 3)] + "...";
    }

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
