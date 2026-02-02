using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetadataRenamer.Services;

/// <summary>
/// Helper class for safe name formatting and filesystem sanitization.
/// </summary>
public static class SafeName
{
    /// <summary>
    /// Renders a series folder name using the specified format template.
    /// </summary>
    /// <param name="format">The format template string.</param>
    /// <param name="name">The series name.</param>
    /// <param name="year">The production year.</param>
    /// <param name="providerLabel">The provider label (e.g., tvdb, tmdb).</param>
    /// <param name="id">The provider ID.</param>
    /// <returns>The formatted and sanitized folder name.</returns>
    public static string RenderSeriesFolder(string format, string name, int year, string providerLabel, string id)
    {
        var s = (format ?? "{Name} ({Year}) [{Provider}-{Id}]")
            .Replace("{Name}", name ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{Year}", year.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{Provider}", providerLabel ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{Id}", id ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        s = CollapseSpaces(s);
        return SanitizeFileName(s);
    }

    /// <summary>
    /// Sanitizes a filename by removing invalid filesystem characters.
    /// </summary>
    /// <param name="input">The input string to sanitize.</param>
    /// <returns>The sanitized filename.</returns>
    public static string SanitizeFileName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "Unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(input.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());

        // Windows: no trailing dots/spaces
        cleaned = cleaned.Trim().TrimEnd('.', ' ');

        cleaned = CollapseSpaces(cleaned);

        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }

    private static string CollapseSpaces(string s)
        => Regex.Replace(s ?? string.Empty, @"\s+", " ").Trim();
}
