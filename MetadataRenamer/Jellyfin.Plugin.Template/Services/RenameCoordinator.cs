using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetadataRenamer.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.MetadataRenamer.Services;

/// <summary>
/// Coordinates series folder renaming based on metadata updates.
/// </summary>
public class RenameCoordinator
{
    private readonly ILogger<RenameCoordinator> _logger;
    private readonly PathRenameService _pathRenamer;
    private readonly ILibraryManager? _libraryManager;
    private readonly TimeSpan _globalMinInterval = TimeSpan.FromSeconds(2);
    private readonly Dictionary<Guid, DateTime> _lastAttemptUtcByItem = new();
    private readonly Dictionary<Guid, string> _providerHashByItem = new();
    private readonly Dictionary<Guid, Dictionary<string, string>> _previousProviderIdsByItem = new();

    // Episode retry queue for episodes with incomplete metadata
    private readonly Dictionary<Guid, DateTime> _episodeRetryQueue = new(); // Episode ID -> Last retry attempt
    private readonly Dictionary<Guid, int> _episodeRetryCount = new(); // Episode ID -> Retry count
    private readonly Dictionary<Guid, string> _episodeRetryReason = new(); // Episode ID -> Reason for queuing
    private const int MaxRetryAttempts = 10;
    private const int RetryDelayMinutes = 5;

    // Track series that have been processed for episodes
    private readonly HashSet<Guid> _seriesProcessedForEpisodes = new();

    // global debounce
    private DateTime _lastGlobalActionUtc = DateTime.MinValue;

    // Track series updates for detecting "Replace all metadata" (bulk refresh)
    private readonly Queue<DateTime> _seriesUpdateTimestamps = new(); // Timestamps of recent series updates
    private DateTime _lastBulkProcessingUtc = DateTime.MinValue;
    private const int BulkUpdateThreshold = 5; // Number of series updates in time window to trigger bulk processing
    private const int BulkUpdateTimeWindowSeconds = 10; // Time window for detecting bulk updates
    private const int BulkProcessingCooldownMinutes = 5; // Cooldown between bulk processing runs

