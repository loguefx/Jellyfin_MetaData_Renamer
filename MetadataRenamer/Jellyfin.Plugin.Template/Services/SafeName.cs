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
    /// <param name="year">The production year (nullable - if null, year part will be removed from format).</param>
    /// <param name="providerLabel">The provider label (e.g., tvdb, tmdb).</param>
    /// <param name="id">The provider ID.</param>
    /// <returns>The formatted and sanitized folder name.</returns>
    public static string RenderSeriesFolder(string format, string name, int? year, string providerLabel, string id)
    {
        var hasProviderId = !string.IsNullOrWhiteSpace(providerLabel) && !string.IsNullOrWhiteSpace(id);
        var hasYear = year.HasValue;

        var s = format ?? "{Name} ({Year}) [{Provider}-{Id}]";
        
        // Replace name
        s = s.Replace("{Name}", name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        
        // Replace year if available, otherwise remove year-related parts
        if (hasYear)
        {
            s = s.Replace("{Year}", year.Value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Remove year-related placeholders and surrounding formatting
            s = Regex.Replace(s, @"\s*\(\s*{Year}\s*\)", string.Empty, RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*-\s*{Year}", string.Empty, RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*{Year}\s*", string.Empty, RegexOptions.IgnoreCase);
        }
        
        // Replace provider and ID
        s = s.Replace("{Provider}", providerLabel ?? string.Empty, StringComparison.OrdinalIgnoreCase)
             .Replace("{Id}", id ?? string.Empty, StringComparison.OrdinalIgnoreCase)
             .Trim();

        // Clean up empty provider brackets if no provider ID
        if (!hasProviderId)
        {
            // Remove empty brackets like " []" or "[-]"
            s = Regex.Replace(s, @"\s*\[\s*[-]?\s*\]", string.Empty, RegexOptions.IgnoreCase);
            // Remove trailing separator if it exists
            s = Regex.Replace(s, @"\s*-\s*$", string.Empty);
        }

        s = CollapseSpaces(s);
        return SanitizeFileName(s);
    }

    /// <summary>
    /// Renders a season folder name using the specified format template.
    /// </summary>
    /// <param name="format">The format template string.</param>
    /// <param name="seasonNumber">The season number (nullable).</param>
    /// <param name="seasonName">The season name (nullable).</param>
    /// <returns>The formatted and sanitized folder name.</returns>
    public static string RenderSeasonFolder(string format, int? seasonNumber, string seasonName)
    {
        var s = format ?? "Season {Season:00}";
        
        // Replace season name if available
        if (!string.IsNullOrWhiteSpace(seasonName))
        {
            s = s.Replace("{SeasonName}", seasonName, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Remove season name placeholder if not available
            s = Regex.Replace(s, @"{SeasonName}", string.Empty, RegexOptions.IgnoreCase);
        }
        
        // Replace season number
        if (seasonNumber.HasValue)
        {
            // Handle format specifiers like {Season:00} for zero-padding
            s = Regex.Replace(s, @"{Season:(\d+)}", seasonNumber.Value.ToString($"D$1", CultureInfo.InvariantCulture), RegexOptions.IgnoreCase);
            s = s.Replace("{Season}", seasonNumber.Value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Remove season-related placeholders
            s = Regex.Replace(s, @"{Season:?\d*}", string.Empty, RegexOptions.IgnoreCase);
        }
        
        s = CollapseSpaces(s);
        return SanitizeFileName(s);
    }

    /// <summary>
    /// Renders an episode file name using the specified format template.
    /// </summary>
    /// <param name="format">The format template string.</param>
    /// <param name="seriesName">The series name.</param>
    /// <param name="seasonNumber">The season number (nullable).</param>
    /// <param name="episodeNumber">The episode number (nullable).</param>
    /// <param name="episodeTitle">The episode title.</param>
    /// <param name="year">The production year (nullable).</param>
    /// <returns>The formatted and sanitized file name (without extension).</returns>
    public static string RenderEpisodeFileName(string format, string seriesName, int? seasonNumber, int? episodeNumber, string episodeTitle, int? year)
    {
        var s = format ?? "S{Season:00}E{Episode:00} - {Title}";
        
        // Replace series name
        s = s.Replace("{SeriesName}", seriesName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        
        // Replace season number
        if (seasonNumber.HasValue)
        {
            // Handle format specifiers like {Season:00} for zero-padding
            s = Regex.Replace(s, @"{Season:(\d+)}", seasonNumber.Value.ToString($"D$1", CultureInfo.InvariantCulture), RegexOptions.IgnoreCase);
            s = s.Replace("{Season}", seasonNumber.Value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Remove season-related placeholders
            s = Regex.Replace(s, @"{Season:?\d*}", string.Empty, RegexOptions.IgnoreCase);
        }
        
        // Replace episode number
        if (episodeNumber.HasValue)
        {
            // Handle format specifiers like {Episode:00} for zero-padding
            // First replace {Episode:XX} patterns (e.g., {Episode:00} -> 09)
            s = Regex.Replace(s, @"{Episode:(\d+)}", m =>
            {
                var padding = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                return episodeNumber.Value.ToString($"D{padding}", CultureInfo.InvariantCulture);
            }, RegexOptions.IgnoreCase);
            // Then replace simple {Episode} placeholder
            s = s.Replace("{Episode}", episodeNumber.Value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Remove episode-related placeholders - need to match both {Episode:XX} and {Episode}
            // Match {Episode:XX} first (with colon and digits)
            s = Regex.Replace(s, @"{Episode:\d+}", string.Empty, RegexOptions.IgnoreCase);
            // Then match simple {Episode}
            s = Regex.Replace(s, @"{Episode}", string.Empty, RegexOptions.IgnoreCase);
        }
        
        // Replace episode title
        s = s.Replace("{Title}", episodeTitle ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        
        // Replace year if available
        if (year.HasValue)
        {
            s = s.Replace("{Year}", year.Value.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Remove year-related placeholders
            s = Regex.Replace(s, @"\s*\(\s*{Year}\s*\)", string.Empty, RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*-\s*{Year}", string.Empty, RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*{Year}\s*", string.Empty, RegexOptions.IgnoreCase);
        }
        
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

    /// <summary>
    /// Attempts to parse an episode number from a filename.
    /// Supports common patterns: "EP 1", "episode 1", "E01", "S01E01", "01", etc.
    /// </summary>
    /// <param name="fileName">The filename (without extension) to parse.</param>
    /// <returns>The episode number if found, null otherwise.</returns>
    public static int? ParseEpisodeNumberFromFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // Try various patterns in order of specificity
        // Pattern 1: S##E## or s##e## (e.g., "S01E01", "s1e5")
        var match = Regex.Match(fileName, @"[Ss](\d+)[Ee](\d+)", RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count >= 3)
        {
            if (int.TryParse(match.Groups[2].Value, out var epNum))
            {
                return epNum;
            }
        }

        // Pattern 2: E## or e## (e.g., "E01", "e5")
        match = Regex.Match(fileName, @"[Ee](\d+)", RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count >= 2)
        {
            if (int.TryParse(match.Groups[1].Value, out var epNum))
            {
                return epNum;
            }
        }

        // Pattern 3: EP ## or ep ## or EP## or ep## (e.g., "EP 1", "ep 5", "EP01", "ep5")
        match = Regex.Match(fileName, @"[Ee][Pp]\s*(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count >= 2)
        {
            if (int.TryParse(match.Groups[1].Value, out var epNum))
            {
                return epNum;
            }
        }

        // Pattern 4: Episode ## or episode ## or Episode## or episode## (e.g., "Episode 1", "episode 5")
        match = Regex.Match(fileName, @"[Ee]pisode\s*(\d+)", RegexOptions.IgnoreCase);
        if (match.Success && match.Groups.Count >= 2)
        {
            if (int.TryParse(match.Groups[1].Value, out var epNum))
            {
                return epNum;
            }
        }

        // Pattern 5: Just a number at the end or in the middle (e.g., "Angel Beats - 1", "Show 01")
        // Look for standalone numbers, prefer numbers at the end
        match = Regex.Match(fileName, @"\b(\d{1,3})\b", RegexOptions.RightToLeft);
        if (match.Success && match.Groups.Count >= 2)
        {
            if (int.TryParse(match.Groups[1].Value, out var epNum) && epNum > 0 && epNum <= 999)
            {
                return epNum;
            }
        }

        return null;
    }

    private static string CollapseSpaces(string s)
        => Regex.Replace(s ?? string.Empty, @"\s+", " ").Trim();
}
