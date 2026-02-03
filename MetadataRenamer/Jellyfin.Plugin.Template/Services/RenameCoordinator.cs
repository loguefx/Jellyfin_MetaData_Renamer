using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MetadataRenamer.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MetadataRenamer.Services;

/// <summary>
/// Coordinates series folder renaming based on metadata updates.
/// </summary>
public class RenameCoordinator
{
    private readonly ILogger<RenameCoordinator> _logger;
    private readonly PathRenameService _pathRenamer;
    private readonly TimeSpan _globalMinInterval = TimeSpan.FromSeconds(2);
    private readonly Dictionary<Guid, DateTime> _lastAttemptUtcByItem = new();
    private readonly Dictionary<Guid, string> _providerHashByItem = new();

    // global debounce
    private DateTime _lastGlobalActionUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="RenameCoordinator"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="pathRenamer">The path rename service.</param>
    public RenameCoordinator(ILogger<RenameCoordinator> logger, PathRenameService pathRenamer)
    {
        _logger = logger;
        _pathRenamer = pathRenamer;
    }

    /// <summary>
    /// Clears all internal state to help with plugin unloading.
    /// </summary>
    public void ClearState()
    {
        try
        {
            _lastAttemptUtcByItem.Clear();
            _providerHashByItem.Clear();
            _lastGlobalActionUtc = DateTime.MinValue;
            _logger?.LogInformation("[MR] RenameCoordinator state cleared");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MR] Error clearing RenameCoordinator state: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Handles item update events and triggers renaming if conditions are met.
    /// </summary>
    /// <param name="e">The item change event arguments.</param>
    /// <param name="cfg">The plugin configuration.</param>
    public void HandleItemUpdated(ItemChangeEventArgs e, PluginConfiguration cfg)
    {
        try
        {
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "C", location = "RenameCoordinator.cs:42", message = "HandleItemUpdated entry", data = new { itemType = e.Item?.GetType().Name ?? "null", itemName = e.Item?.Name ?? "null", enabled = cfg.Enabled, renameSeriesFolders = cfg.RenameSeriesFolders, dryRun = cfg.DryRun, requireProviderIdMatch = cfg.RequireProviderIdMatch, onlyRenameWhenProviderIdsChange = cfg.OnlyRenameWhenProviderIdsChange }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            
            // Log all item updates with full details
            _logger.LogInformation("[MR] ===== ItemUpdated Event Received =====");
            _logger.LogInformation("[MR] Item Type: {Type}", e.Item?.GetType().Name ?? "NULL");
            _logger.LogInformation("[MR] Item Name: {Name}", e.Item?.Name ?? "NULL");
            _logger.LogInformation("[MR] Item ID: {Id}", e.Item?.Id.ToString() ?? "NULL");
            _logger.LogInformation(
                "[MR] Configuration: Enabled={Enabled}, RenameSeriesFolders={RenameSeriesFolders}, DryRun={DryRun}, RequireProviderIdMatch={RequireProviderIdMatch}, OnlyRenameWhenProviderIdsChange={OnlyRenameWhenProviderIdsChange}",
                cfg.Enabled, cfg.RenameSeriesFolders, cfg.DryRun, cfg.RequireProviderIdMatch, cfg.OnlyRenameWhenProviderIdsChange);

            if (!cfg.Enabled)
            {
                // #region agent log
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "RenameCoordinator.cs:49", message = "Plugin disabled", data = new { itemType = e.Item?.GetType().Name ?? "null" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                // #endregion
                _logger.LogWarning("[MR] SKIP: Plugin is disabled in configuration");
                return;
            }

            if (!cfg.RenameSeriesFolders)
            {
                _logger.LogWarning("[MR] SKIP: RenameSeriesFolders is disabled in configuration");
                return;
            }

            // Debounce global spam
            var now = DateTime.UtcNow;
            var timeSinceLastAction = now - _lastGlobalActionUtc;
            if (timeSinceLastAction < _globalMinInterval)
            {
                _logger.LogInformation("[MR] SKIP: Global debounce active. Time since last action: {Seconds} seconds (min: {MinSeconds})",
                    timeSinceLastAction.TotalSeconds, _globalMinInterval.TotalSeconds);
                return;
            }

            _lastGlobalActionUtc = now;

            // Handle Series items
            if (e.Item is Series series)
            {
                HandleSeriesUpdate(series, cfg, now);
                return;
            }

            // Handle Episode items
            if (e.Item is Episode episode && cfg.RenameEpisodeFiles)
            {
                HandleEpisodeUpdate(episode, cfg, now);
                return;
            }

            // Skip other item types
            _logger.LogInformation("[MR] SKIP: Item is not a Series or Episode. Type={Type}, Name={Name}", e.Item?.GetType().Name ?? "NULL", e.Item?.Name ?? "NULL");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] CRITICAL ERROR in HandleItemUpdated: {Message}", ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "RenameCoordinator.cs:252", message = "CRITICAL ERROR in HandleItemUpdated", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
        }
    }

    /// <summary>
    /// Handles series folder renaming.
    /// </summary>
    private void HandleSeriesUpdate(Series series, PluginConfiguration cfg, DateTime now)
    {
        _logger.LogInformation("[MR] Processing Series: Name={Name}, Id={Id}, Path={Path}", series.Name, series.Id, series.Path);

            // Per-item cooldown
            if (_lastAttemptUtcByItem.TryGetValue(series.Id, out var lastTry))
            {
                var timeSinceLastTry = (now - lastTry).TotalSeconds;
                if (timeSinceLastTry < cfg.PerItemCooldownSeconds)
                {
                _logger.LogInformation(
                    "[MR] SKIP: Cooldown active. SeriesId={Id}, Name={Name}, Time since last try: {Seconds} seconds (cooldown: {CooldownSeconds})",
                    series.Id, series.Name, timeSinceLastTry.ToString(System.Globalization.CultureInfo.InvariantCulture), cfg.PerItemCooldownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }
            }

            _lastAttemptUtcByItem[series.Id] = now;

            var path = series.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                _logger.LogWarning("[MR] SKIP: Series has no path. SeriesId={Id}, Name={Name}", series.Id, series.Name);
                return;
            }

            if (!Directory.Exists(path))
            {
                _logger.LogWarning("[MR] SKIP: Series path does not exist on disk. Path={Path}, SeriesId={Id}, Name={Name}", path, series.Id, series.Name);
                return;
            }

            _logger.LogInformation("[MR] Series path verified: {Path}", path);

            // Check provider IDs
            var providerIdsCount = series.ProviderIds?.Count ?? 0;
            var providerIdsString = series.ProviderIds != null 
                ? string.Join(", ", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")) 
                : "NONE";
            
            _logger.LogInformation("[MR] Provider IDs: Count={Count}, Values={Values}", providerIdsCount, providerIdsString);

            // Must be matched
            if (cfg.RequireProviderIdMatch)
            {
                if (series.ProviderIds == null || series.ProviderIds.Count == 0)
                {
                    _logger.LogWarning("[MR] SKIP: RequireProviderIdMatch is true but no ProviderIds found. Name={Name}", series.Name);
                    return;
                }
                _logger.LogInformation("[MR] Provider ID requirement satisfied");
            }

            var name = series.Name?.Trim();
            
            // Try ProductionYear first, then PremiereDate as fallback
            int? year = series.ProductionYear;
            string yearSource = "ProductionYear";
            
            if (year is null && series.PremiereDate.HasValue)
            {
                year = series.PremiereDate.Value.Year;
                yearSource = "PremiereDate";
            }

            _logger.LogInformation("[MR] Series metadata: Name={Name}, Year={Year} (from {YearSource}), ProviderIds={ProviderIds}",
                name ?? "NULL", 
                year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", 
                yearSource,
                providerIdsString);

            if (string.IsNullOrWhiteSpace(name))
            {
                _logger.LogWarning("[MR] SKIP: Missing required metadata. Name={Name}", name ?? "NULL");
                return;
            }
            
            // Year is now optional - we'll handle it in the format rendering

            // Identify inference: only rename when provider ids changed
            if (cfg.OnlyRenameWhenProviderIdsChange)
            {
                var hasProviderIds = series.ProviderIds != null && series.ProviderIds.Count > 0;
                var newHash = hasProviderIds && series.ProviderIds != null
                    ? ProviderIdHelper.ComputeProviderHash(series.ProviderIds)
                    : string.Empty;
                
                var hasOldHash = _providerHashByItem.TryGetValue(series.Id, out var oldHash);

                // #region agent log
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "B", location = "RenameCoordinator.cs:124", message = "Provider hash check", data = new { oldHash = oldHash ?? "(none)", newHash = newHash, hasOldHash = hasOldHash, hasProviderIds = hasProviderIds, seriesName = name, providerIds = series.ProviderIds != null ? string.Join(",", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")) : "null" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                // #endregion

                _logger.LogInformation("[MR] === Provider Hash Check ===");
                _logger.LogInformation("[MR] Has Provider IDs: {HasProviderIds}", hasProviderIds);
                _logger.LogInformation("[MR] Old Hash Exists: {HasOldHash}, Value: {OldHash}", hasOldHash, oldHash ?? "(none)");
                _logger.LogInformation("[MR] New Hash: {NewHash}", newHash);
                _logger.LogInformation("[MR] Series: {Name}", name);

                // If we have an old hash and it matches the new hash, skip (no change)
                if (hasOldHash && string.Equals(newHash, oldHash, StringComparison.Ordinal))
                {
                    // #region agent log
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "B", location = "RenameCoordinator.cs:138", message = "SKIP: ProviderIds unchanged", data = new { seriesName = name, hash = newHash }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    // #endregion
                    _logger.LogWarning("[MR] SKIP: Provider IDs unchanged. OnlyRenameWhenProviderIdsChange is enabled. Name={Name}, Hash={Hash}", name, newHash);
                    _logger.LogInformation("[MR] ===== Processing Complete (Skipped) =====");
                    return;
                }

                // Allow rename if:
                // 1. No old hash exists (first time processing this series)
                // 2. Hash changed (provider IDs changed)
                _logger.LogInformation("[MR] ✓ Provider IDs changed or first time processing. Allowing rename.");
                _logger.LogInformation("[MR] Old Hash: {OldHash}, New Hash: {NewHash}", oldHash ?? "(none)", newHash);
                _providerHashByItem[series.Id] = newHash;
            }
            else
            {
                _logger.LogInformation("[MR] OnlyRenameWhenProviderIdsChange is disabled - proceeding with rename");
            }

            // Handle provider IDs - use if available, otherwise use empty values
            string providerLabel = string.Empty;
            string providerId = string.Empty;

            if (series.ProviderIds != null && series.ProviderIds.Count > 0)
            {
                var preferredProviders = cfg.PreferredSeriesProviders != null
                    ? string.Join(", ", cfg.PreferredSeriesProviders)
                    : "NONE";
                _logger.LogInformation("[MR] Preferred Providers: {Providers}", preferredProviders);

                var preferredList = cfg.PreferredSeriesProviders != null
                    ? cfg.PreferredSeriesProviders
                    : new System.Collections.ObjectModel.Collection<string>();
                var best = ProviderIdHelper.GetBestProvider(series.ProviderIds, preferredList);
                if (best != null)
                {
                    providerLabel = best.Value.ProviderLabel;
                    providerId = best.Value.Id;
                    _logger.LogInformation("[MR] Selected Provider: {Provider}={Id}", providerLabel, providerId);
                }
                else
                {
                    _logger.LogWarning("[MR] No matching provider found in preferred list");
                }
            }
            else if (cfg.RequireProviderIdMatch)
            {
                // If we require provider IDs but don't have any, skip renaming
                _logger.LogWarning("[MR] SKIP: RequireProviderIdMatch is true but no ProviderIds found. Name={Name}", name);
                _logger.LogInformation("[MR] ===== Processing Complete (Skipped) =====");
                return;
            }
            else
            {
                // No provider IDs but we don't require them - rename anyway to help with identification
                _logger.LogInformation("[MR] No ProviderIds but RequireProviderIdMatch is false - renaming to help identification");
            }

            // Build final desired folder name: Name (Year) [provider-id] or Name (Year) if no provider IDs
            var currentFolderName = Path.GetFileName(path);
            var desiredFolderName = SafeName.RenderSeriesFolder(
                cfg.SeriesFolderFormat,
                name,
                year,
                providerLabel,
                providerId);
            
            _logger.LogInformation("[MR] Desired folder name: {DesiredName} (year available: {HasYear})", desiredFolderName, year.HasValue);

            _logger.LogInformation("[MR] === Folder Rename Details ===");
            _logger.LogInformation("[MR] Current Folder: {Current}", currentFolderName);
            _logger.LogInformation("[MR] Desired Folder: {Desired}", desiredFolderName);
            _logger.LogInformation("[MR] Full Current Path: {Path}", path);
            _logger.LogInformation("[MR] Format: {Format}", cfg.SeriesFolderFormat);
            _logger.LogInformation("[MR] Dry Run Mode: {DryRun}", cfg.DryRun);

            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "RenameCoordinator.cs:186", message = "Attempting rename", data = new { seriesName = name, currentPath = path, desiredFolderName = desiredFolderName, dryRun = cfg.DryRun }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion

            _pathRenamer.TryRenameSeriesFolder(series, desiredFolderName, cfg.DryRun);

            _logger.LogInformation("[MR] ===== Processing Complete =====");
    }

    /// <summary>
    /// Handles episode file renaming.
    /// </summary>
    private void HandleEpisodeUpdate(Episode episode, PluginConfiguration cfg, DateTime now)
    {
        try
        {
            _logger.LogInformation("[MR] Processing Episode: Name={Name}, Id={Id}, Path={Path}", episode.Name, episode.Id, episode.Path);

            // Per-item cooldown
            if (_lastAttemptUtcByItem.TryGetValue(episode.Id, out var lastTry))
            {
                var timeSinceLastTry = (now - lastTry).TotalSeconds;
                if (timeSinceLastTry < cfg.PerItemCooldownSeconds)
                {
                    _logger.LogInformation(
                        "[MR] SKIP: Cooldown active. EpisodeId={Id}, Name={Name}, Time since last try: {Seconds} seconds (cooldown: {CooldownSeconds})",
                        episode.Id, episode.Name, timeSinceLastTry.ToString(System.Globalization.CultureInfo.InvariantCulture), cfg.PerItemCooldownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }
            }

            _lastAttemptUtcByItem[episode.Id] = now;

            var path = episode.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                _logger.LogWarning("[MR] SKIP: Episode has no path. EpisodeId={Id}, Name={Name}", episode.Id, episode.Name);
                return;
            }

            if (!File.Exists(path))
            {
                _logger.LogWarning("[MR] SKIP: Episode file does not exist on disk. Path={Path}, EpisodeId={Id}, Name={Name}", path, episode.Id, episode.Name);
                return;
            }

            _logger.LogInformation("[MR] Episode file path verified: {Path}", path);

            // Get episode metadata
            var episodeTitle = episode.Name?.Trim() ?? string.Empty;
            var seasonNumber = episode.ParentIndexNumber; // Season number
            var episodeNumber = episode.IndexNumber; // Episode number
            var seriesName = episode.SeriesName?.Trim() ?? string.Empty;
            
            // Get year from series if available
            int? year = null;
            if (episode.Series != null)
            {
                year = episode.Series.ProductionYear;
                if (year is null && episode.Series.PremiereDate.HasValue)
                {
                    year = episode.Series.PremiereDate.Value.Year;
                }
            }

            _logger.LogInformation("[MR] Episode metadata: Series={Series}, Season={Season}, Episode={Episode}, Title={Title}, Year={Year}",
                seriesName, seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                episodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                episodeTitle, year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");

            if (string.IsNullOrWhiteSpace(episodeTitle))
            {
                _logger.LogWarning("[MR] SKIP: Episode title is missing. EpisodeId={Id}", episode.Id);
                return;
            }

            if (!seasonNumber.HasValue || !episodeNumber.HasValue)
            {
                _logger.LogWarning("[MR] SKIP: Episode missing season or episode number. Season={Season}, Episode={Episode}", 
                    seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                    episodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                return;
            }

            // Build desired file name (without extension)
            var currentFileName = Path.GetFileNameWithoutExtension(path);
            var fileExtension = Path.GetExtension(path);
            var desiredFileName = SafeName.RenderEpisodeFileName(
                cfg.EpisodeFileFormat,
                seriesName,
                seasonNumber,
                episodeNumber,
                episodeTitle,
                year);

            _logger.LogInformation("[MR] === Episode File Rename Details ===");
            _logger.LogInformation("[MR] Current File: {Current}{Extension}", currentFileName, fileExtension);
            _logger.LogInformation("[MR] Desired File: {Desired}{Extension}", desiredFileName, fileExtension);
            _logger.LogInformation("[MR] Format: {Format}", cfg.EpisodeFileFormat);
            _logger.LogInformation("[MR] Dry Run Mode: {DryRun}", cfg.DryRun);

            _pathRenamer.TryRenameEpisodeFile(episode, desiredFileName, fileExtension, cfg.DryRun);

            _logger.LogInformation("[MR] ===== Episode Processing Complete =====");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] ERROR in HandleEpisodeUpdate: {Message}", ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
        }
    }
}