    /// <summary>
    /// Initializes a new instance of the <see cref="RenameCoordinator"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="pathRenamer">The path rename service.</param>
    /// <param name="libraryManager">The library manager instance (optional, used for processing all episodes).</param>
    public RenameCoordinator(ILogger<RenameCoordinator> logger, PathRenameService pathRenamer, ILibraryManager? libraryManager = null)
    {
        _logger = logger;
        _pathRenamer = pathRenamer;
        _libraryManager = libraryManager;
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
            _previousProviderIdsByItem.Clear();
            _episodeRetryQueue.Clear();
            _episodeRetryCount.Clear();
            _episodeRetryReason.Clear();
            _seriesProcessedForEpisodes.Clear();
            _lastGlobalActionUtc = DateTime.MinValue;
            _logger?.LogInformation("[MR] RenameCoordinator state cleared");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MR] Error clearing RenameCoordinator state: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Queues an episode for retry when metadata is incomplete.
    /// </summary>
    /// <param name="episode">The episode to queue.</param>
    /// <param name="reason">The reason for queuing.</param>
    private void QueueEpisodeForRetry(Episode episode, string reason)
    {
        try
        {
            if (!_episodeRetryQueue.ContainsKey(episode.Id))
            {
                _episodeRetryQueue[episode.Id] = DateTime.UtcNow;
                _episodeRetryCount[episode.Id] = 0;
                _episodeRetryReason[episode.Id] = reason;
                _logger.LogInformation("[MR] [DEBUG] === Episode Queued for Retry ===");
                _logger.LogInformation("[MR] [DEBUG] Episode ID: {Id}", episode.Id);
                _logger.LogInformation("[MR] [DEBUG] Episode Name: {Name}", episode.Name ?? "NULL");
                _logger.LogInformation("[MR] [DEBUG] Episode Path: {Path}", episode.Path ?? "NULL");
                _logger.LogInformation("[MR] [DEBUG] Season: {Season}, Episode: {Episode}", 
                    episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                    episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                _logger.LogInformation("[MR] [DEBUG] Reason: {Reason}", reason);
                _logger.LogInformation("[MR] [DEBUG] Retry delay: {Minutes} minutes", RetryDelayMinutes);
                _logger.LogInformation("[MR] [DEBUG] Max retry attempts: {MaxAttempts}", MaxRetryAttempts);
                _logger.LogInformation("[MR] [DEBUG] Current queue size: {QueueSize}", _episodeRetryQueue.Count);
                _logger.LogInformation("[MR] [DEBUG] Will retry after {Minutes} minutes or on next ItemUpdated event", RetryDelayMinutes);
                _logger.LogInformation("[MR] [DEBUG] === Episode Queued for Retry Complete ===");
            }
            else
            {
                // Update retry count
                var currentCount = _episodeRetryCount.GetValueOrDefault(episode.Id, 0);
                _logger.LogInformation("[MR] [DEBUG] Episode {Id} already in retry queue. Current retry count: {Count}/{MaxAttempts}", 
                    episode.Id, currentCount, MaxRetryAttempts);
                _logger.LogInformation("[MR] [DEBUG] Previous reason: {Reason}", _episodeRetryReason.GetValueOrDefault(episode.Id, "Unknown"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] Error queuing episode for retry: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Processes the episode retry queue, attempting to rename episodes that were previously skipped due to incomplete metadata.
    /// </summary>
    /// <param name="cfg">The plugin configuration.</param>
    /// <param name="now">The current UTC time.</param>
    private void ProcessRetryQueue(PluginConfiguration cfg, DateTime now)
    {
        try
        {
            if (!cfg.RenameEpisodeFiles || cfg.DryRun)
            {
                _logger.LogDebug("[MR] [DEBUG] ProcessRetryQueue: Skipping (RenameEpisodeFiles={RenameEpisodes}, DryRun={DryRun})", 
                    cfg.RenameEpisodeFiles, cfg.DryRun);
                return;
            }

            if (_episodeRetryQueue.Count == 0)
            {
                _logger.LogDebug("[MR] [DEBUG] ProcessRetryQueue: Queue is empty, nothing to process");
                return;
            }

            _logger.LogInformation("[MR] [DEBUG] === Processing Episode Retry Queue ===");
            _logger.LogInformation("[MR] [DEBUG] Episodes in queue: {Count}", _episodeRetryQueue.Count);
            _logger.LogInformation("[MR] [DEBUG] Current time: {Now}", now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));

            var episodesToRetry = new List<Guid>();
            var episodesToRemove = new List<Guid>();

            _logger.LogInformation("[MR] [DEBUG] Evaluating {Count} episodes in queue", _episodeRetryQueue.Count);
            
            foreach (var kvp in _episodeRetryQueue.ToList())
            {
                var episodeId = kvp.Key;
                var lastRetry = kvp.Value;
                var retryCount = _episodeRetryCount.GetValueOrDefault(episodeId, 0);
                var reason = _episodeRetryReason.GetValueOrDefault(episodeId, "Unknown");

                // Check if enough time has passed since last retry
                var timeSinceLastRetry = now - lastRetry;
                var shouldRetry = timeSinceLastRetry.TotalMinutes >= RetryDelayMinutes;

                _logger.LogInformation("[MR] [DEBUG] Episode {Id}: Retry count={Count}/{Max}, Time since last retry={Minutes:F1} min, Should retry={ShouldRetry}", 
                    episodeId, retryCount, MaxRetryAttempts, timeSinceLastRetry.TotalMinutes, shouldRetry);
                _logger.LogInformation("[MR] [DEBUG] Episode {Id}: Reason={Reason}", episodeId, reason);

                if (retryCount >= MaxRetryAttempts)
                {
                    _logger.LogWarning("[MR] [DEBUG] Removing episode {Id} from retry queue: Max retry attempts ({MaxAttempts}) reached", 
                        episodeId, MaxRetryAttempts);
                    episodesToRemove.Add(episodeId);
                    continue;
                }

                if (shouldRetry)
                {
                    _logger.LogInformation("[MR] [DEBUG] Episode {Id} will be retried (enough time has passed)", episodeId);
                    episodesToRetry.Add(episodeId);
                }
                else
                {
                    _logger.LogInformation("[MR] [DEBUG] Episode {Id} will not be retried yet (only {Minutes:F1} minutes since last retry, need {RequiredMinutes} minutes)", 
                        episodeId, timeSinceLastRetry.TotalMinutes, RetryDelayMinutes);
                }
            }

            // Remove episodes that exceeded max attempts
            foreach (var episodeId in episodesToRemove)
            {
                _episodeRetryQueue.Remove(episodeId);
                _episodeRetryCount.Remove(episodeId);
                _episodeRetryReason.Remove(episodeId);
            }

            // Retry episodes
            if (episodesToRetry.Count > 0 && _libraryManager != null)
            {
                _logger.LogInformation("[MR] [DEBUG] Retrying {Count} episodes from queue", episodesToRetry.Count);

                foreach (var episodeId in episodesToRetry)
                {
                    try
                    {
                        _logger.LogInformation("[MR] [DEBUG] === Retrying Episode {Id} ===", episodeId);
                        var item = _libraryManager.GetItemById(episodeId);
                        if (item is Episode episode)
                        {
                            var retryCount = _episodeRetryCount.GetValueOrDefault(episodeId, 0);
                            _episodeRetryCount[episodeId] = retryCount + 1;
                            _episodeRetryQueue[episodeId] = now; // Update last retry time

                            _logger.LogInformation("[MR] [DEBUG] Retry attempt {Attempt}/{MaxAttempts} for episode {Id}", 
                                retryCount + 1, MaxRetryAttempts, episodeId);
                            _logger.LogInformation("[MR] [DEBUG] Episode Name: {Name}", episode.Name ?? "NULL");
                            _logger.LogInformation("[MR] [DEBUG] Episode Path: {Path}", episode.Path ?? "NULL");

                            // Process the episode
                            HandleEpisodeUpdate(episode, cfg, now, isBulkProcessing: false);

                            _logger.LogInformation("[MR] [DEBUG] === Retry Attempt Complete for Episode {Id} ===", episodeId);
                        }
                        else
                        {
                            _logger.LogWarning("[MR] [DEBUG] Episode {Id} no longer exists or is not an Episode. Removing from queue.", episodeId);
                            episodesToRemove.Add(episodeId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[MR] [DEBUG] Error retrying episode {Id}: {Message}", episodeId, ex.Message);
                    }
                }
            }
            else if (episodesToRetry.Count > 0 && _libraryManager == null)
            {
                _logger.LogWarning("[MR] [DEBUG] Cannot retry episodes: LibraryManager is not available");
            }

            _logger.LogInformation("[MR] [DEBUG] === Retry Queue Processing Complete ===");
            _logger.LogInformation("[MR] [DEBUG] Episodes retried: {Retried}", episodesToRetry.Count);
            _logger.LogInformation("[MR] [DEBUG] Episodes removed (max attempts): {Removed}", episodesToRemove.Count);
            _logger.LogInformation("[MR] [DEBUG] Episodes remaining in queue: {Count}", _episodeRetryQueue.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] Error processing retry queue: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Removes an episode from the retry queue (called after successful processing).
    /// </summary>
    /// <param name="episodeId">The episode ID to remove.</param>
    private void RemoveEpisodeFromRetryQueue(Guid episodeId)
    {
        if (_episodeRetryQueue.ContainsKey(episodeId))
        {
            var retryCount = _episodeRetryCount.GetValueOrDefault(episodeId, 0);
            var reason = _episodeRetryReason.GetValueOrDefault(episodeId, "Unknown");
            
            _episodeRetryQueue.Remove(episodeId);
            _episodeRetryCount.Remove(episodeId);
            _episodeRetryReason.Remove(episodeId);
            
            _logger.LogInformation("[MR] [DEBUG] === Episode Removed from Retry Queue ===");
            _logger.LogInformation("[MR] [DEBUG] Episode ID: {Id}", episodeId);
            _logger.LogInformation("[MR] [DEBUG] Retry count when removed: {Count}", retryCount);
            _logger.LogInformation("[MR] [DEBUG] Original reason: {Reason}", reason);
            _logger.LogInformation("[MR] [DEBUG] Episode {Id} successfully processed and removed from retry queue", episodeId);
            _logger.LogInformation("[MR] [DEBUG] Remaining queue size: {Count}", _episodeRetryQueue.Count);
        }
        else
        {
            _logger.LogDebug("[MR] [DEBUG] Episode {Id} was not in retry queue (may have been removed already)", episodeId);
        }
    }

    /// <summary>
    /// Extracts a clean episode title from episode.Name by removing filename patterns.
    /// </summary>
    /// <param name="episode">The episode to extract the title from.</param>
    /// <returns>The cleaned episode title, or empty string if no clean title can be extracted.</returns>
    private string ExtractCleanEpisodeTitle(Episode episode)
    {
        if (episode == null || string.IsNullOrWhiteSpace(episode.Name))
        {
            _logger.LogDebug("[MR] [DEBUG] ExtractCleanEpisodeTitle: Episode is null or Name is empty");
            return string.Empty;
        }

        var episodeName = episode.Name.Trim();
        var seasonNumber = episode.ParentIndexNumber;
        var episodeNumber = episode.IndexNumber;

        _logger.LogInformation("[MR] [DEBUG] === Episode Title Extraction ===");
        _logger.LogInformation("[MR] [DEBUG] Original episode.Name: '{Original}'", episodeName);
        _logger.LogInformation("[MR] [DEBUG] Season: {Season}, Episode: {Episode}", 
            seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
            episodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");

        // Use SafeName helper to extract clean title
        var cleanTitle = SafeName.ExtractCleanEpisodeTitle(episodeName, seasonNumber, episodeNumber);

        // #region agent log - EPISODE-TITLE-EXTRACTION: Track title extraction details
        try
        {
            var isFilenamePattern = !string.IsNullOrWhiteSpace(episodeName) && 
                                  System.Text.RegularExpressions.Regex.IsMatch(episodeName, @"[Ss]\d+[Ee]\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            _logger.LogInformation("[MR] [DEBUG] [EPISODE-TITLE-EXTRACTION] EpisodeId={EpisodeId}, OriginalName='{OriginalName}', Season={Season}, Episode={Episode}, CleanTitle='{CleanTitle}', IsEmpty={IsEmpty}, IsFilenamePattern={IsFilenamePattern}",
                episode.Id.ToString(), episodeName, 
                seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                episodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                cleanTitle ?? "NULL", string.IsNullOrWhiteSpace(cleanTitle), isFilenamePattern);
            
            var logData = new { 
                runId = "run1", 
                hypothesisId = "EPISODE-TITLE-EXTRACTION", 
                location = "RenameCoordinator.cs:301", 
                message = "Episode title extraction result", 
                data = new { 
                    episodeId = episode.Id.ToString(),
                    originalName = episodeName,
                    seasonNumber = seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                    episodeNumber = episodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                    cleanTitle = cleanTitle ?? "NULL",
                    isEmpty = string.IsNullOrWhiteSpace(cleanTitle),
                    isFilenamePattern = isFilenamePattern
                }, 
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
            };
            var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] [DEBUG] [EPISODE-TITLE-EXTRACTION] ERROR logging title extraction: {Error}", ex.Message);
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "EPISODE-TITLE-EXTRACTION", location = "RenameCoordinator.cs:301", message = "ERROR logging title extraction", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
        }
        // #endregion

        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            _logger.LogWarning("[MR] [DEBUG] Could not extract clean episode title from '{EpisodeName}'. Original name may be a filename pattern.", episodeName);
            _logger.LogInformation("[MR] [DEBUG] === Episode Title Extraction Complete (Empty) ===");
            return string.Empty;
        }

        if (cleanTitle != episodeName)
        {
            _logger.LogInformation("[MR] [DEBUG] ✓ Successfully extracted clean episode title");
            _logger.LogInformation("[MR] [DEBUG] Original: '{Original}'", episodeName);
            _logger.LogInformation("[MR] [DEBUG] Cleaned:  '{Cleaned}'", cleanTitle);
        }
        else
        {
            _logger.LogInformation("[MR] [DEBUG] Episode title was already clean (no patterns detected)");
        }

        _logger.LogInformation("[MR] [DEBUG] === Episode Title Extraction Complete ===");
        return cleanTitle;
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
            // Process retry queue first (before processing new items)
            ProcessRetryQueue(cfg, DateTime.UtcNow);
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "C", location = "RenameCoordinator.cs:42", message = "HandleItemUpdated entry", data = new { itemType = e.Item?.GetType().Name ?? "null", itemName = e.Item?.Name ?? "null", enabled = cfg.Enabled, renameSeriesFolders = cfg.RenameSeriesFolders, dryRun = cfg.DryRun, requireProviderIdMatch = cfg.RequireProviderIdMatch, onlyRenameWhenProviderIdsChange = cfg.OnlyRenameWhenProviderIdsChange }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            
            // Log all item updates with full details
            _logger.LogInformation("[MR] ===== ItemUpdated Event Received =====");
            _logger.LogInformation("[MR] Item Type: {Type}", e.Item?.GetType().Name ?? "NULL");
            _logger.LogInformation("[MR] Item Name: {Name}", e.Item?.Name ?? "NULL");
            _logger.LogInformation("[MR] Item ID: {Id}", e.Item?.Id.ToString() ?? "NULL");
            _logger.LogInformation(
                "[MR] Configuration: Enabled={Enabled}, RenameSeriesFolders={RenameSeriesFolders}, RenameSeasonFolders={RenameSeasonFolders}, RenameEpisodeFiles={RenameEpisodeFiles}, RenameMovieFolders={RenameMovieFolders}, DryRun={DryRun}, RequireProviderIdMatch={RequireProviderIdMatch}, OnlyRenameWhenProviderIdsChange={OnlyRenameWhenProviderIdsChange}, ProcessDuringLibraryScans={ProcessDuringLibraryScans}",
                cfg.Enabled, cfg.RenameSeriesFolders, cfg.RenameSeasonFolders, cfg.RenameEpisodeFiles, cfg.RenameMovieFolders, cfg.DryRun, cfg.RequireProviderIdMatch, cfg.OnlyRenameWhenProviderIdsChange, cfg.ProcessDuringLibraryScans);

        if (!cfg.Enabled)
        {
                // #region agent log
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "RenameCoordinator.cs:49", message = "Plugin disabled", data = new { itemType = e.Item?.GetType().Name ?? "null" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                // #endregion
                _logger.LogWarning("[MR] SKIP: Plugin is disabled in configuration");
            return;
        }

        var now = DateTime.UtcNow;

            // Handle Series items
            if (e.Item is Series series)
            {
                // #region agent log - SERIES-ITEM-UPDATED: Track all series ItemUpdated events
                try
                {
                    var logData = new { 
                        runId = "run1", 
                        hypothesisId = "SERIES-ITEM-UPDATED", 
                        location = "RenameCoordinator.cs:406", 
                        message = "Series ItemUpdated event received", 
                        data = new { 
                            seriesId = series.Id.ToString(),
                            seriesName = series.Name ?? "NULL",
                            seriesPath = series.Path ?? "NULL",
                            providerIdsCount = series.ProviderIds?.Count ?? 0,
                            providerIds = series.ProviderIds != null ? string.Join(",", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")) : "null"
                        }, 
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    _logger.LogInformation("[MR] [DEBUG] [SERIES-ITEM-UPDATED] Series ItemUpdated: Id={Id}, Name='{Name}', Path={Path}, ProviderIds={ProviderIds}",
                        series.Id, series.Name ?? "NULL", series.Path ?? "NULL",
                        series.ProviderIds != null ? string.Join(",", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")) : "NONE");
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "SERIES-ITEM-UPDATED", location = "RenameCoordinator.cs:406", message = "ERROR logging series event", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                }
                // #endregion
                
                // Debounce global spam for Series items only
                var timeSinceLastAction = now - _lastGlobalActionUtc;
                if (timeSinceLastAction < _globalMinInterval)
                {
                    _logger.LogInformation("[MR] SKIP: Global debounce active. Time since last action: {Seconds} seconds (min: {MinSeconds})",
                        timeSinceLastAction.TotalSeconds, _globalMinInterval.TotalSeconds);
                    return;
                }
                _lastGlobalActionUtc = now;

        if (!cfg.RenameSeriesFolders)
        {
                    _logger.LogInformation("[MR] SKIP: RenameSeriesFolders is disabled in configuration");
                    return;
                }
                HandleSeriesUpdate(series, cfg, now);
            return;
        }

            // Handle Season items
            if (e.Item is Season season)
            {
                // #region agent log - SEASON-ITEM-UPDATED: Track all season ItemUpdated events
                try
                {
                    _logger.LogInformation("[MR] [DEBUG] [SEASON-ITEM-UPDATED] Season ItemUpdated: Id={Id}, Name='{Name}', Path={Path}, SeasonNumber={SeasonNumber}, SeriesId={SeriesId}, SeriesName='{SeriesName}'",
                        season.Id, season.Name ?? "NULL", season.Path ?? "NULL", 
                        season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                        season.Series?.Id.ToString() ?? "NULL", season.Series?.Name ?? "NULL");
                    
                    var logData = new { 
                        runId = "run1", 
                        hypothesisId = "SEASON-ITEM-UPDATED", 
                        location = "RenameCoordinator.cs:457", 
                        message = "Season ItemUpdated event received", 
                        data = new { 
                            seasonId = season.Id.ToString(),
                            seasonName = season.Name ?? "NULL",
                            seasonPath = season.Path ?? "NULL",
                            seasonNumber = season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                            seriesId = season.Series?.Id.ToString() ?? "NULL",
                            seriesName = season.Series?.Name ?? "NULL",
                            seriesPath = season.Series?.Path ?? "NULL"
                        }, 
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MR] [DEBUG] [SEASON-ITEM-UPDATED] ERROR logging season event: {Error}", ex.Message);
                }
                // #endregion
                
                // Debounce global spam for Season items only
                var timeSinceLastAction = now - _lastGlobalActionUtc;
                if (timeSinceLastAction < _globalMinInterval)
                {
                    _logger.LogInformation("[MR] [DEBUG] [SEASON-SKIP-GLOBAL-DEBOUNCE] SKIP: Global debounce active. Time since last action: {Seconds} seconds (min: {MinSeconds})",
                        timeSinceLastAction.TotalSeconds, _globalMinInterval.TotalSeconds);
                    return;
                }
                _lastGlobalActionUtc = now;

                if (!cfg.RenameSeasonFolders)
                {
                    _logger.LogInformation("[MR] [DEBUG] [SEASON-SKIP-CONFIG-DISABLED] SKIP: RenameSeasonFolders is disabled in configuration");
                    return;
                }
                
                // #region agent log - SEASON-PROCESSING-START: Track when season processing starts
                try
                {
                    _logger.LogInformation("[MR] [DEBUG] [SEASON-PROCESSING-START] Starting HandleSeasonUpdate for Season: {Name}, ID: {Id}, SeasonNumber: {SeasonNumber}",
                        season.Name ?? "NULL", season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MR] [DEBUG] [SEASON-PROCESSING-START] ERROR logging season processing start: {Error}", ex.Message);
                }
                // #endregion
                
                HandleSeasonUpdate(season, cfg, now);
                
                // #region agent log - SEASON-PROCESSING-COMPLETE: Track when season processing completes
                try
                {
                    _logger.LogInformation("[MR] [DEBUG] [SEASON-PROCESSING-COMPLETE] HandleSeasonUpdate completed for Season: {Name}, ID: {Id}",
                        season.Name ?? "NULL", season.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MR] [DEBUG] [SEASON-PROCESSING-COMPLETE] ERROR logging season processing complete: {Error}", ex.Message);
                }
                // #endregion
                
            return;
        }

            // Handle Episode items
            if (e.Item is Episode episode)
            {
                // #region agent log - EPISODE-METADATA-DEBUG: Track episode metadata state when ItemUpdated fires
                try
                {
                    var currentFileName = !string.IsNullOrWhiteSpace(episode.Path) ? Path.GetFileNameWithoutExtension(episode.Path) : "NULL";
                    var isFilenamePattern = !string.IsNullOrWhiteSpace(episode.Name) && 
                                           System.Text.RegularExpressions.Regex.IsMatch(episode.Name, @"[Ss]\d+[Ee]\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var logData = new { 
                        runId = "run1", 
                        hypothesisId = "EPISODE-METADATA", 
                        location = "RenameCoordinator.cs:405", 
                        message = "Episode ItemUpdated event - full metadata state", 
                        data = new { 
                            episodeId = episode.Id.ToString(), 
                            episodeName = episode.Name ?? "NULL", 
                            episodeNameIsFilenamePattern = isFilenamePattern,
                            episodePath = episode.Path ?? "NULL",
                            currentFileName = currentFileName,
                            episodeIndexNumber = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", 
                            parentIndexNumber = episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                            seriesName = episode.Series?.Name ?? "NULL",
                            seriesId = episode.Series?.Id.ToString() ?? "NULL",
                            seriesPath = episode.Series?.Path ?? "NULL",
                            seriesHasProviderIds = episode.Series?.ProviderIds != null && episode.Series.ProviderIds.Count > 0
                        }, 
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    _logger.LogInformation("[MR] [EPISODE-METADATA] Episode ItemUpdated: Id={Id}, Name='{Name}' (isPattern={IsPattern}), Path={Path}, S{Season}E{Episode}", 
                        episode.Id, episode.Name ?? "NULL", isFilenamePattern, episode.Path ?? "NULL",
                        episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "??",
                        episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "??");
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "EPISODE-METADATA", location = "RenameCoordinator.cs:405", message = "ERROR logging episode metadata", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                }
                // #endregion
                
                // #region agent log - MULTI-EPISODE-HYP-A: Track all episode ItemUpdated events
                try
                {
                    var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-A", location = "RenameCoordinator.cs:124", message = "Episode ItemUpdated event received", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", episodePath = episode.Path ?? "NULL", episodeIndexNumber = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", parentIndexNumber = episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", seriesName = episode.Series?.Name ?? "NULL", seriesPath = episode.Series?.Path ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    _logger.LogInformation("[MR] [MULTI-EP-A] Episode ItemUpdated event: EpisodeId={Id}, Name={Name}, Path={Path}, IndexNumber={IndexNumber}", 
                        episode.Id, episode.Name ?? "NULL", episode.Path ?? "NULL", episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-A", location = "RenameCoordinator.cs:124", message = "ERROR logging episode event", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                }
                // #endregion
                
                // #region agent log - Hypothesis A: Check episode state immediately after cast
                try
                {
                    var indexNumberImmediate = episode.IndexNumber;
                    var parentIndexNumberImmediate = episode.ParentIndexNumber;
                    var episodeTypeImmediate = episode.GetType().FullName;
                    var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "RenameCoordinator.cs:124", message = "Episode cast successful - immediate IndexNumber check", data = new { episodeType = episodeTypeImmediate, indexNumber = indexNumberImmediate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", parentIndexNumber = parentIndexNumberImmediate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    _logger.LogInformation("[MR] [DEBUG-HYP-A] Episode cast: Type={Type}, IndexNumber={IndexNumber}, ParentIndexNumber={ParentIndexNumber}, Id={Id}, Name={Name}", 
                        episodeTypeImmediate, indexNumberImmediate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", 
                        parentIndexNumberImmediate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episode.Id, episode.Name ?? "NULL");
                }
                catch (Exception ex)
                {
                    var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "RenameCoordinator.cs:124", message = "ERROR checking IndexNumber immediately after cast", data = new { error = ex.Message, episodeId = episode?.Id.ToString() ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    _logger.LogError(ex, "[MR] [DEBUG-HYP-A] ERROR checking IndexNumber immediately after cast: {Error}", ex.Message);
                }
                // #endregion
                
                var seasonNumForLogging = episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
                var episodeNumForLogging = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
                
                if (!cfg.RenameEpisodeFiles)
                {
                    // #region agent log - MULTI-EPISODE-HYP-B: Track episodes skipped due to config
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-B", location = "RenameCoordinator.cs:148", message = "Episode skipped - RenameEpisodeFiles disabled", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNumForLogging, episodeNumber = episodeNumForLogging }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    // #endregion
                    _logger.LogInformation("[MR] [DEBUG] [EPISODE-SKIP-CONFIG-DISABLED] SKIP: RenameEpisodeFiles is disabled in configuration. Season={Season}, Episode={Episode}", seasonNumForLogging, episodeNumForLogging);
                    _logger.LogInformation("[MR] ===== Episode Processing Complete (Skipped - Config Disabled) =====");
                    return;
                }
                HandleEpisodeUpdate(episode, cfg, now);
                return;
            }

            // Handle Movie items
            if (e.Item is Movie movie)
            {
                // Debounce global spam for Movie items only
                var timeSinceLastAction = now - _lastGlobalActionUtc;
                if (timeSinceLastAction < _globalMinInterval)
                {
                    _logger.LogInformation("[MR] SKIP: Global debounce active. Time since last action: {Seconds} seconds (min: {MinSeconds})",
                        timeSinceLastAction.TotalSeconds, _globalMinInterval.TotalSeconds);
                    return;
                }
                _lastGlobalActionUtc = now;

                if (!cfg.RenameMovieFolders)
                {
                    _logger.LogInformation("[MR] SKIP: RenameMovieFolders is disabled in configuration");
                    return;
                }
                HandleMovieUpdate(movie, cfg, now);
                return;
            }

            // Skip other item types
            _logger.LogInformation("[MR] SKIP: Item is not a Series, Season, Episode, or Movie. Type={Type}, Name={Name}", e.Item?.GetType().Name ?? "NULL", e.Item?.Name ?? "NULL");
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
    /// <param name="series">The series to process.</param>
    /// <param name="cfg">The plugin configuration.</param>
    /// <param name="now">The current UTC time.</param>
    /// <param name="forceProcessing">If true, processes the series even if provider IDs haven't changed (for bulk refresh).</param>
    private void HandleSeriesUpdate(Series series, PluginConfiguration cfg, DateTime now, bool forceProcessing = false)
    {
        // Defensive check: Ensure RenameSeriesFolders is enabled
        if (!cfg.RenameSeriesFolders)
        {
            _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-SKIP] SKIP: RenameSeriesFolders is disabled in configuration. Series: {Name}, Id={Id}", 
                series.Name ?? "NULL", series.Id);
            return;
        }
        
        _logger.LogInformation("[MR] Processing Series: Name={Name}, Id={Id}, Path={Path}", series.Name, series.Id, series.Path);

        // Per-item cooldown for series folder renaming
        // NOTE: We check cooldown here but may still process episodes even if series folder rename is skipped
        bool seriesRenameOnCooldown = false;
        if (_lastAttemptUtcByItem.TryGetValue(series.Id, out var lastTry))
        {
                var timeSinceLastTry = (now - lastTry).TotalSeconds;
                if (timeSinceLastTry < cfg.PerItemCooldownSeconds)
            {
                    _logger.LogInformation(
                        "[MR] SKIP: Cooldown active for series folder rename. SeriesId={Id}, Name={Name}, Time since last try: {Seconds} seconds (cooldown: {CooldownSeconds})",
                        series.Id, series.Name, timeSinceLastTry.ToString(System.Globalization.CultureInfo.InvariantCulture), cfg.PerItemCooldownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _logger.LogInformation("[MR] [DEBUG] Cooldown active, but will still check if episodes need processing");
                seriesRenameOnCooldown = true;
                // Don't return here - continue to process episodes even if series folder rename is on cooldown
            }
        }

        // Only update cooldown timestamp if we're not on cooldown (to avoid resetting the timer)
        if (!seriesRenameOnCooldown)
        {
        _lastAttemptUtcByItem[series.Id] = now;
        }

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

            // Check provider IDs - log ALL provider IDs in detail for debugging
            var providerIdsCount = series.ProviderIds?.Count ?? 0;
            var providerIdsString = series.ProviderIds != null 
                ? string.Join(", ", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")) 
                : "NONE";
            
            _logger.LogInformation("[MR] === Provider IDs Details ===");
            _logger.LogInformation("[MR] Provider IDs Count: {Count}", providerIdsCount);
            _logger.LogInformation("[MR] All Provider IDs: {Values}", providerIdsString);
            
            // Log each provider ID individually for clarity
            if (series.ProviderIds != null && series.ProviderIds.Count > 0)
            {
                foreach (var kv in series.ProviderIds)
                {
                    _logger.LogInformation("[MR]   - {Provider}: {Id}", kv.Key, kv.Value ?? "NULL");
                }
            }
            else
            {
                _logger.LogWarning("[MR]   - No provider IDs found!");
            }

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

            // #region agent log - Year detection
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "YEAR-DETECT", location = "RenameCoordinator.cs:297", message = "Year detection from metadata", data = new { seriesId = series.Id.ToString(), seriesName = name ?? "NULL", productionYear = series.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", premiereDate = series.PremiereDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", premiereDateYear = series.PremiereDate?.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", finalYear = year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", yearSource = yearSource, currentFolderPath = path, currentFolderName = Path.GetFileName(path) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion

            _logger.LogInformation("[MR] === Year Detection from Metadata (BEFORE Correction) ===");
            _logger.LogInformation("[MR] Series: {Name}, ID: {Id}", name ?? "NULL", series.Id);
            _logger.LogInformation("[MR] ProductionYear (from Jellyfin): {ProductionYear}", 
                series.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] PremiereDate (from Jellyfin): {PremiereDate}, Year: {PremiereDateYear}", 
                series.PremiereDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                series.PremiereDate?.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] Year from Metadata (BEFORE correction): {Year} (Source: {YearSource})", 
                year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                yearSource);
            _logger.LogInformation("[MR] Current Folder Name: {CurrentFolderName}", Path.GetFileName(path));
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

            // Check if we should process during library scans
            // NEW LOGIC:
            // - Normal scans: Only process when provider IDs change (Identify flow)
            // - "Replace all metadata": Triggered via bulk processing (handled above)
            // - The "Identify" flow (provider IDs change) always works
            var hasProviderIds = series.ProviderIds != null && series.ProviderIds.Count > 0;
            var newHash = hasProviderIds && series.ProviderIds != null
                ? ProviderIdHelper.ComputeProviderHash(series.ProviderIds)
                : string.Empty;
            
            var hasOldHash = _providerHashByItem.TryGetValue(series.Id, out var oldHash);
            var providerIdsChanged = hasOldHash && !string.Equals(newHash, oldHash, StringComparison.Ordinal);
            var isFirstTime = !hasOldHash;

            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "B", location = "RenameCoordinator.cs:124", message = "Provider hash check", data = new { oldHash = oldHash ?? "(none)", newHash = newHash, hasOldHash = hasOldHash, hasProviderIds = hasProviderIds, seriesName = name, providerIds = series.ProviderIds != null ? string.Join(",", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")) : "null", processDuringLibraryScans = cfg.ProcessDuringLibraryScans, onlyRenameWhenProviderIdsChange = cfg.OnlyRenameWhenProviderIdsChange, providerIdsChanged = providerIdsChanged, isFirstTime = isFirstTime }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion

            _logger.LogInformation("[MR] === Provider Hash Check ===");
            _logger.LogInformation("[MR] ProcessDuringLibraryScans: {ProcessDuringLibraryScans}", cfg.ProcessDuringLibraryScans);
            _logger.LogInformation("[MR] OnlyRenameWhenProviderIdsChange: {OnlyRenameWhenProviderIdsChange}", cfg.OnlyRenameWhenProviderIdsChange);
            _logger.LogInformation("[MR] Has Provider IDs: {HasProviderIds}", hasProviderIds);
            _logger.LogInformation("[MR] Old Hash Exists: {HasOldHash}, Value: {OldHash}", hasOldHash, oldHash ?? "(none)");
            _logger.LogInformation("[MR] New Hash: {NewHash}", newHash);
            _logger.LogInformation("[MR] Provider IDs Changed: {Changed}, First Time: {FirstTime}", providerIdsChanged, isFirstTime);
            _logger.LogInformation("[MR] Series: {Name}", name);
            
            // #region agent log - PROVIDER-HASH-CHECK: Track provider hash state
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [PROVIDER-HASH-CHECK] Provider hash check state: SeriesId={SeriesId}, SeriesName='{SeriesName}', OnlyRenameWhenProviderIdsChange={OnlyRenameWhenProviderIdsChange}, HasProviderIds={HasProviderIds}, HasOldHash={HasOldHash}, OldHash={OldHash}, NewHash={NewHash}, ProviderIdsChanged={ProviderIdsChanged}, IsFirstTime={IsFirstTime}, ForceProcessing={ForceProcessing}",
                    series.Id, name ?? "NULL", cfg.OnlyRenameWhenProviderIdsChange, hasProviderIds, hasOldHash, oldHash ?? "(none)", newHash, providerIdsChanged, isFirstTime, forceProcessing);
                
                var logData = new { 
                    runId = "run1", 
                    hypothesisId = "PROVIDER-HASH-CHECK", 
                    location = "RenameCoordinator.cs:777", 
                    message = "Provider hash check state", 
                    data = new { 
                        seriesId = series.Id.ToString(),
                        seriesName = name ?? "NULL",
                        processDuringLibraryScans = cfg.ProcessDuringLibraryScans,
                        onlyRenameWhenProviderIdsChange = cfg.OnlyRenameWhenProviderIdsChange,
                        hasProviderIds = hasProviderIds,
                        hasOldHash = hasOldHash,
                        oldHash = oldHash ?? "(none)",
                        newHash = newHash,
                        providerIdsChanged = providerIdsChanged,
                        isFirstTime = isFirstTime,
                        forceProcessing = forceProcessing
                    }, 
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [PROVIDER-HASH-CHECK] ERROR logging hash check: {Error}", ex.Message);
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "PROVIDER-HASH-CHECK", location = "RenameCoordinator.cs:777", message = "ERROR logging hash check", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            }
            // #endregion

            // Track series update for bulk processing detection
            // IMPORTANT: Only add to queue if provider IDs have NOT changed
            // If provider IDs changed (Identify flow), don't count this towards bulk processing detection
            var currentTime = DateTime.UtcNow;
            
            // Only add to queue if this is NOT an "Identify" operation (provider IDs unchanged)
            // This prevents "Identify" from triggering bulk processing
            if (!providerIdsChanged && !isFirstTime)
            {
                _seriesUpdateTimestamps.Enqueue(currentTime);
            }
            else
            {
                _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE-SKIP] Skipping queue addition - Provider IDs changed (Identify flow) or first time. ProviderIdsChanged={Changed}, IsFirstTime={FirstTime}",
                    providerIdsChanged, isFirstTime);
            }

            // #region agent log - SERIES-UPDATE-QUEUE: Track each series update added to queue
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE] === Series Update Added to Queue ===");
                _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE] Series: {Name}, ID: {Id}", name ?? "NULL", series.Id);
                _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE] Timestamp: {Timestamp}", currentTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
                _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE] Queue size BEFORE adding: {Count}", _seriesUpdateTimestamps.Count - 1);
                _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE] Queue size AFTER adding: {Count}", _seriesUpdateTimestamps.Count);
                _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE] Provider IDs Changed: {Changed}", providerIdsChanged);
                _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE] Is First Time: {IsFirstTime}", isFirstTime);
                
                var logData = new {
                    runId = "run1",
                    hypothesisId = "SERIES-UPDATE-QUEUE",
                    location = "RenameCoordinator.cs:798",
                    message = "Series update added to queue",
                    data = new {
                        seriesId = series.Id.ToString(),
                        seriesName = name ?? "NULL",
                        timestamp = currentTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
                        queueSizeBefore = providerIdsChanged || isFirstTime ? _seriesUpdateTimestamps.Count : _seriesUpdateTimestamps.Count - 1,
                        queueSizeAfter = _seriesUpdateTimestamps.Count,
                        providerIdsChanged = providerIdsChanged,
                        isFirstTime = isFirstTime,
                        hasProviderIds = hasProviderIds,
                        addedToQueue = !providerIdsChanged && !isFirstTime
                    },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [SERIES-UPDATE-QUEUE] ERROR logging queue addition: {Error}", ex.Message);
            }
            // #endregion

            // Clean old timestamps outside the time window
            var timestampsRemoved = 0;
            while (_seriesUpdateTimestamps.Count > 0 && (currentTime - _seriesUpdateTimestamps.Peek()).TotalSeconds > BulkUpdateTimeWindowSeconds)
            {
                var removedTimestamp = _seriesUpdateTimestamps.Dequeue();
                timestampsRemoved++;
            }
            
            // #region agent log - SERIES-UPDATE-QUEUE-CLEAN: Track timestamp cleanup
            if (timestampsRemoved > 0)
            {
                try
                {
                    _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE-CLEAN] Removed {Count} old timestamps from queue", timestampsRemoved);
                    _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE-CLEAN] Queue size after cleanup: {Count}", _seriesUpdateTimestamps.Count);
                    
                    var logData = new {
                        runId = "run1",
                        hypothesisId = "SERIES-UPDATE-QUEUE-CLEAN",
                        location = "RenameCoordinator.cs:804",
                        message = "Old timestamps removed from queue",
                        data = new {
                            seriesId = series.Id.ToString(),
                            seriesName = name ?? "NULL",
                            timestampsRemoved = timestampsRemoved,
                            queueSizeAfterCleanup = _seriesUpdateTimestamps.Count
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MR] [DEBUG] [SERIES-UPDATE-QUEUE-CLEAN] ERROR logging cleanup: {Error}", ex.Message);
                }
            }
            // #endregion

            // Check if this looks like "Replace all metadata" (bulk refresh)
            // IMPORTANT: Only trigger bulk processing if:
            // 1. Provider IDs have NOT changed (indicates "Replace all metadata", not "Identify")
            // 2. Series has provider IDs (is identified) - don't process unidentified shows
            // 3. Enough series updates in the time window
            // "Replace all metadata" updates metadata but doesn't change provider IDs
            // "Identify" changes provider IDs, so we should NOT trigger bulk processing for it
            var isBulkRefresh = _seriesUpdateTimestamps.Count >= BulkUpdateThreshold;
            var timeSinceLastBulkProcessing = (currentTime - _lastBulkProcessingUtc).TotalMinutes;
            var seriesIsIdentified = hasProviderIds; // Series has provider IDs (is identified)
            var shouldTriggerBulkProcessing = isBulkRefresh &&
                                             timeSinceLastBulkProcessing >= BulkProcessingCooldownMinutes &&
                                             !providerIdsChanged &&
                                             !isFirstTime &&
                                             seriesIsIdentified; // Only trigger for identified series (don't process everything during normal scans)

            // #region agent log - BULK-PROCESSING-DETECTION: Track bulk processing detection logic
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] === Bulk Processing Detection ===");
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] Series: {Name}, ID: {Id}", name ?? "NULL", series.Id);
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] Series updates in queue: {Count}", _seriesUpdateTimestamps.Count);
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] Bulk update threshold: {Threshold}", BulkUpdateThreshold);
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] Time window: {Seconds} seconds", BulkUpdateTimeWindowSeconds);
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] isBulkRefresh (count >= threshold): {IsBulkRefresh}", isBulkRefresh);
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] Time since last bulk processing: {Minutes} minutes (cooldown: {CooldownMinutes} minutes)",
                    timeSinceLastBulkProcessing, BulkProcessingCooldownMinutes);
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] providerIdsChanged: {Changed}", providerIdsChanged);
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] isFirstTime: {IsFirstTime}", isFirstTime);
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] seriesIsIdentified: {IsIdentified}", seriesIsIdentified);
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] shouldTriggerBulkProcessing: {ShouldTrigger}", shouldTriggerBulkProcessing);
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] Reason: {Reason}",
                    shouldTriggerBulkProcessing
                        ? "Bulk refresh detected (Replace all metadata)"
                        : (isBulkRefresh
                            ? (providerIdsChanged || isFirstTime
                                ? "Provider IDs changed (Identify flow) - skipping bulk processing"
                                : (!seriesIsIdentified
                                    ? "Series not identified - skipping bulk processing (normal scan, not bulk refresh)"
                                    : (timeSinceLastBulkProcessing < BulkProcessingCooldownMinutes
                                        ? "Bulk processing on cooldown"
                                        : "Unknown reason")))
                            : "Not enough series updates to trigger bulk processing"));

                var logData = new {
                    runId = "run1",
                    hypothesisId = "BULK-PROCESSING-DETECTION",
                    location = "RenameCoordinator.cs:790",
                    message = "Bulk processing detection",
                    data = new {
                        seriesId = series.Id.ToString(),
                        seriesName = name ?? "NULL",
                        seriesUpdatesInQueue = _seriesUpdateTimestamps.Count,
                        bulkUpdateThreshold = BulkUpdateThreshold,
                        bulkUpdateTimeWindowSeconds = BulkUpdateTimeWindowSeconds,
                        isBulkRefresh = isBulkRefresh,
                        timeSinceLastBulkProcessing = timeSinceLastBulkProcessing,
                        bulkProcessingCooldownMinutes = BulkProcessingCooldownMinutes,
                        providerIdsChanged = providerIdsChanged,
                        isFirstTime = isFirstTime,
                        seriesIsIdentified = seriesIsIdentified,
                        shouldTriggerBulkProcessing = shouldTriggerBulkProcessing
                    },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [BULK-PROCESSING-DETECTION] ERROR logging bulk processing detection: {Error}", ex.Message);
            }
            // #endregion

            if (shouldTriggerBulkProcessing)
            {
                // #region agent log - BULK-PROCESSING-TRIGGERED: Track when bulk processing is actually triggered
                try
                {
                    _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-TRIGGERED] ===== BULK PROCESSING TRIGGERED =====");
                    _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-TRIGGERED] Series that triggered it: {Name}, ID: {Id}", name ?? "NULL", series.Id);
                    _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-TRIGGERED] Queue size: {Count}", _seriesUpdateTimestamps.Count);
                    _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-TRIGGERED] Provider IDs Changed: {Changed}", providerIdsChanged);
                    _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-TRIGGERED] Is First Time: {IsFirstTime}", isFirstTime);
                    _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-TRIGGERED] Series Is Identified: {IsIdentified}", seriesIsIdentified);
                    _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-TRIGGERED] Time Since Last Bulk: {Minutes} minutes", timeSinceLastBulkProcessing);
                    
                    var logData = new {
                        runId = "run1",
                        hypothesisId = "BULK-PROCESSING-TRIGGERED",
                        location = "RenameCoordinator.cs:880",
                        message = "Bulk processing triggered - ProcessAllSeriesInLibrary will be called",
                        data = new {
                            triggeringSeriesId = series.Id.ToString(),
                            triggeringSeriesName = name ?? "NULL",
                            queueSize = _seriesUpdateTimestamps.Count,
                            providerIdsChanged = providerIdsChanged,
                            isFirstTime = isFirstTime,
                            seriesIsIdentified = seriesIsIdentified,
                            timeSinceLastBulkProcessing = timeSinceLastBulkProcessing
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MR] [DEBUG] [BULK-PROCESSING-TRIGGERED] ERROR logging trigger: {Error}", ex.Message);
                }
                // #endregion
                
                _logger.LogInformation("[MR] === Bulk Refresh Detected (Replace All Metadata) ===");
                _logger.LogInformation("[MR] Detected {Count} series updates in {Seconds} seconds",
                    _seriesUpdateTimestamps.Count, BulkUpdateTimeWindowSeconds);
                _logger.LogInformation("[MR] Provider IDs unchanged - this indicates 'Replace all metadata' operation");
                _logger.LogInformation("[MR] Triggering bulk processing of all series in library...");

                _lastBulkProcessingUtc = currentTime;
                _seriesUpdateTimestamps.Clear(); // Clear after triggering to avoid duplicate triggers

                // Process all series in the library asynchronously (don't block current processing)
                Task.Run(() => ProcessAllSeriesInLibrary(cfg, currentTime));
            }
            else if (isBulkRefresh && (providerIdsChanged || isFirstTime))
            {
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] Skipping bulk processing - Provider IDs changed (Identify flow detected)");
            }

            // Determine if we should proceed with rename
            bool shouldProceed = false;
            string proceedReason = string.Empty;

            if (cfg.OnlyRenameWhenProviderIdsChange)
            {
                // OnlyRenameWhenProviderIdsChange is enabled
                // NEW BEHAVIOR: Normal scans only process when provider IDs change (Identify flow)
                // "Replace all metadata" is handled via bulk processing (triggered above)
                if (forceProcessing)
                {
                    // Force processing for bulk refresh (Replace all metadata)
                    shouldProceed = true;
                    proceedReason = "Bulk refresh - processing all series regardless of provider ID changes";
                }
                else if (providerIdsChanged || isFirstTime)
                {
                    // Provider IDs changed or first time - always proceed (Identify flow)
                    shouldProceed = true;
                    proceedReason = providerIdsChanged ? "Provider IDs changed (Identify flow)" : "First time processing";
                }
                else
                {
                    // Provider IDs unchanged - skip (normal scans only process identified shows)
                    // Bulk refresh is handled separately via ProcessAllSeriesInLibrary
                    shouldProceed = false;
                    proceedReason = "Provider IDs unchanged - normal scans only process identified shows. Use 'Replace all metadata' for bulk processing.";
                }
            }
            else
            {
                // OnlyRenameWhenProviderIdsChange is disabled - always proceed
                shouldProceed = true;
                proceedReason = "OnlyRenameWhenProviderIdsChange disabled";
            }
            
            // #region agent log - SHOULD-PROCEED-DECISION: Track shouldProceed decision
            try
            {
                var logData = new { 
                    runId = "run1", 
                    hypothesisId = "SHOULD-PROCEED-DECISION", 
                    location = "RenameCoordinator.cs:749", 
                    message = "shouldProceed decision made", 
                    data = new { 
                        seriesId = series.Id.ToString(),
                        seriesName = name ?? "NULL",
                        shouldProceed = shouldProceed,
                        proceedReason = proceedReason,
                        onlyRenameWhenProviderIdsChange = cfg.OnlyRenameWhenProviderIdsChange,
                        forceProcessing = forceProcessing,
                        providerIdsChanged = providerIdsChanged,
                        isFirstTime = isFirstTime
                    }, 
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                _logger.LogInformation("[MR] [DEBUG] [SHOULD-PROCEED-DECISION] shouldProceed={ShouldProceed}, Reason='{Reason}'", shouldProceed, proceedReason);
            }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "SHOULD-PROCEED-DECISION", location = "RenameCoordinator.cs:749", message = "ERROR logging shouldProceed", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            }
            // #endregion

            if (!shouldProceed)
            {
                // #region agent log - SKIP-SERIES-PROCESSING: Track when series processing is skipped
                try
                {
                    var logData = new { 
                        runId = "run1", 
                        hypothesisId = "SKIP-SERIES-PROCESSING", 
                        location = "RenameCoordinator.cs:784", 
                        message = "Series processing skipped - shouldProceed=false", 
                        data = new { 
                            seriesId = series.Id.ToString(),
                            seriesName = name ?? "NULL",
                            hash = newHash,
                            reason = proceedReason,
                            onlyRenameWhenProviderIdsChange = cfg.OnlyRenameWhenProviderIdsChange,
                            providerIdsChanged = providerIdsChanged,
                            isFirstTime = isFirstTime,
                            forceProcessing = forceProcessing
                        }, 
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    _logger.LogWarning("[MR] [DEBUG] [SKIP-SERIES-PROCESSING] SKIP: {Reason}. Name={Name}, Hash={Hash}", proceedReason, name, newHash);
                }
                catch (Exception ex)
                {
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "SKIP-SERIES-PROCESSING", location = "RenameCoordinator.cs:784", message = "ERROR logging skip", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                }
                // #endregion
                _logger.LogWarning("[MR] SKIP: {Reason}. Name={Name}, Hash={Hash}", proceedReason, name, newHash);
                _logger.LogInformation("[MR] ===== Processing Complete (Skipped) =====");
                return;
            }

            // Handle provider IDs - use if available, otherwise use empty values
            string providerLabel = string.Empty;
            string providerId = string.Empty;

            // SMART DETECTION: Before storing new provider IDs, check what changed
            // This allows us to detect which provider the user selected in "Identify"
            Dictionary<string, string>? previousProviderIds = null;
            if (hasProviderIds && series.ProviderIds != null)
            {
                // Get previous provider IDs BEFORE storing new ones
                _previousProviderIdsByItem.TryGetValue(series.Id, out previousProviderIds);
            }

            if (series.ProviderIds != null && series.ProviderIds.Count > 0)
            {
                // SMART DETECTION: If provider IDs changed, detect which one was newly added/changed
                // This represents the provider the user selected in "Identify"
                string? selectedProviderKey = null;
                string? selectedProviderId = null;
                
                if (providerIdsChanged && previousProviderIds != null)
                {
                    // #region agent log - Provider ID Detection
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:411", message = "Provider IDs changed - starting detection", data = new { seriesId = series.Id.ToString(), seriesName = series.Name ?? "NULL", previousProviderIds = previousProviderIds.Select(kv => $"{kv.Key}={kv.Value}").ToList(), currentProviderIds = series.ProviderIds?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>(), preferredProviders = cfg.PreferredSeriesProviders?.ToList() ?? new List<string>() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    // #endregion
                    
                    // Compare old vs new to find what changed
                    _logger.LogInformation("[MR] === Detecting User-Selected Provider (Identify) ===");
                    _logger.LogInformation("[MR] Previous Provider IDs: {Previous}", 
                        previousProviderIds.Count > 0 
                            ? string.Join(", ", previousProviderIds.Select(kv => $"{kv.Key}={kv.Value}"))
                            : "NONE");
                    _logger.LogInformation("[MR] Current Provider IDs: {Current}", 
                        string.Join(", ", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")));
                    
                    // Collect all new/changed providers (don't break on first match)
                    var changedProviders = new List<(string Key, string Value, string ChangeType)>();
                    
                    foreach (var kv in series.ProviderIds)
                    {
                        var providerKey = kv.Key;
                        var providerValue = kv.Value;
                        
                        // Check if this provider ID is new or changed
                        if (!previousProviderIds.TryGetValue(providerKey, out var oldValue))
                        {
                            // New provider ID
                            changedProviders.Add((providerKey, providerValue, "NEW"));
                            _logger.LogInformation("[MR] ✓ Detected NEW provider: {Provider}={Id}", 
                                providerKey, providerValue);
                        }
                        else if (oldValue != providerValue)
                        {
                            // Changed provider ID
                            changedProviders.Add((providerKey, providerValue, "CHANGED"));
                            _logger.LogInformation("[MR] ✓ Detected CHANGED provider: {Provider}={OldId} -> {NewId}", 
                                providerKey, oldValue, providerValue);
                        }
                    }
                    
                    // If multiple providers changed, prioritize based on preferred list
                    if (changedProviders.Count > 0)
                    {
                        var preferredList = cfg.PreferredSeriesProviders != null
                            ? cfg.PreferredSeriesProviders
                            : new System.Collections.ObjectModel.Collection<string>();
                        
                        // #region agent log - Changed Providers List
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:448", message = "Changed providers collected", data = new { changedProvidersCount = changedProviders.Count, changedProviders = changedProviders.Select(cp => new { key = cp.Key, value = cp.Value, changeType = cp.ChangeType }).ToList(), preferredList = preferredList.ToList() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                        // #endregion
                        
                        // Try to find a changed provider that matches the preferred list
                        foreach (var preferred in preferredList)
                        {
                            var match = changedProviders.FirstOrDefault(cp => 
                                string.Equals(cp.Key, preferred, StringComparison.OrdinalIgnoreCase));
                            if (match.Key != null)
                            {
                                selectedProviderKey = match.Key;
                                selectedProviderId = match.Value;
                                // #region agent log - Provider Selected from Preferred
                                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:460", message = "Provider selected from preferred list", data = new { selectedProvider = selectedProviderKey, selectedId = selectedProviderId, changeType = match.ChangeType, preferred = preferred }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                                // #endregion
                                _logger.LogInformation("[MR] ✓ Selected provider from changed list (prioritized by preference): {Provider}={Id} ({ChangeType})", 
                                    selectedProviderKey, selectedProviderId, match.ChangeType);
                                break;
                            }
                        }
                        
                        // If no preferred match found, use the first changed provider
                        if (selectedProviderKey == null)
                        {
                            selectedProviderKey = changedProviders[0].Key;
                            selectedProviderId = changedProviders[0].Value;
                            // #region agent log - Provider Selected (First Changed)
                            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:470", message = "Provider selected (first changed, no preferred match)", data = new { selectedProvider = selectedProviderKey, selectedId = selectedProviderId, changeType = changedProviders[0].ChangeType }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                            // #endregion
                            _logger.LogInformation("[MR] ✓ Selected first changed provider: {Provider}={Id} ({ChangeType})", 
                                selectedProviderKey, selectedProviderId, changedProviders[0].ChangeType);
                        }
                    }
                    else
                    {
                        // #region agent log - No Changed Provider Detected
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:492", message = "WARNING: No changed provider detected - all IDs already present", data = new { previousProviderIds = previousProviderIds.Select(kv => $"{kv.Key}={kv.Value}").ToList(), currentProviderIds = series.ProviderIds?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>(), preferredProviders = cfg.PreferredSeriesProviders?.ToList() ?? new List<string>() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                        // #endregion
                        _logger.LogWarning("[MR] ⚠️ No newly added/changed provider detected (all IDs were already present). This may indicate the wrong match was selected.");
                        _logger.LogWarning("[MR] ⚠️ If you selected a different match in Identify, you may need to clear the series metadata first, then re-identify.");
                    }
                }
                else if (isFirstTime)
                {
                    // #region agent log - First Time Processing
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:498", message = "First time processing - cannot detect user selection", data = new { currentProviderIds = series.ProviderIds?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>(), preferredProviders = cfg.PreferredSeriesProviders?.ToList() ?? new List<string>() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    // #endregion
                    _logger.LogInformation("[MR] First time processing - cannot detect user selection, will use preferred provider");
                }
                
                // If we detected a user-selected provider, use it; otherwise fall back to preferred list
                if (selectedProviderKey != null && selectedProviderId != null)
                {
                    providerLabel = selectedProviderKey.Trim().ToLowerInvariant();
                    providerId = selectedProviderId.Trim();
                    
                    // Log year before correction
                    var yearBeforeCorrection = year;
                    
                    // Validate and correct year now that we have the provider ID
                    year = ValidateAndCorrectYear(year, providerLabel, providerId, name, path);
                    
                    // Log year after correction
                    if (yearBeforeCorrection != year)
                    {
                        _logger.LogInformation("[MR] === Year Correction Applied ===");
                        _logger.LogInformation("[MR] Year BEFORE correction: {BeforeYear}", 
                            yearBeforeCorrection?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                        _logger.LogInformation("[MR] Year AFTER correction: {AfterYear}", 
                            year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                    }
                    
                    // #region agent log - Final Provider Selection (User-Selected)
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:504", message = "FINAL: Using user-selected provider", data = new { selectedProvider = providerLabel, selectedId = providerId, allProviderIds = series.ProviderIds?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    // #endregion
                    _logger.LogInformation("[MR] === Provider Selection (User-Selected) ===");
                    _logger.LogInformation("[MR] Using user-selected provider from Identify: {Provider}={Id}", providerLabel, providerId);
                }
                else
                {
                    // Fall back to preferred provider list
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
                        
                        // Log year before correction
                        var yearBeforeCorrection = year;
                        
                        // Validate and correct year now that we have the provider ID
                        year = ValidateAndCorrectYear(year, providerLabel, providerId, name, path);
                        
                        // Log year after correction
                        if (yearBeforeCorrection != year)
                        {
                            _logger.LogInformation("[MR] === Year Correction Applied ===");
                            _logger.LogInformation("[MR] Year BEFORE correction: {BeforeYear}", 
                                yearBeforeCorrection?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                            _logger.LogInformation("[MR] Year AFTER correction: {AfterYear}", 
                                year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                        }
                        
                        // #region agent log - Final Provider Selection (Preferred List)
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:522", message = "FINAL: Using preferred list provider (fallback)", data = new { selectedProvider = providerLabel, selectedId = providerId, preferredList = preferredList.ToList(), allProviderIds = series.ProviderIds?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>(), whyFallback = selectedProviderKey == null ? "No user-selected provider detected" : "User-selected provider was null" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                        // #endregion
                        _logger.LogInformation("[MR] === Provider Selection (Preferred List) ===");
                        _logger.LogInformation("[MR] Selected Provider: {Provider}={Id} (from preferred list: {PreferredList})", 
                            providerLabel, providerId, string.Join(", ", preferredList));
                        
                        // Warn if multiple provider IDs exist - might indicate conflicting matches
                        if (series.ProviderIds.Count > 1)
                        {
                            _logger.LogWarning("[MR] ⚠️ WARNING: Multiple provider IDs detected ({Count}). Selected: {SelectedProvider}={SelectedId}", 
                                series.ProviderIds.Count, providerLabel, providerId);
                            _logger.LogWarning("[MR] ⚠️ If the wrong ID was selected, check your 'Preferred Series Providers' setting in plugin configuration.");
                            _logger.LogWarning("[MR] ⚠️ Current preference order: {Order}", string.Join(" > ", preferredList));
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[MR] No matching provider found in preferred list");
                    }
                }
            }
            
            // Update hash AFTER processing (so we can compare next time)
            _logger.LogInformation("[MR] ✓ Proceeding with rename. Reason: {Reason}", proceedReason);
            _logger.LogInformation("[MR] Old Hash: {OldHash}, New Hash: {NewHash}", oldHash ?? "(none)", newHash);
            if (hasProviderIds && series.ProviderIds != null)
            {
                _providerHashByItem[series.Id] = newHash;
                // Store current provider IDs for next comparison
                _previousProviderIdsByItem[series.Id] = new Dictionary<string, string>(series.ProviderIds);
            }
            
            if (series.ProviderIds == null || series.ProviderIds.Count == 0)
            {
                if (cfg.RequireProviderIdMatch)
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
            }

            // Only attempt series folder rename if not on cooldown
            bool renameSuccessful = false;
            if (!seriesRenameOnCooldown)
            {
            // Build final desired folder name: Name (Year) [provider-id] or Name (Year) if no provider IDs
            var currentFolderName = Path.GetFileName(path);
            
            // #region agent log - Before folder name generation
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "YEAR-DETECT", location = "RenameCoordinator.cs:594", message = "Before folder name generation", data = new { seriesName = name, year = year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", yearSource = yearSource, providerLabel = providerLabel ?? "NULL", providerId = providerId ?? "NULL", currentFolderName = currentFolderName, format = cfg.SeriesFolderFormat, productionYear = series.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", premiereDate = series.PremiereDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            
            _logger.LogInformation("[MR] === Folder Name Generation ===");
            _logger.LogInformation("[MR] Format: {Format}", cfg.SeriesFolderFormat);
            _logger.LogInformation("[MR] FINAL Year (AFTER correction): {Year} (Original Source: {YearSource})", 
                year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                yearSource);
            _logger.LogInformation("[MR] Values: Name={Name}, Year={Year}, Provider={Provider}, ID={Id}", 
                name ?? "NULL",
                year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                providerLabel ?? "NULL",
                providerId ?? "NULL");
            
            var desiredFolderName = SafeName.RenderSeriesFolder(
                cfg.SeriesFolderFormat,
                name,
                year,
                providerLabel,
                providerId);
            
            // #region agent log - Final Folder Name Generation
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:604", message = "Final folder name generation", data = new { seriesName = name, year = year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", providerLabel = providerLabel ?? "NULL", providerId = providerId ?? "NULL", currentFolderName = currentFolderName, desiredFolderName = desiredFolderName, format = cfg.SeriesFolderFormat }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            
            _logger.LogInformation("[MR] Current Folder Name: {Current}", currentFolderName);
            _logger.LogInformation("[MR] Desired Folder Name: {Desired}", desiredFolderName);
            _logger.LogInformation("[MR] Year Available: {HasYear}", year.HasValue);

            _logger.LogInformation("[MR] Full Current Path: {Path}", path);
            _logger.LogInformation("[MR] Dry Run Mode: {DryRun}", cfg.DryRun);

            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "RenameCoordinator.cs:186", message = "Attempting rename", data = new { seriesName = name, currentPath = path, desiredFolderName = desiredFolderName, dryRun = cfg.DryRun }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion

                renameSuccessful = _pathRenamer.TryRenameSeriesFolder(series, desiredFolderName, cfg.DryRun);
            }
            else
            {
                _logger.LogInformation("[MR] [DEBUG] Series folder rename skipped due to cooldown, but will still process episodes if needed");
            }

            // DEBUG: Log detailed decision-making for episode processing
            _logger.LogInformation("[MR] === Episode Processing Decision ===");
            _logger.LogInformation("[MR] renameSuccessful: {RenameSuccessful}", renameSuccessful);
            _logger.LogInformation("[MR] cfg.RenameEpisodeFiles: {RenameEpisodeFiles}", cfg.RenameEpisodeFiles);
            _logger.LogInformation("[MR] cfg.DryRun: {DryRun}", cfg.DryRun);
            _logger.LogInformation("[MR] providerIdsChanged: {ProviderIdsChanged}", providerIdsChanged);
            _logger.LogInformation("[MR] Series ID: {SeriesId}, Series Name: {SeriesName}", series.Id, series.Name);
            _logger.LogInformation("[MR] Series has provider IDs: {HasProviderIds}", series.ProviderIds?.Count > 0);

            // ALWAYS process all episodes when:
            // - RenameEpisodeFiles is enabled
            // - Not in dry-run mode
            // - Series has provider IDs (identified)
            // This ensures ALL seasons are processed automatically when a series is identified
            var shouldProcessEpisodes = cfg.RenameEpisodeFiles && 
                                       !cfg.DryRun && 
                                       series.ProviderIds != null && 
                                       series.ProviderIds.Count > 0;

            _logger.LogInformation("[MR] [DEBUG] === Series Episode Processing Decision ===");
            _logger.LogInformation("[MR] [DEBUG] shouldProcessEpisodes: {ShouldProcess}", shouldProcessEpisodes);
            _logger.LogInformation("[MR] [DEBUG]   - RenameEpisodeFiles: {RenameEpisodes}", cfg.RenameEpisodeFiles);
            _logger.LogInformation("[MR] [DEBUG]   - DryRun: {DryRun}", cfg.DryRun);
            _logger.LogInformation("[MR] [DEBUG]   - Has Provider IDs: {HasProviderIds} (Count: {Count})", 
                series.ProviderIds != null && series.ProviderIds.Count > 0, 
                series.ProviderIds?.Count ?? 0);
            _logger.LogInformation("[MR] [DEBUG]   - Already processed: {AlreadyProcessed}", 
                _seriesProcessedForEpisodes.Contains(series.Id));

            if (shouldProcessEpisodes)
            {
                // Check if we've already processed this series recently (avoid duplicate processing)
                // Process episodes if:
                // - Provider IDs changed (series was identified) - ALWAYS process (this ensures all seasons are processed when a series is identified)
                // - Series hasn't been processed for episodes yet - ALWAYS process
                // - Series folder was renamed (and not already processed) - Process if rename happened
                // - ProcessDuringLibraryScans is enabled - Process episodes during library scans to catch metadata updates
                //   This ensures that if metadata is updated during a scan, episodes are reprocessed
                //   IMPORTANT: This only applies to identified series (series with provider IDs)
                var shouldReprocess = providerIdsChanged || 
                                     !_seriesProcessedForEpisodes.Contains(series.Id) ||
                                     (renameSuccessful && !_seriesProcessedForEpisodes.Contains(series.Id)) ||
                                     (cfg.ProcessDuringLibraryScans && seriesIsIdentified); // Process during library scans for identified series

                _logger.LogInformation("[MR] [DEBUG] shouldReprocess: {ShouldReprocess}", shouldReprocess);
                _logger.LogInformation("[MR] [DEBUG]   - providerIdsChanged: {Changed}", providerIdsChanged);
                _logger.LogInformation("[MR] [DEBUG]   - Not in processed set: {NotProcessed}", !_seriesProcessedForEpisodes.Contains(series.Id));
                _logger.LogInformation("[MR] [DEBUG]   - renameSuccessful: {Renamed}", renameSuccessful);
                _logger.LogInformation("[MR] [DEBUG]   - seriesRenameOnCooldown: {OnCooldown}", seriesRenameOnCooldown);
                _logger.LogInformation("[MR] [DEBUG]   - ProcessDuringLibraryScans: {ProcessDuringLibraryScans}", cfg.ProcessDuringLibraryScans);
                _logger.LogInformation("[MR] [DEBUG]   - seriesIsIdentified: {IsIdentified} (has provider IDs)", seriesIsIdentified);

                if (shouldReprocess)
                {
                    _logger.LogInformation("[MR] === DECISION: Processing all episodes from all seasons ===");
                    _logger.LogInformation("[MR] Series: {Name}, ID: {Id}", series.Name ?? "NULL", series.Id);
                    if (providerIdsChanged)
                    {
                        _logger.LogInformation("[MR] [DEBUG] Reason: Provider IDs changed (series identified)");
                    }
                    else if (renameSuccessful)
                    {
                        _logger.LogInformation("[MR] [DEBUG] Reason: Series folder renamed");
                    }
                    else if (cfg.ProcessDuringLibraryScans && seriesIsIdentified)
                    {
                        _logger.LogInformation("[MR] [DEBUG] Reason: Library scan in progress (ProcessDuringLibraryScans enabled) - reprocessing identified series to catch metadata updates");
                    }
                    else
                    {
                        _logger.LogInformation("[MR] [DEBUG] Reason: Series has provider IDs and episodes need processing");
                    }

                    // #region agent log - PROCESS-ALL-EPISODES: Track when ProcessAllEpisodesFromSeries is called
                    try
                    {
                        _logger.LogWarning("[MR] [DEBUG] [PROCESS-ALL-EPISODES] ProcessAllEpisodesFromSeries called: SeriesId={SeriesId}, SeriesName='{SeriesName}', ProviderIdsChanged={ProviderIdsChanged}, RenameSuccessful={RenameSuccessful}, HasProviderIds={HasProviderIds}",
                            series.Id.ToString(), series.Name ?? "NULL", providerIdsChanged, renameSuccessful,
                            series.ProviderIds != null && series.ProviderIds.Count > 0);
                        
                        var logData = new { 
                            runId = "run1", 
                            hypothesisId = "PROCESS-ALL-EPISODES", 
                            location = "RenameCoordinator.cs:1001", 
                            message = "ProcessAllEpisodesFromSeries called", 
                            data = new { 
                                seriesId = series.Id.ToString(),
                                seriesName = series.Name ?? "NULL",
                                providerIdsChanged = providerIdsChanged,
                                renameSuccessful = renameSuccessful,
                                hasProviderIds = series.ProviderIds != null && series.ProviderIds.Count > 0
                            }, 
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                        };
                        var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[MR] [DEBUG] [PROCESS-ALL-EPISODES] ERROR logging ProcessAllEpisodesFromSeries call: {Error}", ex.Message);
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "PROCESS-ALL-EPISODES", location = "RenameCoordinator.cs:1001", message = "ERROR logging ProcessAllEpisodesFromSeries call", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    }
                    // #endregion
                    
                    ProcessAllEpisodesFromSeries(series, cfg, now);
                    _seriesProcessedForEpisodes.Add(series.Id);
                    _logger.LogInformation("[MR] [DEBUG] Series {Id} marked as processed for episodes", series.Id);
                }
                else
                {
                    _logger.LogInformation("[MR] === DECISION: Skipping episode processing (already processed recently) ===");
                    _logger.LogInformation("[MR] [DEBUG] Series {Id} was already processed. Skipping to avoid duplicate processing.", series.Id);
                    
                    // #region agent log - SKIP-EPISODE-PROCESSING: Track when episode processing is skipped
                    try
                    {
                        _logger.LogInformation("[MR] [DEBUG] [SKIP-EPISODE-PROCESSING] Episode processing skipped for series: SeriesId={SeriesId}, SeriesName='{SeriesName}', ProviderIdsChanged={ProviderIdsChanged}, AlreadyProcessed={AlreadyProcessed}, RenameSuccessful={RenameSuccessful}",
                            series.Id, series.Name ?? "NULL", providerIdsChanged, _seriesProcessedForEpisodes.Contains(series.Id), renameSuccessful);
                        
                        var logData = new { 
                            runId = "run1", 
                            hypothesisId = "SKIP-EPISODE-PROCESSING", 
                            location = "RenameCoordinator.cs:1273", 
                            message = "Episode processing skipped - already processed", 
                            data = new { 
                                seriesId = series.Id.ToString(),
                                seriesName = series.Name ?? "NULL",
                                providerIdsChanged = providerIdsChanged,
                                alreadyProcessed = _seriesProcessedForEpisodes.Contains(series.Id),
                                renameSuccessful = renameSuccessful
                            }, 
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                        };
                        var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[MR] [DEBUG] [SKIP-EPISODE-PROCESSING] ERROR logging skip: {Error}", ex.Message);
                    }
                    // #endregion
                }
            }
            else
            {
                _logger.LogInformation("[MR] === DECISION: Not processing all episodes. Reasons:");
                if (!cfg.RenameEpisodeFiles)
                    _logger.LogInformation("[MR]   - RenameEpisodeFiles is disabled");
                if (cfg.DryRun)
                    _logger.LogInformation("[MR]   - DryRun mode is enabled");
                if (series.ProviderIds == null || series.ProviderIds.Count == 0)
                    _logger.LogInformation("[MR]   - Series has no provider IDs (not identified)");
            }

            _logger.LogInformation("[MR] ===== Processing Complete =====");
    }

    /// <summary>
    /// Processes all episodes from all seasons of a series.
    /// This ensures that when a series is identified, ALL episodes from ALL seasons are processed,
    /// not just the ones that happen to trigger ItemUpdated events.
    /// </summary>
    /// <param name="series">The series to process episodes for.</param>
    /// <param name="cfg">The plugin configuration.</param>
    /// <param name="now">The current UTC time.</param>
    private void ProcessAllEpisodesFromSeries(Series series, PluginConfiguration cfg, DateTime now)
    {
        try
        {
            // Defensive check: Ensure RenameEpisodeFiles is enabled
            if (!cfg.RenameEpisodeFiles)
            {
                _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES-SKIP] SKIP: RenameEpisodeFiles is disabled in configuration. Series: {Name}, ID: {Id}", 
                    series.Name ?? "NULL", series.Id);
                return;
            }
            
            // #region agent log - PROCESS-ALL-EPISODES-ENTRY: Track ProcessAllEpisodesFromSeries entry
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ENTRY] ===== ProcessAllEpisodesFromSeries ENTRY =====");
                _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ENTRY] Series: {Name}, ID: {Id}, Path: {Path}", 
                    series.Name ?? "NULL", series.Id, series.Path ?? "NULL");
                _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ENTRY] RenameEpisodeFiles: {RenameEpisodeFiles}, DryRun: {DryRun}", 
                    cfg.RenameEpisodeFiles, cfg.DryRun);
                
                var logData = new { 
                    runId = "run1", 
                    hypothesisId = "PROCESS-ALL-EPISODES-ENTRY", 
                    location = "RenameCoordinator.cs:1305", 
                    message = "ProcessAllEpisodesFromSeries entry", 
                    data = new { 
                        seriesId = series.Id.ToString(),
                        seriesName = series.Name ?? "NULL",
                        seriesPath = series.Path ?? "NULL",
                        renameEpisodeFiles = cfg.RenameEpisodeFiles,
                        dryRun = cfg.DryRun,
                        libraryManagerAvailable = _libraryManager != null
                    }, 
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [PROCESS-ALL-EPISODES-ENTRY] ERROR logging entry: {Error}", ex.Message);
            }
            // #endregion
            
            if (_libraryManager == null)
            {
                _logger.LogWarning("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ENTRY] LibraryManager is not available. Cannot process all episodes from series. Episodes will be processed individually as ItemUpdated events are received.");
                return;
            }

            _logger.LogInformation("[MR] === Processing All Episodes from All Seasons ===");
            _logger.LogInformation("[MR] Series: {Name}, ID: {Id}", series.Name, series.Id);

            // Get all episodes from the series using ILibraryManager
            // Try to get episodes recursively from the series
            var allEpisodes = new List<Episode>();
            
            try
            {
                // Get all children of the series (seasons and episodes)
                var query = new InternalItemsQuery
                {
                    ParentId = series.Id,
                    Recursive = true
                };
                
                _logger.LogInformation("[MR] === Executing Episode Query ===");
                _logger.LogInformation("[MR] Query: ParentId={ParentId}, Recursive={Recursive}", series.Id, true);
                _logger.LogInformation("[MR] Series Path: {Path}", series.Path ?? "NULL");
                _logger.LogInformation("[MR] LibraryManager Available: {Available}", _libraryManager != null);
                
                // #region agent log - EPISODE-QUERY-EXECUTION: Track episode query execution
                try
                {
                    var logData = new {
                        runId = "run1",
                        hypothesisId = "EPISODE-QUERY-EXECUTION",
                        location = "RenameCoordinator.cs:1613",
                        message = "Executing episode query",
                        data = new {
                            seriesId = series.Id.ToString(),
                            seriesName = series.Name ?? "NULL",
                            seriesPath = series.Path ?? "NULL",
                            queryParentId = series.Id.ToString(),
                            queryRecursive = true,
                            libraryManagerAvailable = _libraryManager != null
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-QUERY-EXECUTION] ERROR logging query execution: {Error}", logEx.Message);
                }
                // #endregion
                
                var allItems = _libraryManager.GetItemList(query);
                _logger.LogInformation("[MR] Query returned {TotalItems} total items", allItems.Count);
                
                // #region agent log - EPISODE-QUERY-RESULT: Track query results
                try
                {
                    // Count seasons and episodes explicitly
                    var seasonCount = allItems.OfType<Season>().Count();
                    var episodeCount = allItems.OfType<Episode>().Count();
                    
                    var logData = new {
                        runId = "run1",
                        hypothesisId = "EPISODE-QUERY-RESULT",
                        location = "RenameCoordinator.cs:1715",
                        message = "Episode query result - verifying seasons are returned",
                        data = new {
                            seriesId = series.Id.ToString(),
                            seriesName = series.Name ?? "NULL",
                            totalItemsReturned = allItems.Count,
                            seasonCount = seasonCount,
                            episodeCount = episodeCount,
                            itemTypes = allItems.GroupBy(item => item.GetType().Name).Select(g => new { type = g.Key, count = g.Count() }).ToList(),
                            seasonsFound = seasonCount > 0,
                            episodesFound = episodeCount > 0
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    
                    _logger.LogInformation("[MR] [DEBUG] [EPISODE-QUERY-RESULT] Query returned {TotalItems} items: {SeasonCount} seasons, {EpisodeCount} episodes",
                        allItems.Count, seasonCount, episodeCount);
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-QUERY-RESULT] ERROR logging query result: {Error}", logEx.Message);
                }
                // #endregion
                
                // Log item types found
                var itemTypes = allItems.GroupBy(item => item.GetType().Name).ToList();
                _logger.LogInformation("[MR] Item types found:");
                foreach (var typeGroup in itemTypes)
                {
                    _logger.LogInformation("[MR]   - {Type}: {Count} items", typeGroup.Key, typeGroup.Count());
                }
                
                allEpisodes = allItems.OfType<Episode>().ToList();
                
                // Extract seasons from query results and process them explicitly
                // This ensures season folders are renamed even if Jellyfin doesn't fire ItemUpdated events for them
                var allSeasons = allItems.OfType<Season>().ToList();
                
                _logger.LogInformation("[MR] Retrieved {Count} episodes and {SeasonCount} seasons using recursive query", allEpisodes.Count, allSeasons.Count);
                
                // #region agent log - SEASONS-FOUND: Track seasons found by ProcessAllEpisodesFromSeries
                try
                {
                    _logger.LogInformation("[MR] [DEBUG] [SEASONS-FOUND] ProcessAllEpisodesFromSeries found {TotalSeasons} seasons for SeriesId={SeriesId}, SeriesName='{SeriesName}'",
                        allSeasons.Count, series.Id.ToString(), series.Name ?? "NULL");
                    
                    foreach (var season in allSeasons.OrderBy(s => s.IndexNumber ?? int.MaxValue))
                    {
                        _logger.LogInformation("[MR] [DEBUG] [SEASONS-FOUND] Season: Id={SeasonId}, Name='{SeasonName}', SeasonNumber={SeasonNumber}, Path={SeasonPath}",
                            season.Id.ToString(), season.Name ?? "NULL", 
                            season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                            season.Path ?? "NULL");
                    }
                    
                    var logData = new {
                        runId = "run1",
                        hypothesisId = "SEASONS-FOUND",
                        location = "RenameCoordinator.cs:1751",
                        message = "Seasons found by ProcessAllEpisodesFromSeries",
                        data = new {
                            seriesId = series.Id.ToString(),
                            seriesName = series.Name ?? "NULL",
                            totalSeasons = allSeasons.Count,
                            seasons = allSeasons.Select(s => new {
                                seasonId = s.Id.ToString(),
                                seasonName = s.Name ?? "NULL",
                                seasonNumber = s.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                                seasonPath = s.Path ?? "NULL"
                            }).ToList()
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, "[MR] [DEBUG] [SEASONS-FOUND] ERROR logging seasons found: {Error}", logEx.Message);
                }
                // #endregion
                
                // Process season folders explicitly if RenameSeasonFolders is enabled
                if (cfg.RenameSeasonFolders && allSeasons.Count > 0)
                {
                    _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SEASONS] === Processing All Season Folders ===");
                    _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SEASONS] Found {Count} seasons to process", allSeasons.Count);
                    
                    int seasonsProcessed = 0;
                    int seasonsSkipped = 0;
                    
                    foreach (var season in allSeasons.OrderBy(s => s.IndexNumber ?? int.MaxValue))
                    {
                        try
                        {
                            var seasonNum = season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
                            _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SEASONS] Processing season folder: Season {SeasonNumber} - {Name} (ID: {Id})",
                                seasonNum, season.Name ?? "NULL", season.Id);
                            
                            // #region agent log - PROCESS-SEASON-BEFORE: Track season before processing
                            try
                            {
                                var logData = new {
                                    runId = "run1",
                                    hypothesisId = "PROCESS-SEASON-BEFORE",
                                    location = "RenameCoordinator.cs:1809",
                                    message = "About to process season folder",
                                    data = new {
                                        seasonId = season.Id.ToString(),
                                        seasonName = season.Name ?? "NULL",
                                        seasonNumber = seasonNum,
                                        seasonPath = season.Path ?? "NULL",
                                        hasPath = !string.IsNullOrWhiteSpace(season.Path),
                                        pathExists = !string.IsNullOrWhiteSpace(season.Path) && Directory.Exists(season.Path),
                                        hasIndexNumber = season.IndexNumber.HasValue,
                                        cooldownActive = _lastAttemptUtcByItem.TryGetValue(season.Id, out var lastTry) && (now - lastTry).TotalSeconds < cfg.PerItemCooldownSeconds
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                };
                                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                            }
                            catch (Exception logEx)
                            {
                                _logger.LogError(logEx, "[MR] [DEBUG] [PROCESS-SEASON-BEFORE] ERROR logging: {Error}", logEx.Message);
                            }
                            // #endregion
                            
                            // Call HandleSeasonUpdate to process the season folder
                            HandleSeasonUpdate(season, cfg, now);
                            
                            // #region agent log - PROCESS-SEASON-AFTER: Track season after processing
                            try
                            {
                                var logData = new {
                                    runId = "run1",
                                    hypothesisId = "PROCESS-SEASON-AFTER",
                                    location = "RenameCoordinator.cs:1818",
                                    message = "Season processing completed (no exception)",
                                    data = new {
                                        seasonId = season.Id.ToString(),
                                        seasonName = season.Name ?? "NULL",
                                        seasonNumber = seasonNum,
                                        seasonPath = season.Path ?? "NULL"
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                };
                                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                            }
                            catch (Exception logEx)
                            {
                                _logger.LogError(logEx, "[MR] [DEBUG] [PROCESS-SEASON-AFTER] ERROR logging: {Error}", logEx.Message);
                            }
                            // #endregion
                            
                            seasonsProcessed++;
                            _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SEASONS] ✓ Season {SeasonNumber} processed successfully", seasonNum);
                        }
                        catch (Exception seasonEx)
                        {
                            seasonsSkipped++;
                            var seasonNum = season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
                            _logger.LogError(seasonEx, "[MR] [DEBUG] [PROCESS-ALL-SEASONS] ERROR processing season {SeasonNumber} (ID: {Id}): {Message}",
                                seasonNum, season.Id, seasonEx.Message);
                            _logger.LogError("[MR] [DEBUG] [PROCESS-ALL-SEASONS] Stack Trace: {StackTrace}", seasonEx.StackTrace ?? "N/A");
                            
                            // #region agent log - PROCESS-SEASON-ERROR: Track season processing errors
                            try
                            {
                                var logData = new {
                                    runId = "run1",
                                    hypothesisId = "PROCESS-SEASON-ERROR",
                                    location = "RenameCoordinator.cs:1823",
                                    message = "Exception during season processing",
                                    data = new {
                                        seasonId = season.Id.ToString(),
                                        seasonName = season.Name ?? "NULL",
                                        seasonNumber = seasonNum,
                                        exceptionType = seasonEx.GetType().FullName,
                                        exceptionMessage = seasonEx.Message,
                                        stackTrace = seasonEx.StackTrace ?? "N/A"
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                };
                                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                            }
                            catch (Exception logEx)
                            {
                                _logger.LogError(logEx, "[MR] [DEBUG] [PROCESS-SEASON-ERROR] ERROR logging: {Error}", logEx.Message);
                            }
                            // #endregion
                        }
                    }
                    
                    _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SEASONS] === Season Folder Processing Summary ===");
                    _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SEASONS] Total seasons found: {Total}", allSeasons.Count);
                    _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SEASONS] Successfully processed: {Processed}", seasonsProcessed);
                    _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SEASONS] Skipped/Errors: {Skipped}", seasonsSkipped);
                    _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SEASONS] === Season Folder Processing Complete ===");
                }
                else if (allSeasons.Count > 0)
                {
                    _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SEASONS] SKIP: RenameSeasonFolders is disabled. Found {Count} seasons but not processing them.",
                        allSeasons.Count);
                }
                
                // Log episodes by season for debugging
                if (allEpisodes.Count > 0)
                {
                    var episodesBySeasonDebug = allEpisodes
                        .Where(e => e.ParentIndexNumber.HasValue)
                        .GroupBy(e => e.ParentIndexNumber.Value)
                        .OrderBy(g => g.Key)
                        .ToList();
                    
                    _logger.LogInformation("[MR] Episodes found by season (from query):");
                    foreach (var seasonGroup in episodesBySeasonDebug)
                    {
                        _logger.LogInformation("[MR]   Season {Season}: {Count} episodes", 
                            seasonGroup.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), 
                            seasonGroup.Count());
                    }
                    
                    // Log episodes without season numbers
                    var episodesWithoutSeasonNumbers = allEpisodes.Where(e => !e.ParentIndexNumber.HasValue).ToList();
                    if (episodesWithoutSeasonNumbers.Count > 0)
                    {
                        _logger.LogWarning("[MR] Found {Count} episodes without season numbers (ParentIndexNumber is null)", episodesWithoutSeasonNumbers.Count);
                        foreach (var ep in episodesWithoutSeasonNumbers.Take(5)) // Log first 5
                        {
                            _logger.LogWarning("[MR]   - Episode ID: {Id}, Name: {Name}, IndexNumber: {IndexNumber}", 
                                ep.Id, ep.Name ?? "NULL", ep.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // #region agent log - EPISODE-QUERY-ERROR: Track episode query errors
                try
                {
                    _logger.LogError(ex, "[MR] [DEBUG] [EPISODE-QUERY-ERROR] ERROR retrieving episodes using GetItemList");
                    _logger.LogError("[MR] [DEBUG] [EPISODE-QUERY-ERROR] Exception Type: {Type}", ex.GetType().FullName);
                    _logger.LogError("[MR] [DEBUG] [EPISODE-QUERY-ERROR] Exception Message: {Message}", ex.Message);
                    _logger.LogError("[MR] [DEBUG] [EPISODE-QUERY-ERROR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
                    _logger.LogError("[MR] [DEBUG] [EPISODE-QUERY-ERROR] Inner Exception: {InnerException}", ex.InnerException?.Message ?? "N/A");
                    _logger.LogError("[MR] [DEBUG] [EPISODE-QUERY-ERROR] Series: {Name}, ID: {Id}, Path: {Path}", 
                        series.Name ?? "NULL", series.Id, series.Path ?? "NULL");
                    
                    var logData = new {
                        runId = "run1",
                        hypothesisId = "EPISODE-QUERY-ERROR",
                        location = "RenameCoordinator.cs:1658",
                        message = "Error retrieving episodes - attempting fallback",
                        data = new {
                            seriesId = series.Id.ToString(),
                            seriesName = series.Name ?? "NULL",
                            seriesPath = series.Path ?? "NULL",
                            exceptionType = ex.GetType().FullName,
                            exceptionMessage = ex.Message,
                            stackTrace = ex.StackTrace ?? "N/A",
                            innerException = ex.InnerException?.Message ?? "N/A"
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-QUERY-ERROR] ERROR logging query error: {Error}", logEx.Message);
                }
                // #endregion
                
                _logger.LogWarning(ex, "[MR] Could not retrieve episodes using GetItemList. Trying alternative method: {Message}", ex.Message);
                
                // Fallback: Try to get episodes from series directly
                // This is a fallback in case the query method doesn't work
                try
                {
                    _logger.LogInformation("[MR] [DEBUG] Attempting fallback method to retrieve episodes");
                    
                    // Get all items recursively and filter for episodes
                    // This should work even if IncludeItemTypes has issues
                    var query = new InternalItemsQuery
                    {
                        ParentId = series.Id,
                        Recursive = true
                    };
                    
                    var allItems = _libraryManager.GetItemList(query);
                    allEpisodes = allItems.OfType<Episode>().ToList();
                    
                    _logger.LogInformation("[MR] Retrieved {Count} episodes using fallback recursive method", allEpisodes.Count);
                    
                    // #region agent log - EPISODE-QUERY-FALLBACK-SUCCESS: Track fallback success
                    try
                    {
                        var logData = new {
                            runId = "run1",
                            hypothesisId = "EPISODE-QUERY-FALLBACK-SUCCESS",
                            location = "RenameCoordinator.cs:1677",
                            message = "Fallback method succeeded",
                            data = new {
                                seriesId = series.Id.ToString(),
                                seriesName = series.Name ?? "NULL",
                                episodesRetrieved = allEpisodes.Count
                            },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-QUERY-FALLBACK-SUCCESS] ERROR logging fallback success: {Error}", logEx.Message);
                    }
                    // #endregion
                }
                catch (Exception fallbackEx)
                {
                    // #region agent log - EPISODE-QUERY-FALLBACK-ERROR: Track fallback failure
                    try
                    {
                        _logger.LogError(fallbackEx, "[MR] [DEBUG] [EPISODE-QUERY-FALLBACK-ERROR] ERROR in fallback method");
                        _logger.LogError("[MR] [DEBUG] [EPISODE-QUERY-FALLBACK-ERROR] Exception Type: {Type}", fallbackEx.GetType().FullName);
                        _logger.LogError("[MR] [DEBUG] [EPISODE-QUERY-FALLBACK-ERROR] Exception Message: {Message}", fallbackEx.Message);
                        _logger.LogError("[MR] [DEBUG] [EPISODE-QUERY-FALLBACK-ERROR] Stack Trace: {StackTrace}", fallbackEx.StackTrace ?? "N/A");
                        _logger.LogError("[MR] [DEBUG] [EPISODE-QUERY-FALLBACK-ERROR] Series: {Name}, ID: {Id}", series.Name ?? "NULL", series.Id);
                        
                        var logData = new {
                            runId = "run1",
                            hypothesisId = "EPISODE-QUERY-FALLBACK-ERROR",
                            location = "RenameCoordinator.cs:1680",
                            message = "Fallback method also failed - cannot retrieve episodes",
                            data = new {
                                seriesId = series.Id.ToString(),
                                seriesName = series.Name ?? "NULL",
                                exceptionType = fallbackEx.GetType().FullName,
                                exceptionMessage = fallbackEx.Message,
                                stackTrace = fallbackEx.StackTrace ?? "N/A",
                                innerException = fallbackEx.InnerException?.Message ?? "N/A"
                            },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-QUERY-FALLBACK-ERROR] ERROR logging fallback error: {Error}", logEx.Message);
                    }
                    // #endregion
                    
                    _logger.LogError(fallbackEx, "[MR] Could not retrieve episodes using fallback method: {Message}", fallbackEx.Message);
                    _logger.LogError("[MR] [DEBUG] Cannot proceed with episode processing - both query methods failed");
                    return;
                }
            }

            _logger.LogInformation("[MR] Found {Count} total episodes in series", allEpisodes.Count);

            // Group episodes by season BEFORE processing to log all seasons found
            var episodesBySeasonForLogging = allEpisodes
                .Where(e => e.ParentIndexNumber.HasValue)
                .GroupBy(e => e.ParentIndexNumber.Value)
                .OrderBy(g => g.Key)
                .ToList();
            
            // Check if we have Season 2+ episodes
            var hasSeason2Plus = episodesBySeasonForLogging.Any(sg => sg.Key >= 2);
            if (hasSeason2Plus)
            {
                _logger.LogWarning("[MR] [DEBUG] [ALL-SEASONS-FOUND] === All Seasons Found in Series (MULTI-SEASON SHOW) ===");
                _logger.LogWarning("[MR] [DEBUG] [ALL-SEASONS-FOUND] Series: {Name}, ID: {Id}", series.Name ?? "NULL", series.Id);
                _logger.LogWarning("[MR] [DEBUG] [ALL-SEASONS-FOUND] Total seasons detected: {SeasonCount} (Season 2+ episodes present!)", episodesBySeasonForLogging.Count);
            }
            else
            {
                _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-FOUND] === All Seasons Found in Series ===");
                _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-FOUND] Series: {Name}, ID: {Id}", series.Name ?? "NULL", series.Id);
                _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-FOUND] Total seasons detected: {SeasonCount}", episodesBySeasonForLogging.Count);
            }
            
            // #region agent log - ALL-SEASONS-FOUND: Track all seasons with episodes
            try
            {
                var logData = new {
                    runId = "run1",
                    hypothesisId = "ALL-SEASONS-FOUND",
                    location = "RenameCoordinator.cs:2170",
                    message = "All seasons with episodes detected",
                    data = new {
                        seriesId = series.Id.ToString(),
                        seriesName = series.Name ?? "NULL",
                        totalSeasonsWithEpisodes = episodesBySeasonForLogging.Count,
                        seasons = episodesBySeasonForLogging.Select(sg => new {
                            seasonNumber = sg.Key,
                            episodeCount = sg.Count()
                        }).ToList()
                    },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "[MR] [DEBUG] [ALL-SEASONS-FOUND] ERROR logging: {Error}", logEx.Message);
            }
            // #endregion
            
            foreach (var seasonGroup in episodesBySeasonForLogging)
            {
                var seasonNum = seasonGroup.Key;
                var isSeason2Plus = seasonNum >= 2;
                if (isSeason2Plus)
                {
                    _logger.LogWarning("[MR] [DEBUG] [ALL-SEASONS-FOUND] Season {Season}: {Count} episodes found (SEASON 2+ - WILL BE PROCESSED)", 
                        seasonNum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        seasonGroup.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-FOUND] Season {Season}: {Count} episodes found", 
                        seasonNum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        seasonGroup.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                
                // #region agent log - MULTI-SEASON-DEBUG: Track Season 2+ detection
                if (isSeason2Plus)
                {
                    try
                    {
                        var logData = new {
                            runId = "run1",
                            hypothesisId = "MULTI-SEASON-DEBUG",
                            location = "RenameCoordinator.cs:2132",
                            message = "Season 2+ detected in query results",
                            data = new {
                                seriesId = series.Id.ToString(),
                                seriesName = series.Name ?? "NULL",
                                seasonNumber = seasonNum,
                                episodeCount = seasonGroup.Count(),
                                episodeIds = seasonGroup.Select(e => e.Id.ToString()).Take(5).ToList() // First 5 episode IDs
                            },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogError(logEx, "[MR] [DEBUG] [MULTI-SEASON-DEBUG] ERROR logging Season 2+ detection: {Error}", logEx.Message);
                    }
                }
                // #endregion
            }

            // #region agent log - EPISODES-FOUND: Track episodes found by ProcessAllEpisodesFromSeries
            try
            {
                var episodesMetadata = allEpisodes.Select(ep => new {
                    episodeId = ep.Id.ToString(),
                    episodeName = ep.Name ?? "NULL",
                    episodePath = ep.Path ?? "NULL",
                    indexNumber = ep.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                    parentIndexNumber = ep.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                    isFilenamePattern = !string.IsNullOrWhiteSpace(ep.Name) && 
                                      System.Text.RegularExpressions.Regex.IsMatch(ep.Name, @"[Ss]\d+[Ee]\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                }).ToList();
                
                _logger.LogWarning("[MR] [DEBUG] [EPISODES-FOUND] ProcessAllEpisodesFromSeries found {TotalEpisodes} episodes for SeriesId={SeriesId}, SeriesName='{SeriesName}'",
                    allEpisodes.Count, series.Id.ToString(), series.Name ?? "NULL");
                
                foreach (var epMeta in episodesMetadata.Take(10)) // Log first 10 episodes to avoid log spam
                {
                    _logger.LogWarning("[MR] [DEBUG] [EPISODES-FOUND] Episode: Id={EpisodeId}, Name='{EpisodeName}', S{Season}E{Episode}, IsFilenamePattern={IsFilenamePattern}",
                        epMeta.episodeId, epMeta.episodeName, epMeta.parentIndexNumber, epMeta.indexNumber, epMeta.isFilenamePattern);
                }
                
                if (episodesMetadata.Count > 10)
                {
                    _logger.LogInformation("[MR] [DEBUG] [EPISODES-FOUND] ... and {RemainingCount} more episodes (total: {Total})",
                        episodesMetadata.Count - 10, episodesMetadata.Count);
                }
                
                var logData = new { 
                    runId = "run1", 
                    hypothesisId = "EPISODES-FOUND", 
                    location = "RenameCoordinator.cs:1135", 
                    message = "Episodes found by ProcessAllEpisodesFromSeries", 
                    data = new { 
                        seriesId = series.Id.ToString(),
                        seriesName = series.Name ?? "NULL",
                        totalEpisodes = allEpisodes.Count,
                        episodes = episodesMetadata
                    }, 
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [EPISODES-FOUND] ERROR logging episodes found: {Error}", ex.Message);
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "EPISODES-FOUND", location = "RenameCoordinator.cs:1135", message = "ERROR logging episodes found", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            }
            // #endregion

            if (allEpisodes.Count == 0)
            {
                _logger.LogInformation("[MR] No episodes found in series. Episodes may not be scanned yet.");
                return;
            }

            // Group episodes by season for logging
            var episodesBySeason = allEpisodes
                .Where(e => e.ParentIndexNumber.HasValue)
                .GroupBy(e => e.ParentIndexNumber.Value)
                .OrderBy(g => g.Key)
                .ToList();

            _logger.LogInformation("[MR] === Episodes by Season (Detailed) ===");
            _logger.LogInformation("[MR] Series: {Name}, ID: {Id}", series.Name ?? "NULL", series.Id);
            _logger.LogInformation("[MR] Total seasons found: {SeasonCount}", episodesBySeason.Count);
            foreach (var seasonGroup in episodesBySeason)
            {
                _logger.LogInformation("[MR]   Season {Season}: {Count} episodes", 
                    seasonGroup.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), 
                    seasonGroup.Count().ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var episode in seasonGroup.OrderBy(e => e.IndexNumber))
                {
                    _logger.LogInformation("[MR]     - S{Season:00}E{Episode:00}: {Name} (ID: {Id}, Path: {Path})", 
                        episode.ParentIndexNumber, 
                        episode.IndexNumber, 
                        episode.Name ?? "NULL", 
                        episode.Id,
                        episode.Path ?? "NULL");
                }
            }
            
            // Log episodes without season numbers
            var episodesWithoutSeasonNumbers2 = allEpisodes.Where(e => !e.ParentIndexNumber.HasValue).ToList();
            if (episodesWithoutSeasonNumbers2.Count > 0)
            {
                _logger.LogWarning("[MR] === Episodes Without Season Numbers ===");
                _logger.LogWarning("[MR] Found {Count} episodes without ParentIndexNumber", episodesWithoutSeasonNumbers2.Count);
                foreach (var ep in episodesWithoutSeasonNumbers2.Take(10))
                {
                    _logger.LogWarning("[MR]   - Episode ID: {Id}, Name: {Name}, IndexNumber: {IndexNumber}, Path: {Path}",
                        ep.Id, ep.Name ?? "NULL", ep.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", ep.Path ?? "NULL");
                }
            }

            // Process each episode - track processing by season
            int processedCount = 0;
            int skippedCount = 0;
            var episodesProcessedBySeason = new Dictionary<int, int>(); // Season number -> count of processed episodes
            var episodesSkippedBySeason = new Dictionary<int, int>(); // Season number -> count of skipped episodes
            
            _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-PROCESSING] === Starting episode processing for ALL seasons ===");
            _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-PROCESSING] Total episodes to process: {Total}", allEpisodes.Count);
            
            foreach (var episode in allEpisodes)
            {
                try
                {
                    var seasonNum = episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
                    var episodeNum = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
                    var episodeName = episode.Name ?? "Unknown";
                    var episodePath = episode.Path ?? "NULL";
                    var seasonNumber = episode.ParentIndexNumber ?? -1;
                    // isSeason2Plus applies to ALL seasons >= 2 (Season 2, 3, 4, 5, etc.)
                    // This enables special handling for multi-season shows (path derivation, relaxed validation, etc.)
                    var isSeason2Plus = seasonNumber >= 2; // Define early for use in validation
                    
                    // Track which season this episode belongs to
                    if (seasonNumber >= 0)
                    {
                        _logger.LogWarning("[MR] [DEBUG] [ALL-SEASONS-PROCESSING] Processing episode from Season {Season}: {Name} (S{Season}E{Episode}) (Season2Plus={IsSeason2Plus})", 
                            seasonNumber, episodeName, seasonNum, episodeNum, isSeason2Plus);
                        
                        // #region agent log - MULTI-SEASON-EPISODE-PROCESSING: Track Season 2+ episode processing
                        if (isSeason2Plus)
                        {
                            try
                            {
                                var logData = new {
                                    runId = "run1",
                                    hypothesisId = "MULTI-SEASON-EPISODE-PROCESSING",
                                    location = "RenameCoordinator.cs:2254",
                                    message = "Processing Season 2+ episode in ProcessAllEpisodesFromSeries",
                                    data = new {
                                        seriesId = series.Id.ToString(),
                                        seriesName = series.Name ?? "NULL",
                                        seasonNumber = seasonNumber,
                                        episodeId = episode.Id.ToString(),
                                        episodeName = episodeName,
                                        episodeNumber = episodeNum,
                                        episodePath = episodePath,
                                        hasValidPath = !string.IsNullOrWhiteSpace(episodePath) && File.Exists(episodePath),
                                        hasIndexNumber = episode.IndexNumber.HasValue,
                                        hasParentIndexNumber = episode.ParentIndexNumber.HasValue
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                };
                                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                            }
                            catch (Exception logEx)
                            {
                                _logger.LogError(logEx, "[MR] [DEBUG] [MULTI-SEASON-EPISODE-PROCESSING] ERROR logging: {Error}", logEx.Message);
                            }
                        }
                        // #endregion
                        
                        // #region agent log - EPISODE-PROCESSING-BY-SEASON: Track episode processing by season
                        try
                        {
                            var logData = new {
                                runId = "run1",
                                hypothesisId = "EPISODE-PROCESSING-BY-SEASON",
                                location = "RenameCoordinator.cs:2244",
                                message = "Processing episode from specific season",
                                data = new {
                                    seriesId = series.Id.ToString(),
                                    seriesName = series.Name ?? "NULL",
                                    seasonNumber = seasonNumber,
                                    episodeId = episode.Id.ToString(),
                                    episodeName = episodeName,
                                    episodeNumber = episodeNum,
                                    episodePath = episodePath
                                },
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            };
                            var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-PROCESSING-BY-SEASON] ERROR logging: {Error}", logEx.Message);
                        }
                        // #endregion
                    }
                    else
                    {
                        _logger.LogWarning("[MR] [DEBUG] [ALL-SEASONS-PROCESSING] WARNING: Episode has no season number: {Name} (Episode: {Episode})", 
                            episodeName, episodeNum);
                    }
                    
                    _logger.LogInformation("[MR] === Processing Episode: {Name} (S{Season}E{Episode}) ===", 
                        episodeName, seasonNum, episodeNum);
                    _logger.LogInformation("[MR] Episode ID: {Id}", episode.Id);
                    _logger.LogInformation("[MR] Episode Path: {Path}", episodePath);
                    _logger.LogInformation("[MR] Season Number: {Season}", seasonNum);
                    _logger.LogInformation("[MR] Episode Number: {Episode}", episodeNum);
                    
                    // Check if episode has a valid path
                    // CRITICAL FIX: For Season 2+ episodes, be more lenient with path validation
                    // Sometimes Jellyfin metadata paths may be stale after folder renames
                    bool hasValidPath = !string.IsNullOrWhiteSpace(episode.Path) && File.Exists(episode.Path);
                    if (!hasValidPath && isSeason2Plus)
                    {
                        // For Season 2+ episodes, try to derive path from series path + season folder
                        if (!string.IsNullOrWhiteSpace(series.Path) && seasonNumber >= 2)
                        {
                            var seasonFolderName = SafeName.RenderSeasonFolder(cfg.SeasonFolderFormat, seasonNumber, null);
                            var potentialSeasonPath = Path.Combine(series.Path, seasonFolderName);
                            if (Directory.Exists(potentialSeasonPath))
                            {
                                // Try to find the episode file in the season folder
                                var episodeFileName = Path.GetFileName(episode.Path ?? episodeName + ".mp4");
                                var potentialEpisodePath = Path.Combine(potentialSeasonPath, episodeFileName);
                                if (File.Exists(potentialEpisodePath))
                                {
                                    _logger.LogWarning("[MR] [DEBUG] [SEASON2+-PATH-FIX] Found Season 2+ episode file using derived path: {Path}", potentialEpisodePath);
                                    // Update episode path for processing
                                    episode.Path = potentialEpisodePath;
                                    episodePath = potentialEpisodePath;
                                    hasValidPath = true;
                                }
                            }
                        }
                    }
                    
                    if (!hasValidPath)
                    {
                        _logger.LogWarning("[MR] SKIP (ProcessAllEpisodes): Episode file does not exist on disk. Path={Path}, EpisodeId={Id}, Name={Name}, Season={Season}, Episode={Episode}, Season2Plus={IsSeason2Plus}", 
                            episodePath, episode.Id, episodeName, seasonNum, episodeNum, isSeason2Plus);
                        skippedCount++;
                        if (seasonNumber >= 0)
                        {
                            episodesSkippedBySeason.TryGetValue(seasonNumber, out var skipped);
                            episodesSkippedBySeason[seasonNumber] = skipped + 1;
                        }
                        continue;
                    }

                    // Check if episode has valid metadata (IndexNumber and ParentIndexNumber)
                    // CRITICAL FIX: For Season 2+ episodes, allow processing even if ParentIndexNumber is missing
                    // as long as we can derive it from the path or it's in a season folder
                    bool hasValidMetadata = episode.IndexNumber.HasValue;
                    bool hasSeasonNumber = episode.ParentIndexNumber.HasValue;
                    
                    // For Season 2+ episodes in season folders, try to derive season number from path
                    if (!hasSeasonNumber && isSeason2Plus && !string.IsNullOrWhiteSpace(episodePath))
                    {
                        var episodeDir = Path.GetDirectoryName(episodePath);
                        if (!string.IsNullOrWhiteSpace(episodeDir))
                        {
                            var dirName = Path.GetFileName(episodeDir);
                            // Try to extract season number from folder name (e.g., "Season 02", "S02", "Season 2")
                            var seasonMatch = System.Text.RegularExpressions.Regex.Match(dirName, @"(?:Season\s*|S)(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (seasonMatch.Success && int.TryParse(seasonMatch.Groups[1].Value, out var extractedSeason))
                            {
                                _logger.LogWarning("[MR] [DEBUG] [SEASON2+-METADATA-FIX] Derived season number {Season} from folder path for episode {EpisodeId}", extractedSeason, episode.Id);
                                // Use reflection to set ParentIndexNumber if possible, or process with derived value
                                hasSeasonNumber = true;
                            }
                        }
                    }
                    
                    if (!hasValidMetadata)
                    {
                        _logger.LogWarning("[MR] SKIP (ProcessAllEpisodes): Episode is missing IndexNumber metadata. EpisodeId={Id}, Name={Name}, Season={Season}, Episode={Episode}, Season2Plus={IsSeason2Plus}", 
                            episode.Id, episodeName, seasonNum, episodeNum, isSeason2Plus);
                        skippedCount++;
                        if (seasonNumber >= 0)
                        {
                            episodesSkippedBySeason.TryGetValue(seasonNumber, out var skipped);
                            episodesSkippedBySeason[seasonNumber] = skipped + 1;
                        }
                        continue;
                    }
                    
                    // For Season 2+ episodes, allow processing even without ParentIndexNumber if we can derive it
                    if (!hasSeasonNumber && !isSeason2Plus)
                    {
                        _logger.LogWarning("[MR] SKIP (ProcessAllEpisodes): Episode is missing ParentIndexNumber metadata. EpisodeId={Id}, Name={Name}, Season={Season}, Episode={Episode}", 
                            episode.Id, episodeName, seasonNum, episodeNum);
                        skippedCount++;
                        if (seasonNumber >= 0)
                        {
                            episodesSkippedBySeason.TryGetValue(seasonNumber, out var skipped);
                            episodesSkippedBySeason[seasonNumber] = skipped + 1;
                        }
                        continue;
                    }

                    // Log episode details before processing
                    _logger.LogInformation("[MR] === Episode Metadata Validated ===");
                    _logger.LogInformation("[MR] Episode: {Name} (S{Season}E{Episode})", episodeName, seasonNum, episodeNum);
                    _logger.LogInformation("[MR] Episode ID: {Id}", episode.Id);
                    _logger.LogInformation("[MR] Episode Path: {Path}", episodePath);
                    _logger.LogInformation("[MR] Series: {SeriesName} (ID: {SeriesId})", episode.Series?.Name ?? "NULL", episode.Series?.Id.ToString() ?? "NULL");
                    _logger.LogInformation("[MR] Series Path: {SeriesPath}", episode.Series?.Path ?? "NULL");
                    _logger.LogInformation("[MR] Proceeding with rename processing...");

                    // Call HandleEpisodeUpdate for each episode
                    // This will apply the renaming logic and safety checks
                    _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES] Calling HandleEpisodeUpdate for S{Season}E{Episode}: {Name}", 
                        seasonNum, episodeNum, episodeName);
                    
                    // #region agent log - EPISODE-PROCESSING-START: Track episode processing start
                    try
                    {
                        var logData = new {
                            runId = "run1",
                            hypothesisId = "EPISODE-PROCESSING-START",
                            location = "RenameCoordinator.cs:2029",
                            message = "Starting HandleEpisodeUpdate for episode",
                            data = new {
                                episodeId = episode.Id.ToString(),
                                episodeName = episodeName,
                                seasonNumber = seasonNum,
                                episodeNumber = episodeNum,
                                episodePath = episodePath,
                                seriesId = episode.Series?.Id.ToString() ?? "NULL",
                                seriesName = episode.Series?.Name ?? "NULL"
                            },
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-PROCESSING-START] ERROR logging processing start: {Error}", logEx.Message);
                    }
                    // #endregion
                    
                    try
                    {
                        if (isSeason2Plus)
                        {
                            _logger.LogWarning("[MR] [DEBUG] [MULTI-SEASON-CALL] About to call HandleEpisodeUpdate for Season 2+ episode: S{Season}E{Episode}: {Name}", 
                                seasonNum, episodeNum, episodeName);
                        }
                        HandleEpisodeUpdate(episode, cfg, now, isBulkProcessing: true);
                        _logger.LogWarning("[MR] [DEBUG] [PROCESS-ALL-EPISODES] HandleEpisodeUpdate completed for S{Season}E{Episode}: {Name} (Season2Plus={IsSeason2Plus})", 
                            seasonNum, episodeNum, episodeName, isSeason2Plus);
                        processedCount++;
                        
                        // #region agent log - MULTI-SEASON-EPISODE-SUCCESS: Track successful Season 2+ processing
                        if (isSeason2Plus)
                        {
                            try
                            {
                                var logData = new {
                                    runId = "run1",
                                    hypothesisId = "MULTI-SEASON-EPISODE-SUCCESS",
                                    location = "RenameCoordinator.cs:2373",
                                    message = "HandleEpisodeUpdate completed for Season 2+ episode",
                                    data = new {
                                        seriesId = series.Id.ToString(),
                                        seriesName = series.Name ?? "NULL",
                                        seasonNumber = seasonNumber,
                                        episodeId = episode.Id.ToString(),
                                        episodeName = episodeName,
                                        episodeNumber = episodeNum
                                    },
                                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                };
                                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                            }
                            catch (Exception logEx)
                            {
                                _logger.LogError(logEx, "[MR] [DEBUG] [MULTI-SEASON-EPISODE-SUCCESS] ERROR logging: {Error}", logEx.Message);
                            }
                        }
                        // #endregion
                        
                        // #region agent log - EPISODE-PROCESSING-SUCCESS: Track successful episode processing
                        try
                        {
                            var logData = new {
                                runId = "run1",
                                hypothesisId = "EPISODE-PROCESSING-SUCCESS",
                                location = "RenameCoordinator.cs:2033",
                                message = "HandleEpisodeUpdate completed successfully",
                                data = new {
                                    episodeId = episode.Id.ToString(),
                                    episodeName = episodeName,
                                    seasonNumber = seasonNum,
                                    episodeNumber = episodeNum
                                },
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            };
                            var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-PROCESSING-SUCCESS] ERROR logging success: {Error}", logEx.Message);
                        }
                        // #endregion
                    }
                    catch (Exception episodeEx)
                    {
                        // #region agent log - EPISODE-PROCESSING-ERROR: Track episode processing errors
                        try
                        {
                            _logger.LogError(episodeEx, "[MR] [DEBUG] [EPISODE-PROCESSING-ERROR] ERROR processing episode S{Season}E{Episode}: {Name} (Season2Plus={IsSeason2Plus})", 
                                seasonNum, episodeNum, episodeName, isSeason2Plus);
                            
                            // #region agent log - MULTI-SEASON-EPISODE-ERROR: Track Season 2+ errors specifically
                            if (isSeason2Plus)
                            {
                                try
                                {
                                    var errorLogData = new {
                                        runId = "run1",
                                        hypothesisId = "MULTI-SEASON-EPISODE-ERROR",
                                        location = "RenameCoordinator.cs:2403",
                                        message = "ERROR processing Season 2+ episode",
                                        data = new {
                                            seriesId = series.Id.ToString(),
                                            seriesName = series.Name ?? "NULL",
                                            seasonNumber = seasonNumber,
                                            episodeId = episode.Id.ToString(),
                                            episodeName = episodeName,
                                            episodeNumber = episodeNum,
                                            exceptionType = episodeEx.GetType().FullName,
                                            exceptionMessage = episodeEx.Message,
                                            stackTrace = episodeEx.StackTrace ?? "N/A"
                                        },
                                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                    };
                                    var errorLogJson = System.Text.Json.JsonSerializer.Serialize(errorLogData) + "\n";
                                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", errorLogJson); } catch { }
                                }
                                catch (Exception logEx)
                                {
                                    _logger.LogError(logEx, "[MR] [DEBUG] [MULTI-SEASON-EPISODE-ERROR] ERROR logging: {Error}", logEx.Message);
                                }
                            }
                            // #endregion
                            _logger.LogError("[MR] [DEBUG] [EPISODE-PROCESSING-ERROR] Exception Type: {Type}", episodeEx.GetType().FullName);
                            _logger.LogError("[MR] [DEBUG] [EPISODE-PROCESSING-ERROR] Exception Message: {Message}", episodeEx.Message);
                            _logger.LogError("[MR] [DEBUG] [EPISODE-PROCESSING-ERROR] Stack Trace: {StackTrace}", episodeEx.StackTrace ?? "N/A");
                            _logger.LogError("[MR] [DEBUG] [EPISODE-PROCESSING-ERROR] Inner Exception: {InnerException}", episodeEx.InnerException?.Message ?? "N/A");
                            _logger.LogError("[MR] [DEBUG] [EPISODE-PROCESSING-ERROR] Episode ID: {Id}, Path: {Path}", episode.Id, episodePath);
                            
                            var logData = new {
                                runId = "run1",
                                hypothesisId = "EPISODE-PROCESSING-ERROR",
                                location = "RenameCoordinator.cs:2030",
                                message = "Error in HandleEpisodeUpdate",
                                data = new {
                                    episodeId = episode.Id.ToString(),
                                    episodeName = episodeName,
                                    seasonNumber = seasonNum,
                                    episodeNumber = episodeNum,
                                    episodePath = episodePath,
                                    exceptionType = episodeEx.GetType().FullName,
                                    exceptionMessage = episodeEx.Message,
                                    stackTrace = episodeEx.StackTrace ?? "N/A",
                                    innerException = episodeEx.InnerException?.Message ?? "N/A"
                                },
                                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            };
                            var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-PROCESSING-ERROR] ERROR logging processing error: {Error}", logEx.Message);
                        }
                        // #endregion
                        
                        skippedCount++;
                        if (seasonNumber >= 0)
                        {
                            episodesSkippedBySeason.TryGetValue(seasonNumber, out var skipped);
                            episodesSkippedBySeason[seasonNumber] = skipped + 1;
                        }
                    }
                    
                    // Track processed episodes by season
                    if (seasonNumber >= 0)
                    {
                        episodesProcessedBySeason.TryGetValue(seasonNumber, out var processed);
                        episodesProcessedBySeason[seasonNumber] = processed + 1;
                        _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-PROCESSING] Season {Season} progress: {Processed} processed, {Skipped} skipped", 
                            seasonNumber, 
                            episodesProcessedBySeason[seasonNumber],
                            episodesSkippedBySeason.TryGetValue(seasonNumber, out var skipped) ? skipped : 0);
                    }
                    
                    _logger.LogInformation("[MR] ✓ Successfully processed episode: {Name} (S{Season}E{Episode})", 
                        episodeName, seasonNum, episodeNum);
                }
                catch (Exception ex)
                {
                    var errorSeasonNumber = episode.ParentIndexNumber ?? -1;
                    _logger.LogError(ex, "[MR] ERROR processing episode {EpisodeName} (ID: {EpisodeId}, S{Season}E{Episode}) during bulk series update: {Message}", 
                        episode.Name ?? "Unknown", episode.Id, 
                        episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "??",
                        episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "??",
                        ex.Message);
                    skippedCount++;
                    if (errorSeasonNumber >= 0)
                    {
                        episodesSkippedBySeason.TryGetValue(errorSeasonNumber, out var skipped);
                        episodesSkippedBySeason[errorSeasonNumber] = skipped + 1;
                    }
                }
            }

            // #region agent log - ALL-SEASONS-SUMMARY: Track processing summary by season
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-SUMMARY] === Episode Processing Summary by Season ===");
                _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-SUMMARY] Total episodes found: {Total}", allEpisodes.Count);
                _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-SUMMARY] Total processed: {Processed}", processedCount);
                _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-SUMMARY] Total skipped: {Skipped}", skippedCount);
                
                foreach (var seasonGroup in episodesBySeasonForLogging)
                {
                    var seasonNum = seasonGroup.Key;
                    var totalInSeason = seasonGroup.Count();
                    var processedInSeason = episodesProcessedBySeason.TryGetValue(seasonNum, out var proc) ? proc : 0;
                    var skippedInSeason = episodesSkippedBySeason.TryGetValue(seasonNum, out var skip) ? skip : 0;
                    
                    _logger.LogInformation("[MR] [DEBUG] [ALL-SEASONS-SUMMARY] Season {Season}: {Total} total, {Processed} processed, {Skipped} skipped", 
                        seasonNum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        totalInSeason,
                        processedInSeason,
                        skippedInSeason);
                }
                
                var logData = new { 
                    runId = "run1", 
                    hypothesisId = "ALL-SEASONS-SUMMARY", 
                    location = "RenameCoordinator.cs:1805", 
                    message = "Episode processing summary by season", 
                    data = new { 
                        seriesId = series.Id.ToString(),
                        seriesName = series.Name ?? "NULL",
                        totalEpisodes = allEpisodes.Count,
                        totalProcessed = processedCount,
                        totalSkipped = skippedCount,
                        seasons = episodesBySeasonForLogging.Select(sg => new {
                            seasonNumber = sg.Key,
                            totalEpisodes = sg.Count(),
                            processed = episodesProcessedBySeason.TryGetValue(sg.Key, out var p) ? p : 0,
                            skipped = episodesSkippedBySeason.TryGetValue(sg.Key, out var s) ? s : 0
                        }).ToList()
                    }, 
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [ALL-SEASONS-SUMMARY] ERROR logging summary: {Error}", ex.Message);
            }
            // #endregion

            _logger.LogInformation("[MR] === Episode Processing Summary ===");
            _logger.LogInformation("[MR] Total episodes found: {Total}", allEpisodes.Count);
            _logger.LogInformation("[MR] Successfully processed: {Processed}", processedCount);
            _logger.LogInformation("[MR] Skipped: {Skipped}", skippedCount);
            _logger.LogInformation("[MR] === All Episodes Processing Complete ===");
            
            // #region agent log - PROCESS-ALL-EPISODES-COMPLETE: Track ProcessAllEpisodesFromSeries completion
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES-COMPLETE] ===== ProcessAllEpisodesFromSeries COMPLETE =====");
                _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES-COMPLETE] Series: {Name}, ID: {Id}", series.Name ?? "NULL", series.Id);
                _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES-COMPLETE] Total episodes found: {Total}, Processed: {Processed}, Skipped: {Skipped}",
                    allEpisodes.Count, processedCount, skippedCount);
                
                var logData = new { 
                    runId = "run1", 
                    hypothesisId = "PROCESS-ALL-EPISODES-COMPLETE", 
                    location = "RenameCoordinator.cs:1575", 
                    message = "ProcessAllEpisodesFromSeries complete", 
                    data = new { 
                        seriesId = series.Id.ToString(),
                        seriesName = series.Name ?? "NULL",
                        totalEpisodes = allEpisodes.Count,
                        processedCount = processedCount,
                        skippedCount = skippedCount
                    }, 
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [PROCESS-ALL-EPISODES-COMPLETE] ERROR logging completion: {Error}", ex.Message);
            }
            // #endregion

            // Process retry queue after processing all episodes
            ProcessRetryQueue(cfg, now);
        }
        catch (Exception ex)
        {
            // #region agent log - PROCESS-ALL-EPISODES-ERROR: Track errors in ProcessAllEpisodesFromSeries
            try
            {
                _logger.LogError(ex, "[MR] [DEBUG] [PROCESS-ALL-EPISODES-ERROR] ERROR in ProcessAllEpisodesFromSeries");
                _logger.LogError("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ERROR] Exception Type: {Type}", ex.GetType().FullName);
                _logger.LogError("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ERROR] Exception Message: {Message}", ex.Message);
                _logger.LogError("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ERROR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
                _logger.LogError("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ERROR] Inner Exception: {InnerException}", ex.InnerException?.Message ?? "N/A");
                _logger.LogError("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ERROR] Series: {Name}, ID: {Id}, Path: {Path}", 
                    series.Name ?? "NULL", series.Id, series.Path ?? "NULL");
                
                var logData = new {
                    runId = "run1",
                    hypothesisId = "PROCESS-ALL-EPISODES-ERROR",
                    location = "RenameCoordinator.cs:1551",
                    message = "Error in ProcessAllEpisodesFromSeries",
                    data = new {
                        seriesId = series.Id.ToString(),
                        seriesName = series.Name ?? "NULL",
                        seriesPath = series.Path ?? "NULL",
                        exceptionType = ex.GetType().FullName,
                        exceptionMessage = ex.Message,
                        stackTrace = ex.StackTrace ?? "N/A",
                        innerException = ex.InnerException?.Message ?? "N/A"
                    },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "[MR] [DEBUG] [PROCESS-ALL-EPISODES-ERROR] ERROR logging process error: {Error}", logEx.Message);
            }
            // #endregion
            
            _logger.LogError(ex, "[MR] ERROR in ProcessAllEpisodesFromSeries: {Message}", ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
        }
    }

    /// <summary>
    /// Processes all series in the library for bulk refresh (Replace all metadata).
    /// </summary>
    private void ProcessAllSeriesInLibrary(PluginConfiguration cfg, DateTime now)
    {
        try
        {
            // #region agent log - PROCESS-ALL-SERIES-ENTRY: Track when ProcessAllSeriesInLibrary is called
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SERIES-ENTRY] ===== ProcessAllSeriesInLibrary ENTRY =====");
                _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SERIES-ENTRY] This method processes ALL series in the library");
                _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-SERIES-ENTRY] Called at: {Timestamp}", now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));
                
                var logData = new {
                    runId = "run1",
                    hypothesisId = "PROCESS-ALL-SERIES-ENTRY",
                    location = "RenameCoordinator.cs:1911",
                    message = "ProcessAllSeriesInLibrary called - will process all series",
                    data = new {
                        timestamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
                        libraryManagerAvailable = _libraryManager != null
                    },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [PROCESS-ALL-SERIES-ENTRY] ERROR logging entry: {Error}", ex.Message);
            }
            // #endregion
            
            if (_libraryManager == null)
            {
                _logger.LogWarning("[MR] LibraryManager is not available. Cannot process all series in library.");
                return;
            }

            _logger.LogInformation("[MR] === Processing All Series in Library (Bulk Refresh) ===");
            _logger.LogInformation("[MR] This is triggered when 'Replace all metadata' is detected.");

            // Get all series from the library
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Series },
                Recursive = true
            };

            var allSeries = _libraryManager.GetItemList(query).OfType<Series>().ToList();

            _logger.LogInformation("[MR] Found {Count} series in library", allSeries.Count);

            if (allSeries.Count == 0)
            {
                _logger.LogInformation("[MR] No series found in library.");
                return;
            }

            int processedCount = 0;
            int skippedCount = 0;

            foreach (var series in allSeries)
            {
                try
                {
                    _logger.LogInformation("[MR] === Processing Series: {Name} (ID: {Id}) ===", series.Name ?? "Unknown", series.Id);

                    // Check if series has required metadata
                    if (string.IsNullOrWhiteSpace(series.Path) || !Directory.Exists(series.Path))
                    {
                        _logger.LogWarning("[MR] SKIP (Bulk): Series path does not exist. Path={Path}, SeriesId={Id}, Name={Name}",
                            series.Path ?? "NULL", series.Id, series.Name ?? "Unknown");
                        skippedCount++;
                        continue;
                    }

                    if (cfg.RequireProviderIdMatch && (series.ProviderIds == null || series.ProviderIds.Count == 0))
                    {
                        _logger.LogInformation("[MR] SKIP (Bulk): Series has no provider IDs. SeriesId={Id}, Name={Name}",
                            series.Id, series.Name ?? "Unknown");
                        skippedCount++;
                        continue;
                    }

                    // Process the series (this will rename folder and episodes if needed)
                    // Force processing for bulk refresh - process even if provider IDs haven't changed
                    HandleSeriesUpdate(series, cfg, now, forceProcessing: true);
                    processedCount++;

                    _logger.LogInformation("[MR] ✓ Successfully processed series: {Name}", series.Name ?? "Unknown");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MR] ERROR processing series {SeriesName} (ID: {SeriesId}) during bulk refresh: {Message}",
                        series.Name ?? "Unknown", series.Id, ex.Message);
                    skippedCount++;
                }
            }

            _logger.LogInformation("[MR] === Bulk Processing Complete ===");
            _logger.LogInformation("[MR] Total series found: {Total}", allSeries.Count);
            _logger.LogInformation("[MR] Successfully processed: {Processed}", processedCount);
            _logger.LogInformation("[MR] Skipped: {Skipped}", skippedCount);
            _logger.LogInformation("[MR] === Bulk Refresh Complete ===");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] ERROR in ProcessAllSeriesInLibrary: {Message}", ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
        }
    }

    /// <summary>
    /// Handles movie folder renaming.
    /// </summary>
    private void HandleMovieUpdate(Movie movie, PluginConfiguration cfg, DateTime now)
    {
        // Defensive check: Ensure RenameMovieFolders is enabled
        if (!cfg.RenameMovieFolders)
        {
            _logger.LogInformation("[MR] [DEBUG] [MOVIE-UPDATE-SKIP] SKIP: RenameMovieFolders is disabled in configuration. Movie: {Name}, Id={Id}", 
                movie.Name ?? "NULL", movie.Id);
            return;
        }
        
        _logger.LogInformation("[MR] Processing Movie: Name={Name}, Id={Id}, Path={Path}", movie.Name, movie.Id, movie.Path);

        // Per-item cooldown
        if (_lastAttemptUtcByItem.TryGetValue(movie.Id, out var lastTry))
        {
            var timeSinceLastTry = (now - lastTry).TotalSeconds;
            if (timeSinceLastTry < cfg.PerItemCooldownSeconds)
            {
                _logger.LogInformation(
                    "[MR] SKIP: Cooldown active. MovieId={Id}, Name={Name}, Time since last try: {Seconds} seconds (cooldown: {CooldownSeconds})",
                    movie.Id, movie.Name, timeSinceLastTry.ToString(System.Globalization.CultureInfo.InvariantCulture), cfg.PerItemCooldownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
        }

        _lastAttemptUtcByItem[movie.Id] = now;

        var path = movie.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("[MR] SKIP: Movie has no path. MovieId={Id}, Name={Name}", movie.Id, movie.Name);
            return;
        }

        // For movies, Path points to the movie file, not the folder
        // We need to get the directory containing the movie file
        var movieDirectory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(movieDirectory) || !Directory.Exists(movieDirectory))
        {
            _logger.LogWarning("[MR] SKIP: Movie directory does not exist on disk. Path={Path}, MovieId={Id}, Name={Name}", path, movie.Id, movie.Name);
            return;
        }

        _logger.LogInformation("[MR] Movie directory verified: {Path}", movieDirectory);

        // Check provider IDs - log ALL provider IDs in detail for debugging
        var providerIdsCount = movie.ProviderIds?.Count ?? 0;
        var providerIdsString = movie.ProviderIds != null
            ? string.Join(", ", movie.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}"))
            : "NONE";

        _logger.LogInformation("[MR] === Provider IDs Details (Movie) ===");
        _logger.LogInformation("[MR] Provider IDs Count: {Count}", providerIdsCount);
        _logger.LogInformation("[MR] All Provider IDs: {Values}", providerIdsString);
        
        // Log each provider ID individually for clarity
        if (movie.ProviderIds != null && movie.ProviderIds.Count > 0)
        {
            foreach (var kv in movie.ProviderIds)
            {
                _logger.LogInformation("[MR]   - {Provider}: {Id}", kv.Key, kv.Value ?? "NULL");
            }
        }
        else
        {
            _logger.LogWarning("[MR]   - No provider IDs found!");
        }

        // Must be matched
        if (cfg.RequireProviderIdMatch)
        {
            if (movie.ProviderIds == null || movie.ProviderIds.Count == 0)
            {
                _logger.LogWarning("[MR] SKIP: RequireProviderIdMatch is true but no ProviderIds found. Name={Name}", movie.Name);
                return;
            }
            _logger.LogInformation("[MR] Provider ID requirement satisfied");
        }

        var name = movie.Name?.Trim();

        // Try ProductionYear first, then PremiereDate as fallback
        int? year = movie.ProductionYear;
        string yearSource = "ProductionYear";

        if (year is null && movie.PremiereDate.HasValue)
        {
            year = movie.PremiereDate.Value.Year;
            yearSource = "PremiereDate";
        }

        _logger.LogInformation("[MR] === Year Detection from Metadata (BEFORE Correction - Movie) ===");
        _logger.LogInformation("[MR] Movie: {Name}, ID: {Id}", name ?? "NULL", movie.Id);
        _logger.LogInformation("[MR] ProductionYear (from Jellyfin): {ProductionYear}", 
            movie.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
        _logger.LogInformation("[MR] PremiereDate (from Jellyfin): {PremiereDate}, Year: {PremiereDateYear}", 
            movie.PremiereDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
            movie.PremiereDate?.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
        _logger.LogInformation("[MR] Year from Metadata (BEFORE correction): {Year} (Source: {YearSource})", 
            year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
            yearSource);
        _logger.LogInformation("[MR] Movie metadata: Name={Name}, Year={Year} (from {YearSource}), ProviderIds={ProviderIds}",
            name ?? "NULL",
            year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
            yearSource,
            providerIdsString);

        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.LogWarning("[MR] SKIP: Missing required metadata. Name={Name}", name ?? "NULL");
            return;
        }

        // Check if we should process during library scans (same logic as series)
        var hasProviderIds = movie.ProviderIds != null && movie.ProviderIds.Count > 0;
        var newHash = hasProviderIds && movie.ProviderIds != null
            ? ProviderIdHelper.ComputeProviderHash(movie.ProviderIds)
            : string.Empty;

        var hasOldHash = _providerHashByItem.TryGetValue(movie.Id, out var oldHash);
        var providerIdsChanged = hasOldHash && !string.Equals(newHash, oldHash, StringComparison.Ordinal);
        var isFirstTime = !hasOldHash;

        _logger.LogInformation("[MR] === Provider Hash Check (Movie) ===");
        _logger.LogInformation("[MR] ProcessDuringLibraryScans: {ProcessDuringLibraryScans}", cfg.ProcessDuringLibraryScans);
        _logger.LogInformation("[MR] OnlyRenameWhenProviderIdsChange: {OnlyRenameWhenProviderIdsChange}", cfg.OnlyRenameWhenProviderIdsChange);
        _logger.LogInformation("[MR] Has Provider IDs: {HasProviderIds}", hasProviderIds);
        _logger.LogInformation("[MR] Old Hash Exists: {HasOldHash}, Value: {OldHash}", hasOldHash, oldHash ?? "(none)");
        _logger.LogInformation("[MR] New Hash: {NewHash}", newHash);
        _logger.LogInformation("[MR] Provider IDs Changed: {Changed}, First Time: {FirstTime}", providerIdsChanged, isFirstTime);
        _logger.LogInformation("[MR] Movie: {Name}", name);

        // Determine if we should proceed with rename (same logic as series)
        bool shouldProceed = false;
        string proceedReason = string.Empty;

        if (cfg.OnlyRenameWhenProviderIdsChange)
        {
            if (providerIdsChanged || isFirstTime)
            {
                shouldProceed = true;
                proceedReason = providerIdsChanged ? "Provider IDs changed (Identify flow)" : "First time processing";
            }
            else if (cfg.ProcessDuringLibraryScans)
            {
                shouldProceed = true;
                proceedReason = "ProcessDuringLibraryScans enabled (library scan)";
            }
            else
            {
                shouldProceed = false;
                proceedReason = "Provider IDs unchanged and ProcessDuringLibraryScans disabled";
            }
        }
        else
        {
            shouldProceed = true;
            proceedReason = "OnlyRenameWhenProviderIdsChange disabled";
        }

        if (!shouldProceed)
        {
            _logger.LogWarning("[MR] SKIP: {Reason}. Name={Name}, Hash={Hash}", proceedReason, name, newHash);
            _logger.LogInformation("[MR] ===== Processing Complete (Skipped) =====");
            return;
        }

        // Handle provider IDs - use if available, otherwise use empty values
        string providerLabel = string.Empty;
        string providerId = string.Empty;

        // SMART DETECTION: Before storing new provider IDs, check what changed
        // This allows us to detect which provider the user selected in "Identify"
        Dictionary<string, string>? previousMovieProviderIds = null;
        if (hasProviderIds && movie.ProviderIds != null)
        {
            // Get previous provider IDs BEFORE storing new ones
            _previousProviderIdsByItem.TryGetValue(movie.Id, out previousMovieProviderIds);
        }

        if (movie.ProviderIds != null && movie.ProviderIds.Count > 0)
        {
            // SMART DETECTION: If provider IDs changed, detect which one was newly added/changed
            // This represents the provider the user selected in "Identify"
            string? selectedProviderKey = null;
            string? selectedProviderId = null;
            
            if (providerIdsChanged && previousMovieProviderIds != null)
            {
                // Compare old vs new to find what changed
                _logger.LogInformation("[MR] === Detecting User-Selected Provider (Identify - Movie) ===");
                
                foreach (var kv in movie.ProviderIds)
                {
                    var providerKey = kv.Key;
                    var providerValue = kv.Value;
                    
                    // Check if this provider ID is new or changed
                    if (!previousMovieProviderIds.TryGetValue(providerKey, out var oldValue) || oldValue != providerValue)
                    {
                        selectedProviderKey = providerKey;
                        selectedProviderId = providerValue;
                        _logger.LogInformation("[MR] ✓ Detected newly added/changed provider: {Provider}={Id} (user selected this in Identify)", 
                            providerKey, providerValue);
                        break; // Use the first changed provider as the selected one
                    }
                }
                
                if (selectedProviderKey == null)
                {
                    _logger.LogInformation("[MR] No newly added/changed provider detected (all IDs were already present)");
                }
            }
            else if (isFirstTime)
            {
                _logger.LogInformation("[MR] First time processing - cannot detect user selection, will use preferred provider");
            }
            
            // If we detected a user-selected provider, use it; otherwise fall back to preferred list
            if (selectedProviderKey != null && selectedProviderId != null)
            {
                providerLabel = selectedProviderKey.Trim().ToLowerInvariant();
                providerId = selectedProviderId.Trim();
                
                // Log year before correction
                var yearBeforeCorrection = year;
                
                // Validate and correct year now that we have the provider ID
                year = ValidateAndCorrectYear(year, providerLabel, providerId, name, movieDirectory);
                
                // Log year after correction
                if (yearBeforeCorrection != year)
                {
                    _logger.LogInformation("[MR] === Year Correction Applied (Movie) ===");
                    _logger.LogInformation("[MR] Year BEFORE correction: {BeforeYear}", 
                        yearBeforeCorrection?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                    _logger.LogInformation("[MR] Year AFTER correction: {AfterYear}", 
                        year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                }
                
                _logger.LogInformation("[MR] === Provider Selection (User-Selected - Movie) ===");
                _logger.LogInformation("[MR] Using user-selected provider from Identify: {Provider}={Id}", providerLabel, providerId);
            }
            else
            {
                // Fall back to preferred provider list
                var preferredProviders = cfg.PreferredMovieProviders != null
                    ? string.Join(", ", cfg.PreferredMovieProviders)
                    : "NONE";
                _logger.LogInformation("[MR] Preferred Providers: {Providers}", preferredProviders);

                var preferredList = cfg.PreferredMovieProviders != null
                    ? cfg.PreferredMovieProviders
                    : new System.Collections.ObjectModel.Collection<string>();
                var best = ProviderIdHelper.GetBestProvider(movie.ProviderIds, preferredList);
                if (best != null)
                {
                    providerLabel = best.Value.ProviderLabel;
                    providerId = best.Value.Id;
                    
                    // Log year before correction
                    var yearBeforeCorrection = year;
                    
                    // Validate and correct year now that we have the provider ID
                    year = ValidateAndCorrectYear(year, providerLabel, providerId, name, movieDirectory);
                    
                    // Log year after correction
                    if (yearBeforeCorrection != year)
                    {
                        _logger.LogInformation("[MR] === Year Correction Applied (Movie) ===");
                        _logger.LogInformation("[MR] Year BEFORE correction: {BeforeYear}", 
                            yearBeforeCorrection?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                        _logger.LogInformation("[MR] Year AFTER correction: {AfterYear}", 
                            year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                    }
                    
                    _logger.LogInformation("[MR] === Provider Selection (Preferred List - Movie) ===");
                    _logger.LogInformation("[MR] Selected Provider: {Provider}={Id} (from preferred list: {PreferredList})", 
                        providerLabel, providerId, string.Join(", ", preferredList));
                    
                    // Warn if multiple provider IDs exist - might indicate conflicting matches
                    if (movie.ProviderIds.Count > 1)
                    {
                        _logger.LogWarning("[MR] ⚠️ WARNING: Multiple provider IDs detected ({Count}). Selected: {SelectedProvider}={SelectedId}", 
                            movie.ProviderIds.Count, providerLabel, providerId);
                        _logger.LogWarning("[MR] ⚠️ If the wrong ID was selected, check your 'Preferred Movie Providers' setting in plugin configuration.");
                        _logger.LogWarning("[MR] ⚠️ Current preference order: {Order}", string.Join(" > ", preferredList));
                    }
                }
                else
                {
                    _logger.LogWarning("[MR] No matching provider found in preferred list");
                }
            }
        }
        
        // Update hash AFTER processing (so we can compare next time)
        _logger.LogInformation("[MR] ✓ Proceeding with rename. Reason: {Reason}", proceedReason);
        _logger.LogInformation("[MR] Old Hash: {OldHash}, New Hash: {NewHash}", oldHash ?? "(none)", newHash);
        if (hasProviderIds && movie.ProviderIds != null)
        {
            _providerHashByItem[movie.Id] = newHash;
            // Store current provider IDs for next comparison
            _previousProviderIdsByItem[movie.Id] = new Dictionary<string, string>(movie.ProviderIds);
        }
        
        if (movie.ProviderIds == null || movie.ProviderIds.Count == 0)
        {
            if (cfg.RequireProviderIdMatch)
            {
                _logger.LogWarning("[MR] SKIP: RequireProviderIdMatch is true but no ProviderIds found. Name={Name}", name);
                _logger.LogInformation("[MR] ===== Processing Complete (Skipped) =====");
                return;
            }
        }
        else
        {
            _logger.LogInformation("[MR] No ProviderIds but RequireProviderIdMatch is false - renaming to help identification");
        }

        // Build final desired folder name: Name (Year) [provider-id] or Name (Year) if no provider IDs
        var currentFolderName = Path.GetFileName(movieDirectory);
        
        _logger.LogInformation("[MR] === Folder Name Generation (Movie) ===");
        _logger.LogInformation("[MR] Format: {Format}", cfg.MovieFolderFormat);
        _logger.LogInformation("[MR] FINAL Year (AFTER correction): {Year} (Original Source: {YearSource})", 
            year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
            yearSource);
        _logger.LogInformation("[MR] Values: Name={Name}, Year={Year}, Provider={Provider}, ID={Id}", 
            name ?? "NULL",
            year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
            providerLabel ?? "NULL",
            providerId ?? "NULL");
        
        var desiredFolderName = SafeName.RenderMovieFolder(
            cfg.MovieFolderFormat,
            name,
            year,
            providerLabel,
            providerId);

        _logger.LogInformation("[MR] Desired folder name: {DesiredName} (year available: {HasYear})", desiredFolderName, year.HasValue);

        _logger.LogInformation("[MR] === Movie Folder Rename Details ===");
        _logger.LogInformation("[MR] Current Folder: {Current}", currentFolderName);
        _logger.LogInformation("[MR] Desired Folder: {Desired}", desiredFolderName);
        _logger.LogInformation("[MR] Full Current Path: {Path}", movieDirectory);
        _logger.LogInformation("[MR] Format: {Format}", cfg.MovieFolderFormat);
        _logger.LogInformation("[MR] Dry Run Mode: {DryRun}", cfg.DryRun);

        _pathRenamer.TryRenameMovieFolder(movie, desiredFolderName, cfg.DryRun);

        _logger.LogInformation("[MR] ===== Movie Processing Complete =====");
    }

    /// <summary>
    /// Scans the series folder for episode files in the root and moves them to "Season 01".
    /// This is called after a successful series folder rename to ensure episodes are properly organized.
    /// Only processes episodes if they are in the series root AND no season folders already exist.
    /// </summary>
    /// <summary>
    /// Fixes nested season folder structures (e.g., "Dororo - Season 1\Season 01").
    /// Moves episodes from nested folders up to the parent and renames the parent to the desired format.
    /// </summary>
    private void FixNestedSeasonFolders(string seriesPath, PluginConfiguration cfg)
    {
        try
        {
            // #region agent log - FixNestedSeasonFolders Entry
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "NESTED-SEASON", location = "RenameCoordinator.cs:900", message = "FixNestedSeasonFolders called", data = new { seriesPath = seriesPath ?? "NULL", pathExists = !string.IsNullOrWhiteSpace(seriesPath) && Directory.Exists(seriesPath) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            
            if (string.IsNullOrWhiteSpace(seriesPath) || !Directory.Exists(seriesPath))
            {
                return;
            }

            _logger.LogInformation("[MR] === Checking for nested season folders ===");
            _logger.LogInformation("[MR] Series path: {Path}", seriesPath);

            // Pattern 1: Standard season folders (Season 1, Season 01, S1, etc.)
            var standardSeasonPattern = new System.Text.RegularExpressions.Regex(
                @"^(Season\s*\d+|S\d+|Season\s*\d{2,})$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            // Pattern 2: Folders containing "Season" or "S" followed by a number (e.g., "Dororo - Season 1")
            var containsSeasonPattern = new System.Text.RegularExpressions.Regex(
                @".*\b(Season|S)\s*\d+.*",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var subdirectories = Directory.GetDirectories(seriesPath);
            var seasonFolders = new List<string>();
            
            foreach (var dir in subdirectories)
            {
                var dirName = Path.GetFileName(dir);
                if (standardSeasonPattern.IsMatch(dirName) || containsSeasonPattern.IsMatch(dirName))
                {
                    seasonFolders.Add(dir);
                }
            }

            if (seasonFolders.Count == 0)
            {
                return; // No season folders to check
            }

            _logger.LogInformation("[MR] Found {Count} season folder(s). Checking for nested structures...", seasonFolders.Count);
            
            // Check if any season folders need to be renamed to match the format
            var desiredSeason1Name = SafeName.RenderSeasonFolder(cfg.SeasonFolderFormat, 1, null);
            var desiredSeason1Path = Path.Combine(seriesPath, desiredSeason1Name);
            
            foreach (var seasonFolder in seasonFolders)
            {
                var seasonFolderName = Path.GetFileName(seasonFolder);
                
                // If this looks like Season 1 but has a different name, check if we should rename it
                if (containsSeasonPattern.IsMatch(seasonFolderName) && 
                    !standardSeasonPattern.IsMatch(seasonFolderName) &&
                    System.Text.RegularExpressions.Regex.IsMatch(seasonFolderName, @"\b(Season|S)\s*1\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    // This is a Season 1 folder with a non-standard name (e.g., "Dororo - Season 1")
                    // Check if there's a nested "Season 01" folder inside it
                    var nestedSeason01Path = Path.Combine(seasonFolder, desiredSeason1Name);
                    // #region agent log - Nested Folder Check
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "NESTED-SEASON", location = "RenameCoordinator.cs:956", message = "Checking for nested season folder", data = new { seasonFolderName = seasonFolderName, desiredSeason1Name = desiredSeason1Name, nestedPath = nestedSeason01Path, nestedExists = Directory.Exists(nestedSeason01Path) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    // #endregion
                    if (Directory.Exists(nestedSeason01Path))
                    {
                        _logger.LogInformation("[MR] Found nested '{DesiredName}' folder inside '{CurrentName}'. Moving episodes up and removing nested folder...", 
                            desiredSeason1Name, seasonFolderName);
                        
                        // Move all files from nested "Season 01" to the parent season folder
                        var nestedFiles = Directory.GetFiles(nestedSeason01Path);
                        foreach (var file in nestedFiles)
                        {
                            var fileName = Path.GetFileName(file);
                            var targetPath = Path.Combine(seasonFolder, fileName);
                            
                            if (File.Exists(targetPath))
                            {
                                _logger.LogWarning("[MR] SKIP: Target file already exists. File: {FileName}", fileName);
                                continue;
                            }
                            
                            try
                            {
                                File.Move(file, targetPath);
                                _logger.LogInformation("[MR] ✓ Moved episode from nested folder: {FileName}", fileName);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "[MR] ERROR: Failed to move file. File: {FileName}, Error: {Error}", fileName, ex.Message);
                            }
                        }
                        
                        // Remove the nested "Season 01" folder if it's empty
                        try
                        {
                            if (Directory.GetFiles(nestedSeason01Path).Length == 0 && 
                                Directory.GetDirectories(nestedSeason01Path).Length == 0)
                            {
                                Directory.Delete(nestedSeason01Path);
                                _logger.LogInformation("[MR] ✓ Removed empty nested folder: {Path}", nestedSeason01Path);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[MR] Could not remove nested folder (may not be empty): {Path}", nestedSeason01Path);
                        }
                    }
                    
                    // Rename the season folder to match the desired format
                    if (!string.Equals(seasonFolderName, desiredSeason1Name, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            Directory.Move(seasonFolder, desiredSeason1Path);
                            _logger.LogInformation("[MR] ✓ Renamed season folder: '{OldName}' -> '{NewName}'", seasonFolderName, desiredSeason1Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[MR] ERROR: Failed to rename season folder. Old: {OldName}, New: {NewName}, Error: {Error}", 
                                seasonFolderName, desiredSeason1Name, ex.Message);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] ERROR in FixNestedSeasonFolders: {Message}", ex.Message);
        }
    }

    private void ProcessEpisodesInSeriesRoot(string seriesPath, PluginConfiguration cfg, DateTime now)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(seriesPath) || !Directory.Exists(seriesPath))
            {
                _logger.LogWarning("[MR] SKIP: Cannot process episodes - series path invalid or does not exist. Path: {Path}", seriesPath);
            return;
        }

            _logger.LogInformation("[MR] Scanning series folder for episode files: {Path}", seriesPath);

            // Check if season folders already exist (don't interfere with existing structure)
            // Pattern 1: Standard season folders (Season 1, Season 01, S1, etc.)
            var standardSeasonPattern = new System.Text.RegularExpressions.Regex(
                @"^(Season\s*\d+|S\d+|Season\s*\d{2,})$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            // Pattern 2: Folders containing "Season" or "S" followed by a number (e.g., "Dororo - Season 1")
            var containsSeasonPattern = new System.Text.RegularExpressions.Regex(
                @".*\b(Season|S)\s*\d+.*",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var subdirectories = Directory.GetDirectories(seriesPath);
            var seasonFolders = new List<string>();
            var nonSeasonFolders = new List<string>();
            
            foreach (var dir in subdirectories)
            {
                var dirName = Path.GetFileName(dir);
                if (standardSeasonPattern.IsMatch(dirName) || containsSeasonPattern.IsMatch(dirName))
                {
                    seasonFolders.Add(dir);
                    _logger.LogInformation("[MR] Detected season folder: {FolderName}", dirName);
                }
                else
                {
                    nonSeasonFolders.Add(dir);
                }
            }

            if (seasonFolders.Count > 0)
            {
                _logger.LogInformation("[MR] Series already has {Count} season folder(s). Checking if they need to be renamed...", seasonFolders.Count);
                
                // Fix any nested season folder structures (e.g., "Dororo - Season 1\Season 01")
                FixNestedSeasonFolders(seriesPath, cfg);
                
                _logger.LogInformation("[MR] Season folders detected and processed. Skipping episode organization - episodes are already structured correctly.");
                return;
            }

            // Get all video files in the series root (not in subdirectories)
            var videoExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" };
            var filesInRoot = Directory.GetFiles(seriesPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => videoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            if (filesInRoot.Count == 0)
            {
                _logger.LogInformation("[MR] No video files found in series root. Episodes may already be in season folders.");
                return;
            }

            _logger.LogInformation("[MR] Found {Count} video file(s) in series root (no season folders detected)", filesInRoot.Count);

            // Create "Season 01" folder if needed
            var season1FolderName = SafeName.RenderSeasonFolder(cfg.SeasonFolderFormat, 1, null);
            var season1FolderPath = Path.Combine(seriesPath, season1FolderName);

            if (!Directory.Exists(season1FolderPath))
            {
                Directory.CreateDirectory(season1FolderPath);
                _logger.LogInformation("[MR] ✓ Created Season 1 folder: {Path}", season1FolderPath);
            }
            else
            {
                _logger.LogInformation("[MR] Season 1 folder already exists: {Path}", season1FolderPath);
            }

            // Move each file to "Season 01"
            foreach (var filePath in filesInRoot)
            {
                var fileName = Path.GetFileName(filePath);
                var newFilePath = Path.Combine(season1FolderPath, fileName);

                if (File.Exists(newFilePath))
                {
                    _logger.LogWarning("[MR] SKIP: Target file already exists in Season 1 folder. File: {FileName}", fileName);
                    continue;
                }

                try
                {
                    File.Move(filePath, newFilePath);
                    _logger.LogInformation("[MR] ✓ Moved episode file to Season 1 folder: {FileName}", fileName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[MR] ERROR: Failed to move episode file. File: {FileName}, Error: {Error}", fileName, ex.Message);
                }
            }

            _logger.LogInformation("[MR] === Finished processing episodes in series root ===");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] ERROR in ProcessEpisodesInSeriesRoot: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Handles season folder renaming.
    /// IMPORTANT: This method uses METADATA VALUES ONLY (season number and name from Jellyfin metadata).
    /// It does NOT parse folder names to determine season numbers. All values come from:
    /// - season.IndexNumber (season number from metadata)
    /// - season.Name (season name from metadata)
    /// </summary>
    private void HandleSeasonUpdate(Season season, PluginConfiguration cfg, DateTime now)
    {
        try
        {
            // Defensive check: Ensure RenameSeasonFolders is enabled
            if (!cfg.RenameSeasonFolders)
            {
                _logger.LogInformation("[MR] [DEBUG] [SEASON-UPDATE-SKIP] SKIP: RenameSeasonFolders is disabled in configuration. Season: {Name}, Id={Id}", 
                    season.Name ?? "NULL", season.Id);
                return;
            }
            
            _logger.LogInformation("[MR] [DEBUG] [SEASON-UPDATE-ENTRY] === HandleSeasonUpdate Entry ===");
            _logger.LogInformation("[MR] [DEBUG] [SEASON-UPDATE-ENTRY] Season: Name={Name}, Id={Id}, Path={Path}, Season Number={SeasonNumber}", 
                season.Name ?? "NULL", season.Id, season.Path ?? "NULL", season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] [DEBUG] [SEASON-UPDATE-ENTRY] Series: Id={SeriesId}, Name={SeriesName}, Path={SeriesPath}",
                season.Series?.Id.ToString() ?? "NULL", season.Series?.Name ?? "NULL", season.Series?.Path ?? "NULL");

            // Per-item cooldown
            if (_lastAttemptUtcByItem.TryGetValue(season.Id, out var lastTry))
            {
                var timeSinceLastTry = (now - lastTry).TotalSeconds;
                if (timeSinceLastTry < cfg.PerItemCooldownSeconds)
                {
                    _logger.LogInformation(
                        "[MR] [DEBUG] [SEASON-SKIP-COOLDOWN] SKIP: Cooldown active. SeasonId={Id}, Name={Name}, Time since last try: {Seconds} seconds (cooldown: {CooldownSeconds})",
                        season.Id, season.Name ?? "NULL", timeSinceLastTry.ToString(System.Globalization.CultureInfo.InvariantCulture), cfg.PerItemCooldownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }
            }

            _lastAttemptUtcByItem[season.Id] = now;

            var path = season.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                _logger.LogWarning("[MR] [DEBUG] [SEASON-SKIP-NO-PATH] SKIP: Season has no path. SeasonId={Id}, Name={Name}, SeasonNumber={SeasonNumber}", 
                    season.Id, season.Name ?? "NULL", season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                return;
            }

            _logger.LogInformation("[MR] [DEBUG] [SEASON-PATH-VALIDATION] Validating season path: {Path}", path);
            
            if (!Directory.Exists(path))
            {
                _logger.LogWarning("[MR] [DEBUG] [SEASON-SKIP-PATH-NOT-EXISTS] SKIP: Season path does not exist on disk. Path={Path}, SeasonId={Id}, Name={Name}, SeasonNumber={SeasonNumber}", 
                    path, season.Id, season.Name ?? "NULL", season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                return;
            }
            
            _logger.LogInformation("[MR] [DEBUG] [SEASON-PATH-VALIDATION] Season path exists: {Path}", path);

            _logger.LogInformation("[MR] Season path verified: {Path}", path);

            // Get season metadata - ALL VALUES FROM METADATA, NOT FROM FOLDER NAME
            var seasonNumber = season.IndexNumber; // Season number from metadata (1, 2, 3, etc.)
            var seasonName = season.Name?.Trim() ?? string.Empty;
            var currentFolderName = Path.GetFileName(path);

            _logger.LogInformation("[MR] === Season Metadata (from Jellyfin, NOT from folder name) ===");
            _logger.LogInformation("[MR] Season Number: {SeasonNumber} (from metadata: IndexNumber)", 
                seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] Season Name: {SeasonName} (from metadata: Name)", seasonName);
            _logger.LogInformation("[MR] Current Folder Name: {Current} (for reference only, not used for renaming)", currentFolderName);

            if (!seasonNumber.HasValue)
            {
                _logger.LogWarning("[MR] [DEBUG] [SEASON-SKIP-NO-SEASON-NUMBER] SKIP: Season missing season number. SeasonId={Id}, Name={Name}, Path={Path}", 
                    season.Id, season.Name ?? "NULL", path);
                return;
            }
            
            _logger.LogInformation("[MR] [DEBUG] [SEASON-METADATA-VALIDATION] Season metadata validated: SeasonNumber={SeasonNumber}, SeasonName={SeasonName}", 
                seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), seasonName);

            // Build desired folder name using METADATA VALUES ONLY
            var desiredFolderName = SafeName.RenderSeasonFolder(
                cfg.SeasonFolderFormat,
                seasonNumber,
                seasonName);

            _logger.LogInformation("[MR] === Season Folder Rename Details ===");
            _logger.LogInformation("[MR] Current Folder: {Current}", currentFolderName);
            _logger.LogInformation("[MR] Desired Folder: {Desired}", desiredFolderName);
            _logger.LogInformation("[MR] Full Current Path: {Path}", path);
            _logger.LogInformation("[MR] Format: {Format}", cfg.SeasonFolderFormat);
            _logger.LogInformation("[MR] Dry Run Mode: {DryRun}", cfg.DryRun);

            // #region agent log - SEASON-FOLDER-RENAME-START: Track season folder rename start
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [SEASON-FOLDER-RENAME-START] Starting season folder rename");
                _logger.LogInformation("[MR] [DEBUG] [SEASON-FOLDER-RENAME-START] Season: {Name}, ID: {Id}, Season Number: {SeasonNumber}", 
                    season.Name ?? "NULL", season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                _logger.LogInformation("[MR] [DEBUG] [SEASON-FOLDER-RENAME-START] Current Path: {Path}", season.Path ?? "NULL");
                _logger.LogInformation("[MR] [DEBUG] [SEASON-FOLDER-RENAME-START] Desired Folder Name: {Desired}", desiredFolderName);
                
                var logData = new {
                    runId = "run1",
                    hypothesisId = "SEASON-FOLDER-RENAME-START",
                    location = "RenameCoordinator.cs:3058",
                    message = "Starting season folder rename",
                    data = new {
                        seasonId = season.Id.ToString(),
                        seasonName = season.Name ?? "NULL",
                        seasonNumber = season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                        seasonPath = season.Path ?? "NULL",
                        desiredFolderName = desiredFolderName,
                        dryRun = cfg.DryRun
                    },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "[MR] [DEBUG] [SEASON-FOLDER-RENAME-START] ERROR logging rename start: {Error}", logEx.Message);
            }
            // #endregion
            
            try
            {
                _pathRenamer.TryRenameSeasonFolder(season, desiredFolderName, cfg.DryRun);
                
                // #region agent log - SEASON-FOLDER-RENAME-SUCCESS: Track successful season folder rename
                try
                {
                    _logger.LogInformation("[MR] [DEBUG] [SEASON-FOLDER-RENAME-SUCCESS] Season folder rename completed");
                    
                    var logData = new {
                        runId = "run1",
                        hypothesisId = "SEASON-FOLDER-RENAME-SUCCESS",
                        location = "RenameCoordinator.cs:3058",
                        message = "Season folder rename completed successfully",
                        data = new {
                            seasonId = season.Id.ToString(),
                            seasonName = season.Name ?? "NULL",
                            seasonNumber = season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                            desiredFolderName = desiredFolderName
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, "[MR] [DEBUG] [SEASON-FOLDER-RENAME-SUCCESS] ERROR logging rename success: {Error}", logEx.Message);
                }
                // #endregion
            }
            catch (Exception seasonRenameEx)
            {
                // #region agent log - SEASON-FOLDER-RENAME-ERROR: Track season folder rename errors
                try
                {
                    _logger.LogError(seasonRenameEx, "[MR] [DEBUG] [SEASON-FOLDER-RENAME-ERROR] ERROR renaming season folder");
                    _logger.LogError("[MR] [DEBUG] [SEASON-FOLDER-RENAME-ERROR] Exception Type: {Type}", seasonRenameEx.GetType().FullName);
                    _logger.LogError("[MR] [DEBUG] [SEASON-FOLDER-RENAME-ERROR] Exception Message: {Message}", seasonRenameEx.Message);
                    _logger.LogError("[MR] [DEBUG] [SEASON-FOLDER-RENAME-ERROR] Stack Trace: {StackTrace}", seasonRenameEx.StackTrace ?? "N/A");
                    _logger.LogError("[MR] [DEBUG] [SEASON-FOLDER-RENAME-ERROR] Season: {Name}, ID: {Id}, Season Number: {SeasonNumber}", 
                        season.Name ?? "NULL", season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                    
                    var logData = new {
                        runId = "run1",
                        hypothesisId = "SEASON-FOLDER-RENAME-ERROR",
                        location = "RenameCoordinator.cs:3058",
                        message = "Error renaming season folder",
                        data = new {
                            seasonId = season.Id.ToString(),
                            seasonName = season.Name ?? "NULL",
                            seasonNumber = season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                            seasonPath = season.Path ?? "NULL",
                            desiredFolderName = desiredFolderName,
                            exceptionType = seasonRenameEx.GetType().FullName,
                            exceptionMessage = seasonRenameEx.Message,
                            stackTrace = seasonRenameEx.StackTrace ?? "N/A",
                            innerException = seasonRenameEx.InnerException?.Message ?? "N/A"
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, "[MR] [DEBUG] [SEASON-FOLDER-RENAME-ERROR] ERROR logging rename error: {Error}", logEx.Message);
                }
                // #endregion
                
                _logger.LogError(seasonRenameEx, "[MR] ERROR renaming season folder: {Message}", seasonRenameEx.Message);
            }

            _logger.LogInformation("[MR] ===== Season Processing Complete =====");
        }
        catch (Exception ex)
        {
            // #region agent log - SEASON-UPDATE-ERROR: Track errors in HandleSeasonUpdate
            try
            {
                _logger.LogError(ex, "[MR] [DEBUG] [SEASON-UPDATE-ERROR] ERROR in HandleSeasonUpdate");
                _logger.LogError("[MR] [DEBUG] [SEASON-UPDATE-ERROR] Exception Type: {Type}", ex.GetType().FullName);
                _logger.LogError("[MR] [DEBUG] [SEASON-UPDATE-ERROR] Exception Message: {Message}", ex.Message);
                _logger.LogError("[MR] [DEBUG] [SEASON-UPDATE-ERROR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
                
                var logData = new {
                    runId = "run1",
                    hypothesisId = "SEASON-UPDATE-ERROR",
                    location = "RenameCoordinator.cs:2991",
                    message = "Error in HandleSeasonUpdate",
                    data = new {
                        exceptionType = ex.GetType().FullName,
                        exceptionMessage = ex.Message,
                        stackTrace = ex.StackTrace ?? "N/A",
                        innerException = ex.InnerException?.Message ?? "N/A"
                    },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "[MR] [DEBUG] [SEASON-UPDATE-ERROR] ERROR logging season update error: {Error}", logEx.Message);
            }
            // #endregion
            
            _logger.LogError(ex, "[MR] ERROR in HandleSeasonUpdate: {Message}", ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
        }
    }

    /// <summary>
    /// Handles episode file renaming.
    /// SAFETY: This method validates that the episode number in the filename matches the metadata episode number
    /// before renaming. This prevents incorrect renames (e.g., renaming "episode 1" to "episode 5").
    /// Renaming uses metadata values for:
    /// - episode.ParentIndexNumber (season number from metadata)
    /// - episode.IndexNumber (episode number from metadata)
    /// - episode.Name (episode title from metadata)
    /// - episode.SeriesName (series name from metadata)
    /// </summary>
    private void HandleEpisodeUpdate(Episode episode, PluginConfiguration cfg, DateTime now, bool isBulkProcessing = false)
    {
        try
        {
            var seasonNum = episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
            var episodeNum = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
            
            // Defensive check: Ensure RenameEpisodeFiles is enabled
            if (!cfg.RenameEpisodeFiles)
            {
                _logger.LogInformation("[MR] [DEBUG] [EPISODE-UPDATE-SKIP] SKIP: RenameEpisodeFiles is disabled in configuration. Season={Season}, Episode={Episode}", 
                    seasonNum, episodeNum);
                return;
            }
            // Calculate isSeason2Plus early for use in episode number validation
            var isSeason2Plus = (episode.ParentIndexNumber ?? -1) >= 2;
            
            // #region agent log - EPISODE-UPDATE-ENTRY: Track all HandleEpisodeUpdate calls
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [EPISODE-UPDATE-ENTRY] HandleEpisodeUpdate called: EpisodeId={EpisodeId}, EpisodeName='{EpisodeName}', Season={Season}, Episode={Episode}, Path={Path}, IsBulkProcessing={IsBulkProcessing}",
                    episode.Id, episode.Name ?? "NULL", seasonNum, episodeNum, episode.Path ?? "NULL", isBulkProcessing);
                
                var logData = new { 
                    runId = "run1", 
                    hypothesisId = "EPISODE-UPDATE-ENTRY", 
                    location = "RenameCoordinator.cs:2342", 
                    message = "HandleEpisodeUpdate entry", 
                    data = new { 
                        episodeId = episode.Id.ToString(), 
                        episodeName = episode.Name ?? "NULL", 
                        episodePath = episode.Path ?? "NULL", 
                        episodeType = episode.GetType().FullName, 
                        seasonNumber = seasonNum, 
                        episodeNumber = episodeNum, 
                        isBulkProcessing = isBulkProcessing 
                    }, 
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [EPISODE-UPDATE-ENTRY] ERROR logging entry: {Error}", ex.Message);
            }
            // #endregion
            
            _logger.LogInformation("[MR] ===== HandleEpisodeUpdate Entry =====");
            _logger.LogInformation("[MR] Processing Episode: Name={Name}, Id={Id}, Path={Path}", episode.Name, episode.Id, episode.Path);
            _logger.LogInformation("[MR] Season: {Season}, Episode: {Episode}", seasonNum, episodeNum);
            _logger.LogInformation("[MR] Is Bulk Processing: {IsBulkProcessing}", isBulkProcessing);
            
            // #region agent log - Hypothesis A: Property access timing
            try
            {
                var indexNumberValue = episode.IndexNumber;
                var parentIndexNumberValue = episode.ParentIndexNumber;
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "RenameCoordinator.cs:445", message = "Episode IndexNumber accessed immediately", data = new { indexNumber = indexNumberValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", parentIndexNumber = parentIndexNumberValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                _logger.LogInformation("[MR] [DEBUG-HYP-A] IndexNumber accessed in HandleEpisodeUpdate: IndexNumber={IndexNumber}, ParentIndexNumber={ParentIndexNumber}, EpisodeId={Id}", 
                    indexNumberValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", 
                    parentIndexNumberValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episode.Id);
            }
            catch (Exception ex)
            {
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "RenameCoordinator.cs:445", message = "ERROR accessing IndexNumber", data = new { error = ex.Message, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                _logger.LogError(ex, "[MR] [DEBUG-HYP-A] ERROR accessing IndexNumber: {Error}", ex.Message);
            }
            // #endregion
            
            // #region agent log - Hypothesis B: Episode object state (reflection)
            try
            {
                var episodeType = episode.GetType();
                var properties = episodeType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var relevantProps = new Dictionary<string, object>();
                foreach (var prop in properties)
                {
                    if (prop.Name.Contains("Index", StringComparison.OrdinalIgnoreCase) || 
                        prop.Name.Contains("Number", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Contains("Season", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Contains("Episode", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name == "Name" || prop.Name == "SeriesName")
                    {
                        try
                        {
                            var value = prop.GetValue(episode);
                            relevantProps[prop.Name] = value?.ToString() ?? "NULL";
                        }
                        catch { relevantProps[prop.Name] = "ERROR"; }
                    }
                }
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "B", location = "RenameCoordinator.cs:465", message = "Episode object properties via reflection", data = new { episodeType = episodeType.FullName, properties = relevantProps, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                _logger.LogInformation("[MR] [DEBUG-HYP-B] Episode type: {Type}, Relevant properties: {Properties}", episodeType.FullName, string.Join(", ", relevantProps.Select(kvp => $"{kvp.Key}={kvp.Value}")));
            }
            catch (Exception ex)
            {
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "B", location = "RenameCoordinator.cs:465", message = "ERROR in reflection", data = new { error = ex.Message, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                _logger.LogError(ex, "[MR] [DEBUG-HYP-B] ERROR in reflection: {Error}", ex.Message);
            }
            // #endregion

            // Per-item cooldown
            if (_lastAttemptUtcByItem.TryGetValue(episode.Id, out var lastTry))
            {
                var timeSinceLastTry = (now - lastTry).TotalSeconds;
                if (timeSinceLastTry < cfg.PerItemCooldownSeconds)
                {
                    // #region agent log - MULTI-EPISODE-HYP-C: Track episodes skipped due to cooldown
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-C", location = "RenameCoordinator.cs:525", message = "Episode skipped - cooldown active", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNum, episodeNumber = episodeNum, timeSinceLastTry = timeSinceLastTry.ToString(System.Globalization.CultureInfo.InvariantCulture), cooldownSeconds = cfg.PerItemCooldownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    // #endregion
                    _logger.LogInformation(
                        "[MR] [DEBUG] [EPISODE-SKIP-COOLDOWN] SKIP: Cooldown active. EpisodeId={Id}, Name={Name}, Season={Season}, Episode={Episode}, Time since last try: {Seconds} seconds (cooldown: {CooldownSeconds})",
                        episode.Id, episode.Name, seasonNum, episodeNum, timeSinceLastTry.ToString(System.Globalization.CultureInfo.InvariantCulture), cfg.PerItemCooldownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _logger.LogInformation("[MR] ===== Episode Processing Complete (Skipped - Cooldown) =====");
                    return;
                }
            }

            _lastAttemptUtcByItem[episode.Id] = now;

            var path = episode.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                // #region agent log - MULTI-EPISODE-HYP-D: Track episodes skipped due to no path
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-D", location = "RenameCoordinator.cs:539", message = "Episode skipped - no path", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNum, episodeNumber = episodeNum }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                // #endregion
                _logger.LogWarning("[MR] [DEBUG] [EPISODE-SKIP-NO-PATH] SKIP: Episode has no path. EpisodeId={Id}, Name={Name}, Season={Season}, Episode={Episode}", episode.Id, episode.Name, seasonNum, episodeNum);
                _logger.LogInformation("[MR] ===== Episode Processing Complete (Skipped - No Path) =====");
                return;
            }

            if (!File.Exists(path))
            {
                // #region agent log - MULTI-EPISODE-HYP-E: Track episodes skipped due to file not existing
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-E", location = "RenameCoordinator.cs:546", message = "Episode skipped - file does not exist", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNum, episodeNumber = episodeNum, path = path }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                // #endregion
                _logger.LogWarning("[MR] [DEBUG] [EPISODE-SKIP-FILE-NOT-EXISTS] SKIP: Episode file does not exist on disk. Path={Path}, EpisodeId={Id}, Name={Name}, Season={Season}, Episode={Episode}", path, episode.Id, episode.Name, seasonNum, episodeNum);
                _logger.LogInformation("[MR] ===== Episode Processing Complete (Skipped - File Not Exists) =====");
                return;
            }

            _logger.LogInformation("[MR] Episode file path verified: {Path}", path);

            // Derive series path from episode file path (more reliable than episode.Series?.Path which may be stale after rename)
            var episodeDirectory = Path.GetDirectoryName(path);
            var seriesPathFromFile = DeriveSeriesPathFromEpisodePath(path);
            var seriesPathFromMetadata = episode.Series?.Path;
            
            // Use file system path if available, fallback to metadata path
            var seriesPath = !string.IsNullOrWhiteSpace(seriesPathFromFile) ? seriesPathFromFile : seriesPathFromMetadata;
            
            _logger.LogInformation("[MR] Series path from file system: {FromFile}, from metadata: {FromMetadata}, using: {Using}", 
                seriesPathFromFile ?? "NULL", seriesPathFromMetadata ?? "NULL", seriesPath ?? "NULL");
            
            // Fix any nested season folder structures (e.g., "Dororo - Season 1\Season 01") before processing episodes
            if (!string.IsNullOrWhiteSpace(seriesPath) && Directory.Exists(seriesPath))
            {
                // #region agent log - FixNestedSeasonFolders Call from Episode
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "NESTED-SEASON", location = "RenameCoordinator.cs:1293", message = "Calling FixNestedSeasonFolders from HandleEpisodeUpdate", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seriesPath = seriesPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                // #endregion
                FixNestedSeasonFolders(seriesPath, cfg);
            }
            
            // Check if episode is directly in series folder (no season folder)
            var isInSeriesRoot = !string.IsNullOrWhiteSpace(seriesPath) && 
                                 !string.IsNullOrWhiteSpace(episodeDirectory) &&
                                 string.Equals(episodeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), 
                                             seriesPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), 
                                             StringComparison.OrdinalIgnoreCase);
            
            // #region agent log - MULTI-EPISODE-HYP-F: Track isInSeriesRoot detection for each episode
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-F", location = "RenameCoordinator.cs:557", message = "isInSeriesRoot check", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", episodePath = path, episodeDirectory = episodeDirectory ?? "NULL", seriesPath = seriesPath ?? "NULL", isInSeriesRoot = isInSeriesRoot }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            
            // Remember if we were in series root BEFORE moving (capture before isInSeriesRoot is modified)
            var wasInSeriesRootBeforeMove = isInSeriesRoot;
            
            if (isInSeriesRoot)
            {
                _logger.LogInformation("[MR] Episode is directly in series folder (no season folder structure)");
                
                // Create "Season 1" folder and move episode into it
                // This ensures Jellyfin shows "Season 1" instead of "Season Unknown"
                _logger.LogInformation("[MR] === Creating Season 1 Folder for Flat Structure ===");
                _logger.LogInformation("[MR] Season Folder Format: {Format}", cfg.SeasonFolderFormat ?? "Season {Season:00}");
                _logger.LogInformation("[MR] Season Number for folder: 1");
                
                var season1FolderName = SafeName.RenderSeasonFolder(cfg.SeasonFolderFormat, 1, null);
                var season1FolderPath = Path.Combine(seriesPath, season1FolderName);
                
                _logger.LogInformation("[MR] Season 1 Folder Name (rendered): {FolderName}", season1FolderName);
                _logger.LogInformation("[MR] Season 1 Folder Path: {FolderPath}", season1FolderPath);
                _logger.LogInformation("[MR] Series Path: {SeriesPath}", seriesPath);
                
                if (!Directory.Exists(season1FolderPath))
                {
                    if (cfg.DryRun)
                    {
                        _logger.LogWarning("[MR] DRY RUN: Would create Season 1 folder: {Path}", season1FolderPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(season1FolderPath);
                        _logger.LogInformation("[MR] ✓ Created Season 1 folder: {Path}", season1FolderPath);
                    }
                }
                else
                {
                    _logger.LogInformation("[MR] Season 1 folder already exists: {Path}", season1FolderPath);
                }
                
                // Move episode file to Season 1 folder
                var fileName = Path.GetFileName(path);
                var newEpisodePath = Path.Combine(season1FolderPath, fileName);
                
                if (!File.Exists(newEpisodePath))
                {
                    if (cfg.DryRun)
                    {
                        _logger.LogWarning("[MR] DRY RUN: Would move episode file from {From} to {To}", path, newEpisodePath);
                    }
                    else
                    {
                        File.Move(path, newEpisodePath);
                        // #region agent log - MULTI-EPISODE-HYP-G: Track successful episode moves
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-G", location = "RenameCoordinator.cs:612", message = "Episode moved to Season 1 folder", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", fromPath = path, toPath = newEpisodePath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                        // #endregion
                        _logger.LogInformation("[MR] ✓ Moved episode file to Season 1 folder");
                        _logger.LogInformation("[MR] From: {From}", path);
                        _logger.LogInformation("[MR] To: {To}", newEpisodePath);
                        
                        // Update path for subsequent processing
                        path = newEpisodePath;
                        episodeDirectory = season1FolderPath;
                        isInSeriesRoot = false; // No longer in series root
                    }
                }
                else
                {
                    _logger.LogWarning("[MR] Target file already exists in Season 1 folder. Skipping move. Path: {Path}", newEpisodePath);
                    // Update path for subsequent processing
                    path = newEpisodePath;
                    episodeDirectory = season1FolderPath;
                    isInSeriesRoot = false;
                }
            }

            // Get episode metadata - ALL VALUES FROM METADATA, NOT FROM FILENAME
            // Extract clean episode title (removing filename patterns)
            var episodeTitle = ExtractCleanEpisodeTitle(episode);
            
            // Determine season number:
            // - If episode was in series root (flat structure), we created Season 1 folder, so use season 1
            // - If episode is already in a season folder, use the actual season number from metadata
            // - Default to 1 if season number is null
            int? seasonNumber = episode.ParentIndexNumber;
            
            if (wasInSeriesRootBeforeMove)
            {
                // Episode was in series root (flat structure) and we moved it to Season 1
                seasonNumber = 1;
                _logger.LogInformation("[MR] Episode was in series root (flat structure). Using Season 1 for renaming after moving to Season 1 folder.");
            }
            else if (seasonNumber == null)
            {
                // Episode is in a season folder but metadata doesn't have season number - use 1 as fallback
                seasonNumber = 1;
                _logger.LogInformation("[MR] Episode is in a season folder but metadata season number is NULL. Using Season 1 as fallback.");
            }
            else
            {
                // Episode is already in a season folder - use the actual season number from metadata
                _logger.LogInformation("[MR] Episode is already in a season folder. Using season number from metadata: Season {Season}", 
                    seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            // #region agent log - Hypothesis C: Check IndexNumber at metadata access point
            try
            {
                var indexNumberBeforeAccess = episode.IndexNumber;
                var parentIndexNumberBeforeAccess = episode.ParentIndexNumber;
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "C", location = "RenameCoordinator.cs:609", message = "IndexNumber accessed at metadata point", data = new { indexNumber = indexNumberBeforeAccess?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", parentIndexNumber = parentIndexNumberBeforeAccess?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                _logger.LogInformation("[MR] [DEBUG-HYP-C] IndexNumber at metadata access point: IndexNumber={IndexNumber}, ParentIndexNumber={ParentIndexNumber}, EpisodeId={Id}, Name={Name}", 
                    indexNumberBeforeAccess?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", 
                    parentIndexNumberBeforeAccess?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episode.Id, episode.Name ?? "NULL");
            }
            catch (Exception ex)
            {
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "C", location = "RenameCoordinator.cs:609", message = "ERROR accessing IndexNumber at metadata point", data = new { error = ex.Message, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                _logger.LogError(ex, "[MR] [DEBUG-HYP-C] ERROR accessing IndexNumber at metadata point: {Error}", ex.Message);
            }
            // #endregion
            
            var episodeNumber = episode.IndexNumber; // Episode number from metadata
            var seriesName = episode.SeriesName?.Trim() ?? string.Empty;
            
            // #region agent log - Hypothesis D: Check Series object state
            try
            {
                var seriesObj = episode.Series;
                var seriesType = seriesObj?.GetType().FullName ?? "NULL";
                var seriesId = seriesObj?.Id.ToString() ?? "NULL";
                var seriesNameFromObj = seriesObj?.Name ?? "NULL";
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "RenameCoordinator.cs:625", message = "Episode.Series object state", data = new { seriesType = seriesType, seriesId = seriesId, seriesName = seriesNameFromObj, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                _logger.LogInformation("[MR] [DEBUG-HYP-D] Episode.Series state: Type={Type}, Id={Id}, Name={Name}, EpisodeId={EpisodeId}", 
                    seriesType, seriesId, seriesNameFromObj, episode.Id);
            }
            catch (Exception ex)
            {
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "RenameCoordinator.cs:625", message = "ERROR accessing Series object", data = new { error = ex.Message, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                _logger.LogError(ex, "[MR] [DEBUG-HYP-D] ERROR accessing Series object: {Error}", ex.Message);
            }
            // #endregion
            
            _logger.LogInformation("[MR] === Episode Metadata Debug ===");
            _logger.LogInformation("[MR] episode.IndexNumber (raw): {IndexNumber}", episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] episode.ParentIndexNumber (raw): {ParentIndexNumber}", episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] episode.Name (raw): {Name}", episode.Name ?? "NULL");
            _logger.LogInformation("[MR] episode.SeriesName (raw): {SeriesName}", episode.SeriesName ?? "NULL");
            
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

            // Get current filename for episode number validation
            var currentFileName = Path.GetFileNameWithoutExtension(path);
            
            _logger.LogInformation("[MR] === Episode Metadata (from Jellyfin) ===");
            _logger.LogInformation("[MR] Series Name: {Series} (from metadata)", seriesName);
            _logger.LogInformation("[MR] Season Number: {Season} (from metadata: ParentIndexNumber)", 
                seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] Episode Number: {Episode} (from metadata: IndexNumber)", 
                episodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] Episode Title: {Title} (from metadata: Name)", episodeTitle);
            _logger.LogInformation("[MR] Year: {Year} (from series metadata)", year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] Current Filename: {Current}", currentFileName);
            _logger.LogInformation("[MR] In Series Root (flat structure): {InSeriesRoot}", isInSeriesRoot);

            // #region agent log - Hypothesis E: IndexNumber is NULL - log full episode state
            if (!episodeNumber.HasValue)
            {
                try
                {
                    var episodeType = episode.GetType();
                    var allProps = new Dictionary<string, object>();
                    foreach (var prop in episodeType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        try
                        {
                            var val = prop.GetValue(episode);
                            if (val != null && (prop.PropertyType.IsPrimitive || prop.PropertyType == typeof(string) || prop.PropertyType == typeof(Guid)))
                            {
                                allProps[prop.Name] = val.ToString();
                            }
                            else if (val == null)
                            {
                                allProps[prop.Name] = "NULL";
                            }
                            else
                            {
                                allProps[prop.Name] = val.GetType().Name;
                            }
                        }
                        catch { allProps[prop.Name] = "ERROR"; }
                    }
                    var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "E", location = "RenameCoordinator.cs:640", message = "IndexNumber is NULL - full episode state", data = new { episodeType = episodeType.FullName, episodeId = episode.Id.ToString(), allProperties = allProps }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    _logger.LogWarning("[MR] [DEBUG-HYP-E] IndexNumber is NULL! Episode type: {Type}, EpisodeId: {Id}, All properties: {Properties}", 
                        episodeType.FullName, episode.Id, string.Join("; ", allProps.Select(kvp => $"{kvp.Key}={kvp.Value}")));
                }
                catch (Exception ex)
                {
                    var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "E", location = "RenameCoordinator.cs:640", message = "ERROR getting full episode state", data = new { error = ex.Message, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
                    _logger.LogError(ex, "[MR] [DEBUG-HYP-E] ERROR getting full episode state: {Error}", ex.Message);
                }
            }
            // #endregion
            
            // Episode number is REQUIRED - try to get it from metadata first, then fall back to filename parsing
            if (!episodeNumber.HasValue)
            {
                _logger.LogWarning("[MR] Episode number is NULL in metadata. Attempting to parse from filename: {FileName}", currentFileName);
                
                // Try to parse episode number from filename as a fallback
                var parsedEpisodeNumber = SafeName.ParseEpisodeNumberFromFileName(currentFileName);
                if (parsedEpisodeNumber.HasValue)
                {
                    episodeNumber = parsedEpisodeNumber;
                    _logger.LogInformation("[MR] ✓ Parsed episode number from filename: {EpisodeNumber}", episodeNumber.Value);
                }
                else
                {
                    // Cannot determine episode number - queue for retry
                    var reason = $"Episode missing episode number in metadata AND could not parse from filename. Cannot determine correct episode number. Filename: {currentFileName}";
                    _logger.LogWarning("[MR] [DEBUG] [EPISODE-SKIP-NO-EP-NUMBER] {Reason} Queueing for retry. Season={Season}, Episode={Episode}", reason, seasonNum, episodeNum);
                    QueueEpisodeForRetry(episode, reason);
                    _logger.LogInformation("[MR] ===== Episode Processing Complete (Skipped - No Episode Number) =====");
                    return;
                }
            }

            // SAFETY CHECK: Parse episode number from filename and compare with metadata
            // Only rename if the episode number in filename matches metadata episode number
            // NOTE: For Season 2+ episodes, we relax this check because filenames may use absolute episode numbers
            // or incorrect numbering, but Jellyfin metadata is authoritative
            var filenameEpisodeNumber = SafeName.ParseEpisodeNumberFromFileName(currentFileName);
            
            _logger.LogInformation("[MR] === Episode Number Validation ===");
            _logger.LogInformation("[MR] Episode number from filename: {FilenameEp} (parsed from: {Filename})", 
                filenameEpisodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NOT FOUND", 
                currentFileName);
            _logger.LogInformation("[MR] Episode number from metadata: {MetadataEp}", 
                episodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (filenameEpisodeNumber.HasValue)
            {
                if (filenameEpisodeNumber.Value != episodeNumber.Value)
                {
                    // For Season 2+ episodes, relax the episode number mismatch check
                    // because filenames may have incorrect episode numbers (e.g., absolute episode numbers)
                    // but Jellyfin metadata is authoritative
                    if (isSeason2Plus)
                    {
                        _logger.LogWarning("[MR] [DEBUG] [SEASON2+-EP-NUMBER-MISMATCH] Season 2+ episode: Filename says episode {FilenameEp}, but metadata says episode {MetadataEp}. " +
                            "Proceeding with rename using metadata (Season 2+ episodes may have incorrect filename numbering). Season={Season}, Episode={Episode}",
                            filenameEpisodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            episodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            seasonNum, episodeNum);
                        // Continue processing - don't skip
                    }
                    else
                    {
                        _logger.LogWarning("[MR] [DEBUG] [EPISODE-SKIP-EP-NUMBER-MISMATCH] SKIP: Episode number mismatch! Filename says episode {FilenameEp}, but metadata says episode {MetadataEp}. " +
                            "This prevents incorrect renames (e.g., renaming 'episode 1' to 'episode 5'). " +
                            "Please verify the file is correctly identified in Jellyfin. Season={Season}, Episode={Episode}",
                            filenameEpisodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            episodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            seasonNum, episodeNum);
                        _logger.LogInformation("[MR] ===== Episode Processing Complete (Skipped - Episode Number Mismatch) =====");
                        return;
                    }
                }
                else
                {
                    _logger.LogInformation("[MR] ✓ Episode number match confirmed: Both filename and metadata indicate episode {Episode}",
                        episodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else
            {
                _logger.LogWarning("[MR] Warning: Could not parse episode number from filename '{Filename}'. " +
                    "Proceeding with rename using metadata episode number {MetadataEp}, but please verify this is correct.",
                    currentFileName,
                    episodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // Log if season number is missing (common for flat structures)
            if (!seasonNumber.HasValue)
            {
                _logger.LogInformation("[MR] Note: Episode has no season number in metadata (flat structure). Season placeholders in format will be removed.");
            }

            // Episode title validation - queue for retry if title is a filename pattern
            _logger.LogInformation("[MR] [DEBUG] === Episode Title Validation ===");
            _logger.LogInformation("[MR] [DEBUG] Extracted clean title: '{Title}'", string.IsNullOrWhiteSpace(episodeTitle) ? "(empty)" : episodeTitle);
            
            if (string.IsNullOrWhiteSpace(episodeTitle))
            {
                var originalName = episode.Name?.Trim() ?? string.Empty;
                _logger.LogInformation("[MR] [DEBUG] Clean title is empty. Checking original episode.Name: '{Original}'", originalName);
                
                // Check if the original name looks like a filename pattern (contains S##E##)
                var isFilenamePattern = !string.IsNullOrWhiteSpace(originalName) && 
                                       System.Text.RegularExpressions.Regex.IsMatch(originalName, @"[Ss]\d+[Ee]\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                _logger.LogInformation("[MR] [DEBUG] Is filename pattern: {IsPattern}", isFilenamePattern);
                
                if (isFilenamePattern)
                {
                    // Episode title is a filename pattern, metadata may not be loaded yet
                    var reason = $"Episode title appears to be a filename pattern: '{originalName}'. Metadata may not be loaded yet.";
                    _logger.LogWarning("[MR] [DEBUG] [EPISODE-SKIP-FILENAME-PATTERN] {Reason} Queueing for retry. Season={Season}, Episode={Episode}", reason, seasonNum, episodeNum);
                    QueueEpisodeForRetry(episode, reason);
                    _logger.LogInformation("[MR] [DEBUG] === Episode Title Validation Complete (Queued for Retry) ===");
                    _logger.LogInformation("[MR] ===== Episode Processing Complete (Skipped - Filename Pattern) =====");
                    return; // Skip processing, will retry later
                }
                else
                {
                    // Title is empty but not a filename pattern - this is acceptable, use episode number only
                    _logger.LogInformation("[MR] [DEBUG] Episode title is empty but not a filename pattern. Will use episode number only for renaming.");
                    _logger.LogInformation("[MR] [DEBUG] EpisodeId={Id}", episode.Id);
                episodeTitle = string.Empty; // Use empty string, format will handle it
                }
            }
            else
            {
                _logger.LogInformation("[MR] [DEBUG] Episode title is valid and clean: '{Title}'", episodeTitle);
            }
            
            _logger.LogInformation("[MR] [DEBUG] === Episode Title Validation Complete ===");

            // Build desired file name (without extension) using METADATA VALUES ONLY
            var fileExtension = Path.GetExtension(path);
            
            // #region agent log - EPISODE-FILENAME-GENERATION: Track metadata values used for filename generation
            try
            {
                System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", 
                    System.Text.Json.JsonSerializer.Serialize(new {
                        runId = "run1",
                        hypothesisId = "EPISODE-FILENAME-GENERATION",
                        location = "RenameCoordinator.cs:4314",
                        message = "Generating episode filename from metadata",
                        data = new {
                            episodeId = episode.Id.ToString(),
                            episodeName = episode.Name ?? "NULL",
                            episodePath = path,
                            seriesName = seriesName ?? "NULL",
                            seasonNumber = seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                            episodeNumber = episodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                            episodeTitle = episodeTitle ?? "NULL",
                            episodeTitleEmpty = string.IsNullOrWhiteSpace(episodeTitle),
                            year = year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                            formatTemplate = cfg.EpisodeFileFormat ?? "NULL",
                            parentIndexNumber = episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                            indexNumber = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }) + "\n");
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-FILENAME-GENERATION] ERROR logging: {Error}", logEx.Message);
            }
            // #endregion
            
            var desiredFileName = SafeName.RenderEpisodeFileName(
                cfg.EpisodeFileFormat,
                seriesName,
                seasonNumber,
                episodeNumber,
                episodeTitle,
                year);

            // #region agent log - EPISODE-FILENAME-RESULT: Track the generated filename
            try
            {
                System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", 
                    System.Text.Json.JsonSerializer.Serialize(new {
                        runId = "run1",
                        hypothesisId = "EPISODE-FILENAME-RESULT",
                        location = "RenameCoordinator.cs:4340",
                        message = "Generated episode filename result",
                        data = new {
                            episodeId = episode.Id.ToString(),
                            desiredFileName = desiredFileName + fileExtension,
                            currentFileName = currentFileName + fileExtension,
                            formatTemplate = cfg.EpisodeFileFormat ?? "NULL"
                        },
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }) + "\n");
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-FILENAME-RESULT] ERROR logging: {Error}", logEx.Message);
            }
            // #endregion

            _logger.LogInformation("[MR] === Episode File Rename Details ===");
            _logger.LogInformation("[MR] Current File: {Current}", currentFileName + fileExtension);
            _logger.LogInformation("[MR] Desired File: {Desired}", desiredFileName + fileExtension);
            _logger.LogInformation("[MR] Format Template: {Format}", cfg.EpisodeFileFormat);
            _logger.LogInformation("[MR] ✓ Safety check passed: Filename episode number matches metadata episode number");
            _logger.LogInformation("[MR] ✓ Using metadata values: Season={Season}, Episode={Episode}, Title={Title}", 
                seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "N/A",
                episodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(episodeTitle) ? "(no title)" : episodeTitle);
            _logger.LogInformation("[MR] Dry Run Mode: {DryRun}", cfg.DryRun);

            // Check if current filename matches desired filename
            _logger.LogInformation("[MR] [DEBUG] === Filename Comparison ===");
            _logger.LogInformation("[MR] [DEBUG] Current filename: '{Current}'", currentFileName);
            _logger.LogInformation("[MR] [DEBUG] Desired filename: '{Desired}'", desiredFileName);
            
            var normalizedCurrent = SafeName.NormalizeFileNameForComparison(currentFileName);
            var normalizedDesired = SafeName.NormalizeFileNameForComparison(desiredFileName);
            _logger.LogInformation("[MR] [DEBUG] Normalized current: '{NormalizedCurrent}'", normalizedCurrent);
            _logger.LogInformation("[MR] [DEBUG] Normalized desired: '{NormalizedDesired}'", normalizedDesired);
            
            var filenamesMatch = SafeName.DoFilenamesMatch(currentFileName, desiredFileName);
            _logger.LogInformation("[MR] [DEBUG] Filenames match: {Match}", filenamesMatch);
            _logger.LogInformation("[MR] [DEBUG] === Filename Comparison Complete ===");
            
            // #region agent log - FILENAME-COMPARISON: Track filename comparison details
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [FILENAME-COMPARISON] EpisodeId={EpisodeId}, CurrentFileName='{CurrentFileName}', DesiredFileName='{DesiredFileName}', NormalizedCurrent='{NormalizedCurrent}', NormalizedDesired='{NormalizedDesired}', FilenamesMatch={FilenamesMatch}, EpisodeName='{EpisodeName}', EpisodeTitle='{EpisodeTitle}', Season={Season}, Episode={Episode}",
                    episode.Id.ToString(), currentFileName, desiredFileName, normalizedCurrent, normalizedDesired, filenamesMatch,
                    episode.Name ?? "NULL", episodeTitle ?? "NULL",
                    seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                    episodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                
                var logData = new { 
                    runId = "run1", 
                    hypothesisId = "FILENAME-COMPARISON", 
                    location = "RenameCoordinator.cs:2430", 
                    message = "Filename comparison result", 
                    data = new { 
                        episodeId = episode.Id.ToString(),
                        currentFileName = currentFileName,
                        desiredFileName = desiredFileName,
                        normalizedCurrent = normalizedCurrent,
                        normalizedDesired = normalizedDesired,
                        filenamesMatch = filenamesMatch,
                        episodeName = episode.Name ?? "NULL",
                        episodeTitle = episodeTitle ?? "NULL",
                        seasonNumber = seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                        episodeNumber = episodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"
                    }, 
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", logJson); } catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [FILENAME-COMPARISON] ERROR logging filename comparison: {Error}", ex.Message);
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "FILENAME-COMPARISON", location = "RenameCoordinator.cs:2430", message = "ERROR logging filename comparison", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            }
            // #endregion
            
            if (filenamesMatch)
            {
                _logger.LogInformation("[MR] === Filename Already Matches Metadata ===");
                _logger.LogInformation("[MR] Current filename matches desired format + metadata. Skipping rename.");
                _logger.LogInformation("[MR] Current: {Current}", currentFileName);
                _logger.LogInformation("[MR] Desired: {Desired}", desiredFileName);
                
                // Remove from retry queue if it was queued
                RemoveEpisodeFromRetryQueue(episode.Id);
                
                _logger.LogInformation("[MR] ===== Episode Processing Complete (No Rename Needed) =====");
                _logger.LogInformation("[MR] Episode: {Name} (S{Season}E{Episode}) - Already correctly named", 
                    episode.Name ?? "Unknown",
                    episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "??",
                    episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "??");
                return;
            }

            _logger.LogInformation("[MR] === Filename Does Not Match Metadata ===");
            _logger.LogInformation("[MR] Current filename does not match desired format + metadata. Rename needed.");
            _logger.LogInformation("[MR] Current: {Current}", currentFileName);
            _logger.LogInformation("[MR] Desired: {Desired}", desiredFileName);

            // Pass the updated path if file was moved to Season 1 folder
            _pathRenamer.TryRenameEpisodeFile(episode, desiredFileName, fileExtension, cfg.DryRun, path);
            
            // Remove from retry queue after successful rename attempt
            RemoveEpisodeFromRetryQueue(episode.Id);

            _logger.LogInformation("[MR] ===== Episode Processing Complete =====");
            _logger.LogInformation("[MR] Episode: {Name} (S{Season}E{Episode}) - Processing finished", 
                episode.Name ?? "Unknown",
                episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "??",
                episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "??");
        }
        catch (Exception ex)
        {
            var seasonNum = episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
            var episodeNum = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
            _logger.LogError(ex, "[MR] ERROR in HandleEpisodeUpdate for episode {EpisodeName} (S{Season}E{Episode}, ID: {EpisodeId}): {Message}", 
                episode.Name ?? "Unknown", seasonNum, episodeNum, episode.Id, ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
        }
    }

    /// <summary>
    /// Derives the series path from an episode file path by checking if the episode is in a season folder.
    /// If the episode is in a season folder (e.g., "Season 01", "Season 1", "S01"), returns the parent directory.
    /// Otherwise, returns the directory containing the episode file (series root).
    /// </summary>
    private string? DeriveSeriesPathFromEpisodePath(string episodeFilePath)
    {
        try
        {
            var episodeDirectory = Path.GetDirectoryName(episodeFilePath);
            if (string.IsNullOrWhiteSpace(episodeDirectory))
            {
                return null;
            }

            var directoryName = Path.GetFileName(episodeDirectory);
            if (string.IsNullOrWhiteSpace(directoryName))
            {
                return episodeDirectory;
            }

            // Check if the directory name matches common season folder patterns
            var seasonPattern = new System.Text.RegularExpressions.Regex(
                @"^(Season\s*\d+|S\d+|Season\s*\d{2,})$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (seasonPattern.IsMatch(directoryName))
            {
                // Episode is in a season folder, series path is the parent
                var parentDirectory = Path.GetDirectoryName(episodeDirectory);
                return parentDirectory;
            }
            else
            {
                // Episode is in series root, series path is the directory itself
                return episodeDirectory;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MR] Warning: Could not derive series path from episode path: {Path}, Error: {Error}", episodeFilePath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Validates and corrects the year value when Jellyfin provides incorrect metadata.
    /// This handles cases where Jellyfin has cached wrong metadata (e.g., old version year).
    /// </summary>
    /// <param name="year">The year from Jellyfin metadata.</param>
    /// <param name="providerLabel">The provider label (e.g., "tmdb", "tvdb").</param>
    /// <param name="providerId">The provider ID.</param>
    /// <param name="seriesName">The series name.</param>
    /// <param name="currentPath">The current folder path (may contain correct year).</param>
    /// <returns>The corrected year, or original year if no correction needed.</returns>
    private int? ValidateAndCorrectYear(int? year, string? providerLabel, string? providerId, string? seriesName, string? currentPath)
    {
        if (!year.HasValue || string.IsNullOrWhiteSpace(providerLabel) || string.IsNullOrWhiteSpace(providerId))
        {
            return year;
        }

        // Known corrections: Provider ID -> Correct Year
        // Format: "provider-id" -> correct year
        var knownCorrections = new Dictionary<string, int>
        {
            { "tmdb-83100", 2019 }, // Dororo (2019) - Jellyfin sometimes provides 1969 (old version)
        };

        var providerKey = $"{providerLabel.ToLowerInvariant()}-{providerId}";
        
        if (knownCorrections.TryGetValue(providerKey, out var correctYear))
        {
            if (year.Value != correctYear)
            {
                _logger.LogWarning("[MR] ⚠️ YEAR CORRECTION: Jellyfin provided year {WrongYear} but correct year for {ProviderKey} is {CorrectYear}", 
                    year.Value, providerKey, correctYear);
                _logger.LogWarning("[MR] ⚠️ Correcting year from {WrongYear} to {CorrectYear} for series: {SeriesName}", 
                    year.Value, correctYear, seriesName ?? "Unknown");
                
                // #region agent log - Year correction
                try 
                { 
                    System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", 
                        System.Text.Json.JsonSerializer.Serialize(new 
                        { 
                            sessionId = "debug-session", 
                            runId = "run1", 
                            hypothesisId = "YEAR-CORRECT", 
                            location = "RenameCoordinator.cs:ValidateAndCorrectYear", 
                            message = "Year correction applied", 
                            data = new 
                            { 
                                providerKey = providerKey,
                                wrongYear = year.Value,
                                correctYear = correctYear,
                                seriesName = seriesName ?? "NULL",
                                currentPath = currentPath ?? "NULL"
                            }, 
                            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() 
                        }) + "\n"); 
                } 
                catch { }
                // #endregion
                
                return correctYear;
            }
        }

        // Also check if current folder name contains a different year that might be correct
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var folderName = Path.GetFileName(currentPath);
            // Try to extract year from folder name pattern: "Name (YYYY) [provider-id]"
            var yearMatch = System.Text.RegularExpressions.Regex.Match(folderName, @"\((\d{4})\)");
            if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out var folderYear))
            {
                // If folder year is significantly different (more than 5 years), it might be correct
                // and Jellyfin metadata might be wrong
                if (Math.Abs(year.Value - folderYear) > 5)
                {
                    _logger.LogWarning("[MR] ⚠️ YEAR MISMATCH: Jellyfin year ({JellyfinYear}) differs significantly from folder year ({FolderYear})", 
                        year.Value, folderYear);
                    _logger.LogWarning("[MR] ⚠️ This may indicate Jellyfin has cached incorrect metadata. Consider refreshing metadata.");
                    
                    // Don't auto-correct based on folder name (could be wrong), but log the warning
                }
            }
        }

        return year;
    }
}
