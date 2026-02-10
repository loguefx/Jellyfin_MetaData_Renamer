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
        return RenderMovieOrSeriesFolder(format, name, year, providerLabel, id);
    }

    /// <summary>
    /// Renders a movie folder name using the specified format template.
    /// </summary>
    /// <param name="format">The format template string.</param>
    /// <param name="name">The movie name.</param>
    /// <param name="year">The production year (nullable - if null, year part will be removed from format).</param>
    /// <param name="providerLabel">The provider label (e.g., tmdb, imdb).</param>
    /// <param name="id">The provider ID.</param>
    /// <returns>The formatted and sanitized folder name.</returns>
    public static string RenderMovieFolder(string format, string name, int? year, string providerLabel, string id)
    {
        return RenderMovieOrSeriesFolder(format, name, year, providerLabel, id);
    }

    /// <summary>
    /// Shared implementation for rendering movie or series folder names.
    /// </summary>
    /// <param name="format">The format template string.</param>
    /// <param name="name">The item name.</param>
    /// <param name="year">The production year (nullable - if null, year part will be removed from format).</param>
    /// <param name="providerLabel">The provider label (e.g., tvdb, tmdb, imdb).</param>
    /// <param name="id">The provider ID.</param>
    /// <returns>The formatted and sanitized folder name.</returns>
    private static string RenderMovieOrSeriesFolder(string format, string name, int? year, string providerLabel, string id)
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
            // Use MatchEvaluator to properly capture the padding value
            // The padding value (e.g., "00" in {Season:00}) represents the minimum width
            // "00" means pad to at least 2 digits, "000" means pad to at least 3 digits, etc.
            s = Regex.Replace(s, @"{Season:(\d+)}", m =>
            {
                var paddingStr = m.Groups[1].Value;
                // The padding string length tells us the minimum width
                // "00" has length 2, so pad to 2 digits; "000" has length 3, so pad to 3 digits
                var paddingWidth = paddingStr.Length;
                // Ensure minimum padding of 1 to avoid D0 format (which doesn't pad)
                if (paddingWidth < 1)
                {
                    paddingWidth = 1;
                }
                // Use the padding width to format the number (e.g., D2 for 2-digit padding)
                return seasonNumber.Value.ToString($"D{paddingWidth}", CultureInfo.InvariantCulture);
            }, RegexOptions.IgnoreCase);
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
            // Use MatchEvaluator to properly capture the padding value
            s = Regex.Replace(s, @"{Season:(\d+)}", m =>
            {
                var paddingStr = m.Groups[1].Value;
                // The padding string length tells us the minimum width
                // "00" has length 2, so pad to 2 digits; "000" has length 3, so pad to 3 digits
                var paddingWidth = paddingStr.Length;
                // Ensure minimum padding of 1 to avoid D0 format (which doesn't pad)
                if (paddingWidth < 1)
                {
                    paddingWidth = 1;
                }
                // Use the padding width to format the number (e.g., D2 for 2-digit padding)
                return seasonNumber.Value.ToString($"D{paddingWidth}", CultureInfo.InvariantCulture);
            }, RegexOptions.IgnoreCase);
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
                var paddingStr = m.Groups[1].Value;
                // The padding string length tells us the minimum width
                // "00" has length 2, so pad to 2 digits; "000" has length 3, so pad to 3 digits
                var paddingWidth = paddingStr.Length;
                // Ensure minimum padding of 1 to avoid D0 format (which doesn't pad)
                if (paddingWidth < 1)
                {
                    paddingWidth = 1;
                }
                // Use the padding width to format the number (e.g., D2 for 2-digit padding)
                return episodeNumber.Value.ToString($"D{paddingWidth}", CultureInfo.InvariantCulture);
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

    /// <summary>
    /// Extracts a clean episode title from episode.Name by removing filename patterns.
    /// Removes patterns like "S02E06 - ", repeated patterns, and series name patterns.
    /// </summary>
    /// <param name="episodeName">The episode name from Jellyfin metadata (may contain filename patterns).</param>
    /// <param name="seasonNumber">The season number (optional, used for pattern detection).</param>
    /// <param name="episodeNumber">The episode number (optional, used for pattern detection).</param>
    /// <returns>The cleaned episode title, or empty string if no clean title can be extracted.</returns>
    public static string ExtractCleanEpisodeTitle(string episodeName, int? seasonNumber, int? episodeNumber)
    {
        if (string.IsNullOrWhiteSpace(episodeName))
        {
            return string.Empty;
        }

        var title = episodeName.Trim();

        // Remove repeated patterns like "S02E06 - S02E06 - S02E06 - ..."
        // This pattern can repeat multiple times
        var repeatedPattern = new Regex(@"^([Ss]\d+[Ee]\d+\s*-\s*)+", RegexOptions.IgnoreCase);
        title = repeatedPattern.Replace(title, string.Empty);

        // Remove S##E## - pattern from beginning (case-insensitive, flexible spacing)
        // Pattern: S{season}E{episode} - or S{season}E{episode}-
        if (seasonNumber.HasValue && episodeNumber.HasValue)
        {
            // Try exact match first
            var exactPattern = new Regex($@"^[Ss]{seasonNumber.Value}[Ee]{episodeNumber.Value}\s*-\s*", RegexOptions.IgnoreCase);
            title = exactPattern.Replace(title, string.Empty);

            // Also try zero-padded versions
            var paddedPattern = new Regex($@"^[Ss]{seasonNumber.Value:D2}[Ee]{episodeNumber.Value:D2}\s*-\s*", RegexOptions.IgnoreCase);
            title = paddedPattern.Replace(title, string.Empty);
        }

        // Remove generic S##E## - pattern (without specific numbers) from beginning
        var genericPattern = new Regex(@"^[Ss]\d+[Ee]\d+\s*-\s*", RegexOptions.IgnoreCase);
        title = genericPattern.Replace(title, string.Empty);

        // CRITICAL FIX: Remove embedded S##E## patterns (e.g., "One Piece_S10E575_", "SeriesName_S01E05")
        // These patterns can appear anywhere in the title, not just at the beginning
        // Pattern: SeriesName_S##E##_ or SeriesName_S##E## or _S##E##_ or _S##E##
        var embeddedSeasonEpisodePattern = new Regex(@"[_\s]+[Ss]\d+[Ee]\d+[_\s]*", RegexOptions.IgnoreCase);
        title = embeddedSeasonEpisodePattern.Replace(title, " ");

        // Remove series name + "Season X Dub Episode Y" patterns
        // Pattern: "Series Name Season X Dub Episode Y" or similar
        var seriesSeasonPattern = new Regex(@"\s*Season\s+\d+\s+Dub\s+Episode\s+\d+.*$", RegexOptions.IgnoreCase);
        title = seriesSeasonPattern.Replace(title, string.Empty);

        // Remove "Episode X" or "Ep X" patterns at the end
        var episodePattern = new Regex(@"\s*[Ee]pisode\s+\d+.*$", RegexOptions.IgnoreCase);
        title = episodePattern.Replace(title, string.Empty);

        // Clean up any remaining separators at the beginning
        title = Regex.Replace(title, @"^[\s\-–—]+", string.Empty);

        // Clean up any remaining separators at the end
        title = Regex.Replace(title, @"[\s\-–—]+$", string.Empty);

        // Collapse multiple spaces
        title = CollapseSpaces(title);

        return title;
    }

    /// <summary>
    /// Normalizes a filename for comparison by removing extension, normalizing whitespace, and converting to lowercase.
    /// </summary>
    /// <param name="fileName">The filename to normalize.</param>
    /// <returns>The normalized filename.</returns>
    public static string NormalizeFileNameForComparison(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        // Remove file extension
        var withoutExt = Path.GetFileNameWithoutExtension(fileName);

        // Normalize whitespace
        withoutExt = CollapseSpaces(withoutExt);

        // Convert to lowercase for case-insensitive comparison
        withoutExt = withoutExt.ToLowerInvariant();

        return withoutExt;
    }

    /// <summary>
    /// Compares two filenames to determine if they match, accounting for minor variations.
    /// </summary>
    /// <param name="current">The current filename.</param>
    /// <param name="desired">The desired filename.</param>
    /// <returns>True if the filenames match (after normalization), false otherwise.</returns>
    public static bool DoFilenamesMatch(string current, string desired)
    {
        if (string.IsNullOrWhiteSpace(current) && string.IsNullOrWhiteSpace(desired))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(desired))
        {
            return false;
        }

        var normalizedCurrent = NormalizeFileNameForComparison(current);
        var normalizedDesired = NormalizeFileNameForComparison(desired);

        return string.Equals(normalizedCurrent, normalizedDesired, StringComparison.Ordinal);
    }

    private static string CollapseSpaces(string s)
        => Regex.Replace(s ?? string.Empty, @"\s+", " ").Trim();
}
