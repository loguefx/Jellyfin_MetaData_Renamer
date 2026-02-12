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

    // One-time per-folder mapping: folder path -> (file path -> (season, episodeWithinSeason)).
    // Prevents duplicate S01E01 etc. when we move files (position would otherwise shift each time).
    private readonly Dictionary<string, Dictionary<string, (int season, int episodeWithinSeason)>> _folderSeasonEpisodeMapCache = new();
    private readonly object _folderSeasonEpisodeMapCacheLock = new object();
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
            _folderSeasonEpisodeMapCache.Clear();
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
            _logger.LogInformation("[MR] [DEBUG] Evaluating {Count} episodes in queue", _episodeRetryQueue.Count);

            var (episodesToRetry, episodesToRemove) = EvaluateRetryQueue(now);
            RemoveFromRetryQueueBatch(episodesToRemove);

            if (episodesToRetry.Count > 0 && _libraryManager != null)
            {
                _logger.LogInformation("[MR] [DEBUG] Retrying {Count} episodes from queue", episodesToRetry.Count);
                ProcessRetryQueueBatch(episodesToRetry, cfg, now, out var additionalToRemove);
                RemoveFromRetryQueueBatch(additionalToRemove);
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

    private (List<Guid> toRetry, List<Guid> toRemove) EvaluateRetryQueue(DateTime now)
    {
        var toRetry = new List<Guid>();
        var toRemove = new List<Guid>();
        foreach (var kvp in _episodeRetryQueue.ToList())
        {
            var episodeId = kvp.Key;
            var lastRetry = kvp.Value;
            var retryCount = _episodeRetryCount.GetValueOrDefault(episodeId, 0);
            var reason = _episodeRetryReason.GetValueOrDefault(episodeId, "Unknown");
            var timeSinceLastRetry = now - lastRetry;
            var shouldRetry = timeSinceLastRetry.TotalMinutes >= RetryDelayMinutes;

            _logger.LogInformation("[MR] [DEBUG] Episode {Id}: Retry count={Count}/{Max}, Time since last retry={Minutes:F1} min, Should retry={ShouldRetry}", 
                episodeId, retryCount, MaxRetryAttempts, timeSinceLastRetry.TotalMinutes, shouldRetry);
            _logger.LogInformation("[MR] [DEBUG] Episode {Id}: Reason={Reason}", episodeId, reason);

            if (retryCount >= MaxRetryAttempts)
            {
                _logger.LogWarning("[MR] [DEBUG] Removing episode {Id} from retry queue: Max retry attempts ({MaxAttempts}) reached", 
                    episodeId, MaxRetryAttempts);
                toRemove.Add(episodeId);
                continue;
            }

            if (shouldRetry)
            {
                _logger.LogInformation("[MR] [DEBUG] Episode {Id} will be retried (enough time has passed)", episodeId);
                toRetry.Add(episodeId);
            }
            else
            {
                _logger.LogInformation("[MR] [DEBUG] Episode {Id} will not be retried yet (only {Minutes:F1} minutes since last retry, need {RequiredMinutes} minutes)", 
                    episodeId, timeSinceLastRetry.TotalMinutes, RetryDelayMinutes);
            }
        }
        return (toRetry, toRemove);
    }

    private void RemoveFromRetryQueueBatch(IEnumerable<Guid> episodeIds)
    {
        foreach (var episodeId in episodeIds)
        {
            _episodeRetryQueue.Remove(episodeId);
            _episodeRetryCount.Remove(episodeId);
            _episodeRetryReason.Remove(episodeId);
        }
    }

    private void ProcessRetryQueueBatch(IReadOnlyList<Guid> episodeIds, PluginConfiguration cfg, DateTime now, out List<Guid> toRemove)
    {
        toRemove = new List<Guid>();
        foreach (var episodeId in episodeIds)
        {
            try
            {
                _logger.LogInformation("[MR] [DEBUG] === Retrying Episode {Id} ===", episodeId);
                var item = _libraryManager.GetItemById(episodeId);
                if (item is Episode episode)
                {
                    var retryCount = _episodeRetryCount.GetValueOrDefault(episodeId, 0);
                    _episodeRetryCount[episodeId] = retryCount + 1;
                    _episodeRetryQueue[episodeId] = now;
                    _logger.LogInformation("[MR] [DEBUG] Retry attempt {Attempt}/{MaxAttempts} for episode {Id}", 
                        retryCount + 1, MaxRetryAttempts, episodeId);
                    _logger.LogInformation("[MR] [DEBUG] Episode Name: {Name}", episode.Name ?? "NULL");
                    _logger.LogInformation("[MR] [DEBUG] Episode Path: {Path}", episode.Path ?? "NULL");
                    HandleEpisodeUpdate(episode, cfg, now, isBulkProcessing: false);
                    _logger.LogInformation("[MR] [DEBUG] === Retry Attempt Complete for Episode {Id} ===", episodeId);
                }
                else
                {
                    _logger.LogWarning("[MR] [DEBUG] Episode {Id} no longer exists or is not an Episode. Removing from queue.", episodeId);
                    toRemove.Add(episodeId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] Error retrying episode {Id}: {Message}", episodeId, ex.Message);
            }
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
            DebugLogHelper.SafeAppend( logJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] [DEBUG] [EPISODE-TITLE-EXTRACTION] ERROR logging title extraction: {Error}", ex.Message);
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "EPISODE-TITLE-EXTRACTION", location = "RenameCoordinator.cs:301", message = "ERROR logging title extraction", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
            if (e == null)
            {
                _logger.LogWarning("[MR] [SAFEGUARD] HandleItemUpdated: ItemChangeEventArgs is null. Ignoring.");
                return;
            }
            if (cfg == null)
            {
                _logger.LogWarning("[MR] [SAFEGUARD] HandleItemUpdated: PluginConfiguration is null. Ignoring.");
                return;
            }

            ProcessRetryQueue(cfg, DateTime.UtcNow);
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "C", location = "RenameCoordinator.cs:42", message = "HandleItemUpdated entry", data = new { itemType = e.Item?.GetType().Name ?? "null", itemName = e.Item?.Name ?? "null", enabled = cfg.Enabled, renameSeriesFolders = cfg.RenameSeriesFolders, dryRun = cfg.DryRun, requireProviderIdMatch = cfg.RequireProviderIdMatch, onlyRenameWhenProviderIdsChange = cfg.OnlyRenameWhenProviderIdsChange }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");

            _logger.LogInformation("[MR] ===== ItemUpdated Event Received =====");
            _logger.LogInformation("[MR] Item Type: {Type}", e.Item?.GetType().Name ?? "NULL");
            _logger.LogInformation("[MR] Item Name: {Name}", e.Item?.Name ?? "NULL");
            _logger.LogInformation("[MR] Item ID: {Id}", e.Item?.Id.ToString() ?? "NULL");
            _logger.LogInformation(
                "[MR] Configuration: Enabled={Enabled}, RenameSeriesFolders={RenameSeriesFolders}, RenameSeasonFolders={RenameSeasonFolders}, RenameEpisodeFiles={RenameEpisodeFiles}, RenameMovieFolders={RenameMovieFolders}, DryRun={DryRun}, RequireProviderIdMatch={RequireProviderIdMatch}, OnlyRenameWhenProviderIdsChange={OnlyRenameWhenProviderIdsChange}, ProcessDuringLibraryScans={ProcessDuringLibraryScans}",
                cfg.Enabled, cfg.RenameSeriesFolders, cfg.RenameSeasonFolders, cfg.RenameEpisodeFiles, cfg.RenameMovieFolders, cfg.DryRun, cfg.RequireProviderIdMatch, cfg.OnlyRenameWhenProviderIdsChange, cfg.ProcessDuringLibraryScans);

            if (!cfg.Enabled)
            {
                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "RenameCoordinator.cs:49", message = "Plugin disabled", data = new { itemType = e.Item?.GetType().Name ?? "null" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                _logger.LogWarning("[MR] SKIP: Plugin is disabled in configuration");
                return;
            }

            var now = DateTime.UtcNow;
            DispatchItemUpdatedByType(e, cfg, now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] CRITICAL ERROR in HandleItemUpdated: {Message}", ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "RenameCoordinator.cs:252", message = "CRITICAL ERROR in HandleItemUpdated", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
    }

    private void DispatchItemUpdatedByType(ItemChangeEventArgs e, PluginConfiguration cfg, DateTime now)
    {
        if (e.Item is Series series)
        {
            HandleItemUpdatedSeries(series, cfg, now);
            return;
        }
        if (e.Item is Season season)
        {
            HandleItemUpdatedSeason(season, cfg, now);
            return;
        }
        if (e.Item is Episode episode)
        {
            HandleItemUpdatedEpisode(episode, cfg, now);
            return;
        }
        if (e.Item is Movie movie)
        {
            HandleItemUpdatedMovie(movie, cfg, now);
            return;
        }

            // Skip other item types
            _logger.LogInformation("[MR] SKIP: Item is not a Series, Season, Episode, or Movie. Type={Type}, Name={Name}", e.Item?.GetType().Name ?? "NULL", e.Item?.Name ?? "NULL");
    }

    private void HandleItemUpdatedSeries(Series series, PluginConfiguration cfg, DateTime now)
    {
        try
        {
            var logData = new { runId = "run1", hypothesisId = "SERIES-ITEM-UPDATED", location = "RenameCoordinator.cs:406", message = "Series ItemUpdated event received", data = new { seriesId = series.Id.ToString(), seriesName = series.Name ?? "NULL", seriesPath = series.Path ?? "NULL", providerIdsCount = series.ProviderIds?.Count ?? 0, providerIds = series.ProviderIds != null ? string.Join(",", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")) : "null" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
            _logger.LogInformation("[MR] [DEBUG] [SERIES-ITEM-UPDATED] Series ItemUpdated: Id={Id}, Name='{Name}', Path={Path}, ProviderIds={ProviderIds}", series.Id, series.Name ?? "NULL", series.Path ?? "NULL", series.ProviderIds != null ? string.Join(",", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")) : "NONE");
        }
        catch (Exception ex)
        {
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "SERIES-ITEM-UPDATED", location = "RenameCoordinator.cs:406", message = "ERROR logging series event", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
        var timeSinceLastAction = now - _lastGlobalActionUtc;
        if (timeSinceLastAction < _globalMinInterval)
        {
            _logger.LogInformation("[MR] SKIP: Global debounce active. Time since last action: {Seconds} seconds (min: {MinSeconds})", timeSinceLastAction.TotalSeconds, _globalMinInterval.TotalSeconds);
            return;
        }
        _lastGlobalActionUtc = now;
        if (!cfg.RenameSeriesFolders)
        {
            _logger.LogInformation("[MR] SKIP: RenameSeriesFolders is disabled in configuration");
            return;
        }
        HandleSeriesUpdate(series, cfg, now);
    }

    private void HandleItemUpdatedSeason(Season season, PluginConfiguration cfg, DateTime now)
    {
        try
        {
            _logger.LogInformation("[MR] [DEBUG] [SEASON-ITEM-UPDATED] Season ItemUpdated: Id={Id}, Name='{Name}', Path={Path}, SeasonNumber={SeasonNumber}, SeriesId={SeriesId}, SeriesName='{SeriesName}'", season.Id, season.Name ?? "NULL", season.Path ?? "NULL", season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", season.Series?.Id.ToString() ?? "NULL", season.Series?.Name ?? "NULL");
            var logData = new { runId = "run1", hypothesisId = "SEASON-ITEM-UPDATED", location = "RenameCoordinator.cs:457", message = "Season ItemUpdated event received", data = new { seasonId = season.Id.ToString(), seasonName = season.Name ?? "NULL", seasonPath = season.Path ?? "NULL", seasonNumber = season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", seriesId = season.Series?.Id.ToString() ?? "NULL", seriesName = season.Series?.Name ?? "NULL", seriesPath = season.Series?.Path ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] [DEBUG] [SEASON-ITEM-UPDATED] ERROR logging season event: {Error}", ex.Message);
        }
        var timeSinceLastAction = now - _lastGlobalActionUtc;
        if (timeSinceLastAction < _globalMinInterval)
        {
            _logger.LogInformation("[MR] [DEBUG] [SEASON-SKIP-GLOBAL-DEBOUNCE] SKIP: Global debounce active. Time since last action: {Seconds} seconds (min: {MinSeconds})", timeSinceLastAction.TotalSeconds, _globalMinInterval.TotalSeconds);
            return;
        }
        _lastGlobalActionUtc = now;
        if (!cfg.RenameSeasonFolders)
        {
            _logger.LogInformation("[MR] [DEBUG] [SEASON-SKIP-CONFIG-DISABLED] SKIP: RenameSeasonFolders is disabled in configuration");
            return;
        }
        try
        {
            _logger.LogInformation("[MR] [DEBUG] [SEASON-PROCESSING-START] Starting HandleSeasonUpdate for Season: {Name}, ID: {Id}, SeasonNumber: {SeasonNumber}", season.Name ?? "NULL", season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] [DEBUG] [SEASON-PROCESSING-START] ERROR logging season processing start: {Error}", ex.Message);
        }
        HandleSeasonUpdate(season, cfg, now);
        try
        {
            _logger.LogInformation("[MR] [DEBUG] [SEASON-PROCESSING-COMPLETE] HandleSeasonUpdate completed for Season: {Name}, ID: {Id}", season.Name ?? "NULL", season.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] [DEBUG] [SEASON-PROCESSING-COMPLETE] ERROR logging season processing complete: {Error}", ex.Message);
        }
    }

    private void HandleItemUpdatedEpisode(Episode episode, PluginConfiguration cfg, DateTime now)
    {
        try
        {
            var currentFileName = !string.IsNullOrWhiteSpace(episode.Path) ? Path.GetFileNameWithoutExtension(episode.Path) : "NULL";
            var isFilenamePattern = !string.IsNullOrWhiteSpace(episode.Name) && System.Text.RegularExpressions.Regex.IsMatch(episode.Name, @"[Ss]\d+[Ee]\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var logData = new { runId = "run1", hypothesisId = "EPISODE-METADATA", location = "RenameCoordinator.cs:405", message = "Episode ItemUpdated event - full metadata state", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", episodeNameIsFilenamePattern = isFilenamePattern, episodePath = episode.Path ?? "NULL", currentFileName = currentFileName, episodeIndexNumber = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", parentIndexNumber = episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", seriesName = episode.Series?.Name ?? "NULL", seriesId = episode.Series?.Id.ToString() ?? "NULL", seriesPath = episode.Series?.Path ?? "NULL", seriesHasProviderIds = episode.Series?.ProviderIds != null && episode.Series.ProviderIds.Count > 0 }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
            _logger.LogInformation("[MR] [EPISODE-METADATA] Episode ItemUpdated: Id={Id}, Name='{Name}' (isPattern={IsPattern}), Path={Path}, S{Season}E{Episode}", episode.Id, episode.Name ?? "NULL", isFilenamePattern, episode.Path ?? "NULL", episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "??", episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "??");
        }
        catch (Exception ex)
        {
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "EPISODE-METADATA", location = "RenameCoordinator.cs:405", message = "ERROR logging episode metadata", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
        try
        {
            var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-A", location = "RenameCoordinator.cs:124", message = "Episode ItemUpdated event received", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", episodePath = episode.Path ?? "NULL", episodeIndexNumber = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", parentIndexNumber = episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", seriesName = episode.Series?.Name ?? "NULL", seriesPath = episode.Series?.Path ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
            _logger.LogInformation("[MR] [MULTI-EP-A] Episode ItemUpdated event: EpisodeId={Id}, Name={Name}, Path={Path}, IndexNumber={IndexNumber}", episode.Id, episode.Name ?? "NULL", episode.Path ?? "NULL", episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
        }
        catch (Exception ex)
        {
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-A", location = "RenameCoordinator.cs:124", message = "ERROR logging episode event", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
        try
        {
            var indexNumberImmediate = episode.IndexNumber;
            var parentIndexNumberImmediate = episode.ParentIndexNumber;
            var episodeTypeImmediate = episode.GetType().FullName;
            var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "RenameCoordinator.cs:124", message = "Episode cast successful - immediate IndexNumber check", data = new { episodeType = episodeTypeImmediate, indexNumber = indexNumberImmediate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", parentIndexNumber = parentIndexNumberImmediate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
            _logger.LogInformation("[MR] [DEBUG-HYP-A] Episode cast: Type={Type}, IndexNumber={IndexNumber}, ParentIndexNumber={ParentIndexNumber}, Id={Id}, Name={Name}", episodeTypeImmediate, indexNumberImmediate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", parentIndexNumberImmediate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episode.Id, episode.Name ?? "NULL");
        }
        catch (Exception ex)
        {
            var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "RenameCoordinator.cs:124", message = "ERROR checking IndexNumber immediately after cast", data = new { error = ex.Message, episodeId = episode?.Id.ToString() ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
            _logger.LogError(ex, "[MR] [DEBUG-HYP-A] ERROR checking IndexNumber immediately after cast: {Error}", ex.Message);
        }
        var seasonNumForLogging = episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
        var episodeNumForLogging = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
        if (!cfg.RenameEpisodeFiles)
        {
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-B", location = "RenameCoordinator.cs:148", message = "Episode skipped - RenameEpisodeFiles disabled", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNumForLogging, episodeNumber = episodeNumForLogging }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            _logger.LogInformation("[MR] [DEBUG] [EPISODE-SKIP-CONFIG-DISABLED] SKIP: RenameEpisodeFiles is disabled in configuration. Season={Season}, Episode={Episode}", seasonNumForLogging, episodeNumForLogging);
            _logger.LogInformation("[MR] ===== Episode Processing Complete (Skipped - Config Disabled) =====");
            return;
        }
        HandleEpisodeUpdate(episode, cfg, now);
    }

    private void HandleItemUpdatedMovie(Movie movie, PluginConfiguration cfg, DateTime now)
    {
        var timeSinceLastAction = now - _lastGlobalActionUtc;
        if (timeSinceLastAction < _globalMinInterval)
        {
            _logger.LogInformation("[MR] SKIP: Global debounce active. Time since last action: {Seconds} seconds (min: {MinSeconds})", timeSinceLastAction.TotalSeconds, _globalMinInterval.TotalSeconds);
            return;
        }
        _lastGlobalActionUtc = now;
        if (!cfg.RenameMovieFolders)
        {
            _logger.LogInformation("[MR] SKIP: RenameMovieFolders is disabled in configuration");
            return;
        }
        HandleMovieUpdate(movie, cfg, now);
    }

    private (string? path, bool seriesRenameOnCooldown) ValidateSeriesPathAndCooldown(Series series, PluginConfiguration cfg, DateTime now)
    {
        bool seriesRenameOnCooldown = false;
        if (_lastAttemptUtcByItem.TryGetValue(series.Id, out var lastTry))
        {
            var timeSinceLastTry = (now - lastTry).TotalSeconds;
            if (timeSinceLastTry < cfg.PerItemCooldownSeconds)
            {
                _logger.LogInformation("[MR] SKIP: Cooldown active for series folder rename. SeriesId={Id}, Name={Name}, Time since last try: {Seconds} seconds (cooldown: {CooldownSeconds})", series.Id, series.Name, timeSinceLastTry.ToString(System.Globalization.CultureInfo.InvariantCulture), cfg.PerItemCooldownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                _logger.LogInformation("[MR] [DEBUG] Cooldown active, but will still check if episodes need processing");
                seriesRenameOnCooldown = true;
            }
        }
        if (!seriesRenameOnCooldown)
            _lastAttemptUtcByItem[series.Id] = now;

        var path = series.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning("[MR] SKIP: Series has no path. SeriesId={Id}, Name={Name}", series.Id, series.Name);
            return (null, false);
        }
        if (!Directory.Exists(path))
        {
            _logger.LogWarning("[MR] SKIP: Series path does not exist on disk. Path={Path}, SeriesId={Id}, Name={Name}", path, series.Id, series.Name);
            return (null, false);
        }
        return (path, seriesRenameOnCooldown);
    }

    private string LogSeriesProviderIdsAndValidate(Series series, PluginConfiguration cfg)
    {
        var providerIdsCount = series.ProviderIds?.Count ?? 0;
        var providerIdsString = series.ProviderIds != null ? string.Join(", ", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")) : "NONE";
        _logger.LogInformation("[MR] === Provider IDs Details ===");
        _logger.LogInformation("[MR] Provider IDs Count: {Count}", providerIdsCount);
        _logger.LogInformation("[MR] All Provider IDs: {Values}", providerIdsString);
        if (series.ProviderIds != null && series.ProviderIds.Count > 0)
        {
            foreach (var kv in series.ProviderIds)
                _logger.LogInformation("[MR]   - {Provider}: {Id}", kv.Key, kv.Value ?? "NULL");
        }
        else
            _logger.LogWarning("[MR]   - No provider IDs found!");
        if (cfg.RequireProviderIdMatch && (series.ProviderIds == null || series.ProviderIds.Count == 0))
        {
            _logger.LogWarning("[MR] SKIP: RequireProviderIdMatch is true but no ProviderIds found. Name={Name}", series.Name);
            return null;
        }
        if (cfg.RequireProviderIdMatch)
            _logger.LogInformation("[MR] Provider ID requirement satisfied");
        return providerIdsString;
    }

    private (string name, int? year, string yearSource) GetSeriesYearAndName(Series series, string path, string providerIdsString)
    {
        var name = series.Name?.Trim();
        int? year = series.ProductionYear;
        string yearSource = "ProductionYear";
        if (year is null && series.PremiereDate.HasValue)
        {
            year = series.PremiereDate.Value.Year;
            yearSource = "PremiereDate";
        }
        DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "YEAR-DETECT", location = "RenameCoordinator.cs", message = "Year detection from metadata", data = new { seriesId = series.Id.ToString(), seriesName = name ?? "NULL", productionYear = series.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", premiereDate = series.PremiereDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", premiereDateYear = series.PremiereDate?.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", finalYear = year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", yearSource = yearSource, currentFolderPath = path, currentFolderName = Path.GetFileName(path) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        _logger.LogInformation("[MR] === Year Detection from Metadata (BEFORE Correction) ===");
        _logger.LogInformation("[MR] Series: {Name}, ID: {Id}", name ?? "NULL", series.Id);
        _logger.LogInformation("[MR] ProductionYear (from Jellyfin): {ProductionYear}", series.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
        _logger.LogInformation("[MR] PremiereDate (from Jellyfin): {PremiereDate}, Year: {PremiereDateYear}", series.PremiereDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", series.PremiereDate?.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
        _logger.LogInformation("[MR] Year from Metadata (BEFORE correction): {Year} (Source: {YearSource})", year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", yearSource);
        _logger.LogInformation("[MR] Current Folder Name: {CurrentFolderName}", Path.GetFileName(path));
        _logger.LogInformation("[MR] Series metadata: Name={Name}, Year={Year} (from {YearSource}), ProviderIds={ProviderIds}", name ?? "NULL", year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", yearSource, providerIdsString);
        if (string.IsNullOrWhiteSpace(name))
        {
            _logger.LogWarning("[MR] SKIP: Missing required metadata. Name={Name}", name ?? "NULL");
            return (null, null, null);
        }
        return (name, year, yearSource);
    }

    private static (string newHash, string? oldHash, bool hasOldHash, bool providerIdsChanged, bool isFirstTime, bool hasProviderIds) ComputeSeriesProviderHashState(Series series, Dictionary<Guid, string> providerHashByItem)
    {
        var hasProviderIds = series.ProviderIds != null && series.ProviderIds.Count > 0;
        var newHash = hasProviderIds && series.ProviderIds != null ? ProviderIdHelper.ComputeProviderHash(series.ProviderIds) : string.Empty;
        var hasOldHash = providerHashByItem.TryGetValue(series.Id, out var oldHash);
        var providerIdsChanged = hasOldHash && !string.Equals(newHash, oldHash, StringComparison.Ordinal);
        var isFirstTime = !hasOldHash;
        return (newHash, oldHash, hasOldHash, providerIdsChanged, isFirstTime, hasProviderIds);
    }

    private void LogProviderHashCheck(Series series, string name, PluginConfiguration cfg, bool forceProcessing, string newHash, string? oldHash, bool hasProviderIds, bool providerIdsChanged, bool isFirstTime, bool hasOldHash)
    {
        DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "B", location = "RenameCoordinator.cs", message = "Provider hash check", data = new { oldHash = oldHash ?? "(none)", newHash = newHash, hasOldHash = hasOldHash, hasProviderIds = hasProviderIds, seriesName = name, providerIds = series.ProviderIds != null ? string.Join(",", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")) : "null", processDuringLibraryScans = cfg.ProcessDuringLibraryScans, onlyRenameWhenProviderIdsChange = cfg.OnlyRenameWhenProviderIdsChange, providerIdsChanged = providerIdsChanged, isFirstTime = isFirstTime }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        _logger.LogInformation("[MR] === Provider Hash Check ===");
        _logger.LogInformation("[MR] ProcessDuringLibraryScans: {ProcessDuringLibraryScans}, OnlyRenameWhenProviderIdsChange: {OnlyRenameWhenProviderIdsChange}, HasProviderIds: {HasProviderIds}, OldHashExists: {HasOldHash}, NewHash: {NewHash}, ProviderIdsChanged: {Changed}, FirstTime: {FirstTime}, Series: {Name}", cfg.ProcessDuringLibraryScans, cfg.OnlyRenameWhenProviderIdsChange, hasProviderIds, hasOldHash, newHash, providerIdsChanged, isFirstTime, name);
        try
        {
            _logger.LogInformation("[MR] [DEBUG] [PROVIDER-HASH-CHECK] Provider hash check state: SeriesId={SeriesId}, SeriesName='{SeriesName}', OnlyRenameWhenProviderIdsChange={OnlyRenameWhenProviderIdsChange}, HasProviderIds={HasProviderIds}, HasOldHash={HasOldHash}, OldHash={OldHash}, NewHash={NewHash}, ProviderIdsChanged={ProviderIdsChanged}, IsFirstTime={IsFirstTime}, ForceProcessing={ForceProcessing}", series.Id, name ?? "NULL", cfg.OnlyRenameWhenProviderIdsChange, hasProviderIds, hasOldHash, oldHash ?? "(none)", newHash, providerIdsChanged, isFirstTime, forceProcessing);
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "PROVIDER-HASH-CHECK", location = "RenameCoordinator.cs", message = "Provider hash check state", data = new { seriesId = series.Id.ToString(), seriesName = name ?? "NULL", processDuringLibraryScans = cfg.ProcessDuringLibraryScans, onlyRenameWhenProviderIdsChange = cfg.OnlyRenameWhenProviderIdsChange, hasProviderIds = hasProviderIds, hasOldHash = hasOldHash, oldHash = oldHash ?? "(none)", newHash = newHash, providerIdsChanged = providerIdsChanged, isFirstTime = isFirstTime, forceProcessing = forceProcessing }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] [DEBUG] [PROVIDER-HASH-CHECK] ERROR logging hash check: {Error}", ex.Message);
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "PROVIDER-HASH-CHECK", location = "RenameCoordinator.cs", message = "ERROR logging hash check", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
    }

    private void EnqueueSeriesUpdateAndCleanup(DateTime currentTime, bool providerIdsChanged, bool isFirstTime, Series series, string name)
    {
        if (!providerIdsChanged && !isFirstTime)
            _seriesUpdateTimestamps.Enqueue(currentTime);
        else
            _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE-SKIP] Skipping queue addition - Provider IDs changed (Identify flow) or first time. ProviderIdsChanged={Changed}, IsFirstTime={FirstTime}", providerIdsChanged, isFirstTime);
        try
        {
            _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE] === Series Update Added to Queue === Series: {Name}, ID: {Id}, Timestamp: {Timestamp}, Queue size: {Count}", name ?? "NULL", series.Id, currentTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), _seriesUpdateTimestamps.Count);
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "SERIES-UPDATE-QUEUE", location = "RenameCoordinator.cs", message = "Series update added to queue", data = new { seriesId = series.Id.ToString(), seriesName = name ?? "NULL", timestamp = currentTime.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), queueSizeBefore = providerIdsChanged || isFirstTime ? _seriesUpdateTimestamps.Count : _seriesUpdateTimestamps.Count - 1, queueSizeAfter = _seriesUpdateTimestamps.Count, providerIdsChanged = providerIdsChanged, isFirstTime = isFirstTime, addedToQueue = !providerIdsChanged && !isFirstTime }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
        catch (Exception ex) { _logger.LogError(ex, "[MR] [DEBUG] [SERIES-UPDATE-QUEUE] ERROR logging queue addition: {Error}", ex.Message); }

        var timestampsRemoved = 0;
        while (_seriesUpdateTimestamps.Count > 0 && (currentTime - _seriesUpdateTimestamps.Peek()).TotalSeconds > BulkUpdateTimeWindowSeconds)
        {
            _seriesUpdateTimestamps.Dequeue();
            timestampsRemoved++;
        }
        if (timestampsRemoved > 0)
        {
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-QUEUE-CLEAN] Removed {Count} old timestamps, queue size after cleanup: {QueueCount}", timestampsRemoved, _seriesUpdateTimestamps.Count);
                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "SERIES-UPDATE-QUEUE-CLEAN", location = "RenameCoordinator.cs", message = "Old timestamps removed from queue", data = new { seriesId = series.Id.ToString(), seriesName = name ?? "NULL", timestampsRemoved = timestampsRemoved, queueSizeAfterCleanup = _seriesUpdateTimestamps.Count }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            }
            catch (Exception ex) { _logger.LogError(ex, "[MR] [DEBUG] [SERIES-UPDATE-QUEUE-CLEAN] ERROR logging cleanup: {Error}", ex.Message); }
        }
    }

    private void MaybeTriggerBulkProcessing(Series series, string name, PluginConfiguration cfg, DateTime currentTime, bool providerIdsChanged, bool isFirstTime, bool hasProviderIds)
    {
        var isBulkRefresh = _seriesUpdateTimestamps.Count >= BulkUpdateThreshold;
        var timeSinceLastBulkProcessing = (currentTime - _lastBulkProcessingUtc).TotalMinutes;
        var shouldTriggerBulkProcessing = isBulkRefresh && timeSinceLastBulkProcessing >= BulkProcessingCooldownMinutes && !providerIdsChanged && !isFirstTime && hasProviderIds;
        try
        {
            _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] === Bulk Processing Detection === Series: {Name}, ID: {Id}, Queue: {Count}, Threshold: {Threshold}, isBulkRefresh: {IsBulkRefresh}, TimeSinceLastBulk: {Minutes}min, shouldTrigger: {ShouldTrigger}", name ?? "NULL", series.Id, _seriesUpdateTimestamps.Count, BulkUpdateThreshold, isBulkRefresh, timeSinceLastBulkProcessing, shouldTriggerBulkProcessing);
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "BULK-PROCESSING-DETECTION", location = "RenameCoordinator.cs", message = "Bulk processing detection", data = new { seriesId = series.Id.ToString(), seriesName = name ?? "NULL", seriesUpdatesInQueue = _seriesUpdateTimestamps.Count, bulkUpdateThreshold = BulkUpdateThreshold, isBulkRefresh = isBulkRefresh, timeSinceLastBulkProcessing = timeSinceLastBulkProcessing, providerIdsChanged = providerIdsChanged, isFirstTime = isFirstTime, seriesIsIdentified = hasProviderIds, shouldTriggerBulkProcessing = shouldTriggerBulkProcessing }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
        catch (Exception ex) { _logger.LogError(ex, "[MR] [DEBUG] [BULK-PROCESSING-DETECTION] ERROR logging bulk processing detection: {Error}", ex.Message); }

        if (shouldTriggerBulkProcessing)
        {
            try
            {
                _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-TRIGGERED] ===== BULK PROCESSING TRIGGERED ===== Series: {Name}, ID: {Id}, Queue size: {Count}", name ?? "NULL", series.Id, _seriesUpdateTimestamps.Count);
                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "BULK-PROCESSING-TRIGGERED", location = "RenameCoordinator.cs", message = "Bulk processing triggered", data = new { triggeringSeriesId = series.Id.ToString(), triggeringSeriesName = name ?? "NULL", queueSize = _seriesUpdateTimestamps.Count, providerIdsChanged = providerIdsChanged, isFirstTime = isFirstTime, timeSinceLastBulkProcessing = timeSinceLastBulkProcessing }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            }
            catch (Exception ex) { _logger.LogError(ex, "[MR] [DEBUG] [BULK-PROCESSING-TRIGGERED] ERROR logging trigger: {Error}", ex.Message); }
            _logger.LogInformation("[MR] === Bulk Refresh Detected (Replace All Metadata) === Detected {Count} series updates in {Seconds}s. Triggering bulk processing.", _seriesUpdateTimestamps.Count, BulkUpdateTimeWindowSeconds);
            _lastBulkProcessingUtc = currentTime;
            _seriesUpdateTimestamps.Clear();
            Task.Run(() => ProcessAllSeriesInLibrary(cfg, currentTime));
        }
        else if (isBulkRefresh && (providerIdsChanged || isFirstTime))
            _logger.LogInformation("[MR] [DEBUG] [BULK-PROCESSING-DETECTION] Skipping bulk processing - Provider IDs changed (Identify flow detected)");
    }

    private (string newHash, string? oldHash, bool providerIdsChanged, bool isFirstTime, bool hasProviderIds) RunSeriesProviderHashQueueAndBulk(Series series, string name, PluginConfiguration cfg, bool forceProcessing)
    {
        var (newHash, oldHash, hasOldHash, providerIdsChanged, isFirstTime, hasProviderIds) = ComputeSeriesProviderHashState(series, _providerHashByItem);
        LogProviderHashCheck(series, name, cfg, forceProcessing, newHash, oldHash, hasProviderIds, providerIdsChanged, isFirstTime, hasOldHash);

        var currentTime = DateTime.UtcNow;
        EnqueueSeriesUpdateAndCleanup(currentTime, providerIdsChanged, isFirstTime, series, name);
        MaybeTriggerBulkProcessing(series, name, cfg, currentTime, providerIdsChanged, isFirstTime, hasProviderIds);

        return (newHash, oldHash, providerIdsChanged, isFirstTime, hasProviderIds);
    }

    private static (bool shouldProceed, string proceedReason) ComputeShouldProceedForSeries(PluginConfiguration cfg, bool forceProcessing, bool providerIdsChanged, bool isFirstTime)
    {
        if (!cfg.OnlyRenameWhenProviderIdsChange)
            return (true, "OnlyRenameWhenProviderIdsChange disabled");
        if (forceProcessing)
            return (true, "Bulk refresh - processing all series regardless of provider ID changes");
        if (providerIdsChanged || isFirstTime)
            return (true, providerIdsChanged ? "Provider IDs changed (Identify flow)" : "First time processing");
        return (false, "Provider IDs unchanged - normal scans only process identified shows. Use 'Replace all metadata' for bulk processing.");
    }

    private void LogSeriesShouldProceedDecision(Series series, string name, bool shouldProceed, string proceedReason, PluginConfiguration cfg, bool forceProcessing, bool providerIdsChanged, bool isFirstTime)
    {
        try
        {
            var logData = new { runId = "run1", hypothesisId = "SHOULD-PROCEED-DECISION", location = "RenameCoordinator.cs", message = "shouldProceed decision made", data = new { seriesId = series.Id.ToString(), seriesName = name ?? "NULL", shouldProceed = shouldProceed, proceedReason = proceedReason, onlyRenameWhenProviderIdsChange = cfg.OnlyRenameWhenProviderIdsChange, forceProcessing = forceProcessing, providerIdsChanged = providerIdsChanged, isFirstTime = isFirstTime }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
            _logger.LogInformation("[MR] [DEBUG] [SHOULD-PROCEED-DECISION] shouldProceed={ShouldProceed}, Reason='{Reason}'", shouldProceed, proceedReason);
        }
        catch (Exception ex)
        {
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "SHOULD-PROCEED-DECISION", location = "RenameCoordinator.cs", message = "ERROR logging shouldProceed", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
    }

    private void LogSeriesSkipAndReturn(Series series, string name, string newHash, string proceedReason, PluginConfiguration cfg, bool providerIdsChanged, bool isFirstTime, bool forceProcessing)
    {
        try
        {
            var logData = new { runId = "run1", hypothesisId = "SKIP-SERIES-PROCESSING", location = "RenameCoordinator.cs", message = "Series processing skipped - shouldProceed=false", data = new { seriesId = series.Id.ToString(), seriesName = name ?? "NULL", hash = newHash, reason = proceedReason, onlyRenameWhenProviderIdsChange = cfg.OnlyRenameWhenProviderIdsChange, providerIdsChanged = providerIdsChanged, isFirstTime = isFirstTime, forceProcessing = forceProcessing }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
            _logger.LogWarning("[MR] [DEBUG] [SKIP-SERIES-PROCESSING] SKIP: {Reason}. Name={Name}, Hash={Hash}", proceedReason, name, newHash);
        }
        catch (Exception ex)
        {
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "SKIP-SERIES-PROCESSING", location = "RenameCoordinator.cs", message = "ERROR logging skip", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
        _logger.LogWarning("[MR] SKIP: {Reason}. Name={Name}, Hash={Hash}", proceedReason, name, newHash);
        _logger.LogInformation("[MR] ===== Processing Complete (Skipped) =====");
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
        if (!cfg.RenameSeriesFolders)
        {
            _logger.LogInformation("[MR] [DEBUG] [SERIES-UPDATE-SKIP] SKIP: RenameSeriesFolders is disabled in configuration. Series: {Name}, Id={Id}", series.Name ?? "NULL", series.Id);
            return;
        }
        _logger.LogInformation("[MR] Processing Series: Name={Name}, Id={Id}, Path={Path}", series.Name, series.Id, series.Path);

        var (path, seriesRenameOnCooldown) = ValidateSeriesPathAndCooldown(series, cfg, now);
        if (path == null)
            return;

        _logger.LogInformation("[MR] Series path verified: {Path}", path);
        var providerIdsString = LogSeriesProviderIdsAndValidate(series, cfg);
        if (providerIdsString == null)
            return;

        var (name, year, yearSource) = GetSeriesYearAndName(series, path, providerIdsString);
        if (name == null)
            return;

        var (newHash, oldHash, providerIdsChanged, isFirstTime, hasProviderIds) = RunSeriesProviderHashQueueAndBulk(series, name, cfg, forceProcessing);
        var seriesIsIdentified = hasProviderIds;

        var (shouldProceed, proceedReason) = ComputeShouldProceedForSeries(cfg, forceProcessing, providerIdsChanged, isFirstTime);
        LogSeriesShouldProceedDecision(series, name, shouldProceed, proceedReason, cfg, forceProcessing, providerIdsChanged, isFirstTime);
        if (!shouldProceed)
        {
            LogSeriesSkipAndReturn(series, name, newHash, proceedReason, cfg, providerIdsChanged, isFirstTime, forceProcessing);
            return;
        }

        ApplySeriesRenameAndEpisodeProcessing(series, cfg, path, name, ref year, yearSource, newHash, oldHash, hasProviderIds, providerIdsChanged, isFirstTime, seriesRenameOnCooldown, seriesIsIdentified, proceedReason, now);
    }

    private void ApplySeriesRenameAndEpisodeProcessing(Series series, PluginConfiguration cfg, string path, string name, ref int? year, string yearSource, string newHash, string? oldHash, bool hasProviderIds, bool providerIdsChanged, bool isFirstTime, bool seriesRenameOnCooldown, bool seriesIsIdentified, string proceedReason, DateTime now)
    {
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
                    DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:411", message = "Provider IDs changed - starting detection", data = new { seriesId = series.Id.ToString(), seriesName = series.Name ?? "NULL", previousProviderIds = previousProviderIds.Select(kv => $"{kv.Key}={kv.Value}").ToList(), currentProviderIds = series.ProviderIds?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>(), preferredProviders = cfg.PreferredSeriesProviders?.ToList() ?? new List<string>() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
                        DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:448", message = "Changed providers collected", data = new { changedProvidersCount = changedProviders.Count, changedProviders = changedProviders.Select(cp => new { key = cp.Key, value = cp.Value, changeType = cp.ChangeType }).ToList(), preferredList = preferredList.ToList() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
                                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:460", message = "Provider selected from preferred list", data = new { selectedProvider = selectedProviderKey, selectedId = selectedProviderId, changeType = match.ChangeType, preferred = preferred }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
                            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:470", message = "Provider selected (first changed, no preferred match)", data = new { selectedProvider = selectedProviderKey, selectedId = selectedProviderId, changeType = changedProviders[0].ChangeType }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                            // #endregion
                            _logger.LogInformation("[MR] ✓ Selected first changed provider: {Provider}={Id} ({ChangeType})", 
                                selectedProviderKey, selectedProviderId, changedProviders[0].ChangeType);
                        }
                    }
                    else
                    {
                        // #region agent log - No Changed Provider Detected
                        DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:492", message = "WARNING: No changed provider detected - all IDs already present", data = new { previousProviderIds = previousProviderIds.Select(kv => $"{kv.Key}={kv.Value}").ToList(), currentProviderIds = series.ProviderIds?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>(), preferredProviders = cfg.PreferredSeriesProviders?.ToList() ?? new List<string>() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                        // #endregion
                        _logger.LogWarning("[MR] ⚠️ No newly added/changed provider detected (all IDs were already present). This may indicate the wrong match was selected.");
                        _logger.LogWarning("[MR] ⚠️ If you selected a different match in Identify, you may need to clear the series metadata first, then re-identify.");
                    }
                }
                else if (isFirstTime)
                {
                    // #region agent log - First Time Processing
                    DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:498", message = "First time processing - cannot detect user selection", data = new { currentProviderIds = series.ProviderIds?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>(), preferredProviders = cfg.PreferredSeriesProviders?.ToList() ?? new List<string>() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
                    DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:504", message = "FINAL: Using user-selected provider", data = new { selectedProvider = providerLabel, selectedId = providerId, allProviderIds = series.ProviderIds?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
                        DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:522", message = "FINAL: Using preferred list provider (fallback)", data = new { selectedProvider = providerLabel, selectedId = providerId, preferredList = preferredList.ToList(), allProviderIds = series.ProviderIds?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>(), whyFallback = selectedProviderKey == null ? "No user-selected provider detected" : "User-selected provider was null" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "YEAR-DETECT", location = "RenameCoordinator.cs:594", message = "Before folder name generation", data = new { seriesName = name, year = year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", yearSource = yearSource, providerLabel = providerLabel ?? "NULL", providerId = providerId ?? "NULL", currentFolderName = currentFolderName, format = cfg.SeriesFolderFormat, productionYear = series.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", premiereDate = series.PremiereDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "PROVIDER-DETECT", location = "RenameCoordinator.cs:604", message = "Final folder name generation", data = new { seriesName = name, year = year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", providerLabel = providerLabel ?? "NULL", providerId = providerId ?? "NULL", currentFolderName = currentFolderName, desiredFolderName = desiredFolderName, format = cfg.SeriesFolderFormat }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            // #endregion
            
            _logger.LogInformation("[MR] Current Folder Name: {Current}", currentFolderName);
            _logger.LogInformation("[MR] Desired Folder Name: {Desired}", desiredFolderName);
            _logger.LogInformation("[MR] Year Available: {HasYear}", year.HasValue);

            _logger.LogInformation("[MR] Full Current Path: {Path}", path);
            _logger.LogInformation("[MR] Dry Run Mode: {DryRun}", cfg.DryRun);

            // Safeguard: ensure desired series folder name is valid before calling rename service
            if (!SafeName.IsValidFolderOrFileName(desiredFolderName))
            {
                _logger.LogWarning("[MR] [SAFEGUARD] Series folder rename skipped: desired folder name is invalid. Series: {Name}, Id: {Id}", series.Name ?? "NULL", series.Id);
            }
            else
            {
                // #region agent log
                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "RenameCoordinator.cs:186", message = "Attempting rename", data = new { seriesName = name, currentPath = path, desiredFolderName = desiredFolderName, dryRun = cfg.DryRun }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                // #endregion

                renameSuccessful = _pathRenamer.TryRenameSeriesFolder(series, desiredFolderName, cfg.DryRun);
            }
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
                        DebugLogHelper.SafeAppend( logJson);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[MR] [DEBUG] [PROCESS-ALL-EPISODES] ERROR logging ProcessAllEpisodesFromSeries call: {Error}", ex.Message);
                        DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "PROCESS-ALL-EPISODES", location = "RenameCoordinator.cs:1001", message = "ERROR logging ProcessAllEpisodesFromSeries call", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
                        DebugLogHelper.SafeAppend( logJson);
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

    private bool ProcessAllEpisodesFromSeriesGuard(Series series, PluginConfiguration cfg)
    {
        if (!cfg.RenameEpisodeFiles)
        {
            _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES-SKIP] SKIP: RenameEpisodeFiles is disabled in configuration. Series: {Name}, ID: {Id}", series.Name ?? "NULL", series.Id);
            return false;
        }
        _folderSeasonEpisodeMapCache.Clear();
        try
        {
            _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ENTRY] ===== ProcessAllEpisodesFromSeries ENTRY =====");
            _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ENTRY] Series: {Name}, ID: {Id}, Path: {Path}", series.Name ?? "NULL", series.Id, series.Path ?? "NULL");
            _logger.LogInformation("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ENTRY] RenameEpisodeFiles: {RenameEpisodeFiles}, DryRun: {DryRun}", cfg.RenameEpisodeFiles, cfg.DryRun);
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "PROCESS-ALL-EPISODES-ENTRY", location = "RenameCoordinator.cs", message = "ProcessAllEpisodesFromSeries entry", data = new { seriesId = series.Id.ToString(), seriesName = series.Name ?? "NULL", seriesPath = series.Path ?? "NULL", renameEpisodeFiles = cfg.RenameEpisodeFiles, dryRun = cfg.DryRun, libraryManagerAvailable = _libraryManager != null }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] [DEBUG] [PROCESS-ALL-EPISODES-ENTRY] ERROR logging entry: {Error}", ex.Message);
        }
        if (_libraryManager == null)
        {
            _logger.LogWarning("[MR] [DEBUG] [PROCESS-ALL-EPISODES-ENTRY] LibraryManager is not available. Cannot process all episodes from series.");
            return false;
        }
        return true;
    }

    private bool TryLoadEpisodesAndSeasonsForSeries(Series series, out List<Episode> allEpisodes, out List<Season> allSeasons)
    {
        allEpisodes = new List<Episode>();
        allSeasons = new List<Season>();
        try
        {
            var query = new InternalItemsQuery { ParentId = series.Id, Recursive = true };
            _logger.LogInformation("[MR] === Executing Episode Query ===");
            _logger.LogInformation("[MR] Query: ParentId={ParentId}, Recursive={Recursive}", series.Id, true);
            try
            {
                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "EPISODE-QUERY-EXECUTION", location = "RenameCoordinator.cs", message = "Executing episode query", data = new { seriesId = series.Id.ToString(), seriesName = series.Name ?? "NULL", seriesPath = series.Path ?? "NULL", queryParentId = series.Id.ToString(), queryRecursive = true, libraryManagerAvailable = _libraryManager != null }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            }
            catch (Exception logEx) { _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-QUERY-EXECUTION] ERROR logging: {Error}", logEx.Message); }
            var allItems = _libraryManager.GetItemList(query);
            allEpisodes = allItems.OfType<Episode>().ToList();
            allSeasons = allItems.OfType<Season>().ToList();
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                _logger.LogError(ex, "[MR] [DEBUG] [EPISODE-QUERY-ERROR] ERROR retrieving episodes using GetItemList");
                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "EPISODE-QUERY-ERROR", location = "RenameCoordinator.cs", message = "Error retrieving episodes - attempting fallback", data = new { seriesId = series.Id.ToString(), seriesName = series.Name ?? "NULL", exceptionType = ex.GetType().FullName, exceptionMessage = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            }
            catch (Exception logEx) { _logger.LogError(logEx, "[MR] [DEBUG] [EPISODE-QUERY-ERROR] ERROR logging: {Error}", logEx.Message); }
            _logger.LogWarning(ex, "[MR] Could not retrieve episodes using GetItemList. Trying alternative method: {Message}", ex.Message);
            try
            {
                _logger.LogInformation("[MR] [DEBUG] Attempting fallback method to retrieve episodes");
                var query = new InternalItemsQuery { ParentId = series.Id, Recursive = true };
                var allItems = _libraryManager.GetItemList(query);
                allEpisodes = allItems.OfType<Episode>().ToList();
                allSeasons = allItems.OfType<Season>().ToList();
                _logger.LogInformation("[MR] Retrieved {Count} episodes using fallback recursive method", allEpisodes.Count);
                return true;
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "[MR] Could not retrieve episodes using fallback method: {Message}", fallbackEx.Message);
                _logger.LogError("[MR] [DEBUG] Cannot proceed with episode processing - both query methods failed");
                return false;
            }
        }
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
            if (!ProcessAllEpisodesFromSeriesGuard(series, cfg))
                return;

            _logger.LogInformation("[MR] === Processing All Episodes from All Seasons ===");
            _logger.LogInformation("[MR] Series: {Name}, ID: {Id}", series.Name, series.Id);

            if (!TryLoadEpisodesAndSeasonsForSeries(series, out var allEpisodes, out var allSeasons))
                return;

            _logger.LogInformation("[MR] Retrieved {Count} episodes and {SeasonCount} seasons using recursive query", allEpisodes.Count, allSeasons.Count);

            ProcessAllEpisodesFromSeriesCore(series, cfg, now, allEpisodes, allSeasons);
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
                DebugLogHelper.SafeAppend( logJson);
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

    private void ProcessAllEpisodesFromSeriesCore(Series series, PluginConfiguration cfg, DateTime now, List<Episode> allEpisodes, List<Season> allSeasons)
    {
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
                    DebugLogHelper.SafeAppend( logJson);
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
                                DebugLogHelper.SafeAppend( logJson);
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
                                DebugLogHelper.SafeAppend( logJson);
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
                                DebugLogHelper.SafeAppend( logJson);
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
                DebugLogHelper.SafeAppend( logJson);
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
                        DebugLogHelper.SafeAppend( logJson);
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
                DebugLogHelper.SafeAppend( logJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [EPISODES-FOUND] ERROR logging episodes found: {Error}", ex.Message);
                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "EPISODES-FOUND", location = "RenameCoordinator.cs:1135", message = "ERROR logging episodes found", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
                        _logger.LogWarning("[MR] [DEBUG] [ALL-SEASONS-PROCESSING] Processing episode from Season {SeasonNum}: {Name} (S{SeasonSxe}E{Episode}) (Season2Plus={IsSeason2Plus})", 
                            seasonNum, episodeName, seasonNum, episodeNum, isSeason2Plus);
                        
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
                                DebugLogHelper.SafeAppend( logJson);
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
                            DebugLogHelper.SafeAppend( logJson);
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
                    // When episode.Path does not exist (wrong drive, stale path), try series.Path + season folder for ANY season (including Season 1).
                    // This fixes episodes not being renamed when Jellyfin stores a different root (e.g. J:\ vs actual library path).
                    bool hasValidPath = !string.IsNullOrWhiteSpace(episode.Path) && File.Exists(episode.Path);
                    if (!hasValidPath && !string.IsNullOrWhiteSpace(series.Path))
                    {
                        // Try derived path for any season (Season 1, 2, ...). Use seasonNumber from metadata or 1 if unknown.
                        int seasonForPath = (seasonNumber >= 1) ? seasonNumber : 1;
                        var seasonFolderName = SafeName.RenderSeasonFolder(cfg.SeasonFolderFormat, seasonForPath, null);
                        var potentialSeasonPath = Path.Combine(series.Path, seasonFolderName);
                        if (Directory.Exists(potentialSeasonPath))
                        {
                            // First try: same filename as in episode.Path (e.g. from metadata)
                            var episodeFileNameFromPath = Path.GetFileName(episode.Path ?? episodeName + ".mp4");
                            var potentialEpisodePath = Path.Combine(potentialSeasonPath, episodeFileNameFromPath);
                            if (File.Exists(potentialEpisodePath))
                            {
                                _logger.LogInformation("[MR] [PATH-RESOLVE] Episode file not at stored path; found via series path. Season={Season}, Resolved={Path}", seasonForPath, potentialEpisodePath);
                                episode.Path = potentialEpisodePath;
                                episodePath = potentialEpisodePath;
                                hasValidPath = true;
                                // #region agent log
                                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "PATH-RESOLVE-FILENAME", location = "RenameCoordinator.cs:PathResolve", message = "Resolved episode path via series path + season folder (filename match)", data = new { seriesName = series.Name, seasonNumber = seasonForPath, resolvedPath = potentialEpisodePath, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                                // #endregion
                            }
                            if (!hasValidPath && (episode.IndexNumber.HasValue || SafeName.ParseEpisodeNumberFromFileName(Path.GetFileNameWithoutExtension(episodePath ?? string.Empty)).HasValue))
                            {
                                // Second try: scan season folder for a file matching episode number (handles "Season 1 EP 2.mp4" vs metadata filename)
                                var videoExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" };
                                var targetEp = episode.IndexNumber ?? SafeName.ParseEpisodeNumberFromFileName(Path.GetFileNameWithoutExtension(episodePath ?? string.Empty));
                                foreach (var f in System.IO.Directory.EnumerateFiles(potentialSeasonPath))
                                {
                                    if (!videoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
                                    var epFromFile = SafeName.ParseEpisodeNumberFromFileName(Path.GetFileNameWithoutExtension(f));
                                    if (epFromFile == targetEp)
                                    {
                                        _logger.LogInformation("[MR] [PATH-RESOLVE] Episode file not at stored path; matched by episode number in season folder. Season={Season}, File={File}", seasonForPath, f);
                                        episode.Path = f;
                                        episodePath = f;
                                        hasValidPath = true;
                                        // #region agent log
                                        DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "PATH-RESOLVE-SCAN", location = "RenameCoordinator.cs:PathResolveScan", message = "Resolved episode path via series path + episode number scan", data = new { seriesName = series.Name, seasonNumber = seasonForPath, resolvedPath = f, episodeId = episode.Id.ToString(), episodeNumber = targetEp }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                                        // #endregion
                                        break;
                                    }
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

                    // When Jellyfin has no IndexNumber (e.g. "Episode=NULL"), derive from filename so we still process
                    if (!hasValidMetadata && !string.IsNullOrWhiteSpace(episodePath))
                    {
                        var fileNameNoExt = Path.GetFileNameWithoutExtension(episodePath);
                        var parsedEpisode = SafeName.ParseEpisodeNumberFromFileName(fileNameNoExt ?? string.Empty);
                        if (parsedEpisode.HasValue)
                        {
                            hasValidMetadata = true;
                            episodeNum = parsedEpisode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            _logger.LogInformation("[MR] [EPISODE-FROM-FILENAME] Episode has no IndexNumber in metadata; derived episode {Episode} from filename. EpisodeId={Id}, Path={Path}",
                                episodeNum, episode.Id, episodePath);
                        }
                    }
                    
                    // When metadata has no season number, derive from path (any season folder)
                    if (!hasSeasonNumber && !string.IsNullOrWhiteSpace(episodePath))
                    {
                        var seasonFromPath = TryGetSeasonNumberFromFolderPath(episodePath);
                        if (seasonFromPath.HasValue)
                        {
                            hasSeasonNumber = true;
                            seasonNum = seasonFromPath.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            seasonNumber = seasonFromPath.Value;
                            isSeason2Plus = seasonNumber >= 2;
                            _logger.LogInformation("[MR] [SEASON-FROM-PATH] Episode has no ParentIndexNumber in metadata; derived season {Season} from path. EpisodeId={Id}, Path={Path}",
                                seasonNum, episode.Id, episodePath);
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
                        DebugLogHelper.SafeAppend( logJson);
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
                                DebugLogHelper.SafeAppend( logJson);
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
                            DebugLogHelper.SafeAppend( logJson);
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
                                    DebugLogHelper.SafeAppend( errorLogJson);
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
                            DebugLogHelper.SafeAppend( logJson);
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
                DebugLogHelper.SafeAppend( logJson);
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
                DebugLogHelper.SafeAppend( logJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [PROCESS-ALL-EPISODES-COMPLETE] ERROR logging completion: {Error}", ex.Message);
            }
            // #endregion

            // Process retry queue after processing all episodes
            ProcessRetryQueue(cfg, now);

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
                DebugLogHelper.SafeAppend( logJson);
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

        // Safeguard: ensure desired movie folder name is valid before calling rename service
        if (!SafeName.IsValidFolderOrFileName(desiredFolderName))
        {
            _logger.LogWarning("[MR] [SAFEGUARD] Movie folder rename skipped: desired folder name is invalid. Movie: {Name}, Id: {Id}", movie.Name ?? "NULL", movie.Id);
        }
        else
        {
            _pathRenamer.TryRenameMovieFolder(movie, desiredFolderName, cfg.DryRun);
        }

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
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "NESTED-SEASON", location = "RenameCoordinator.cs:900", message = "FixNestedSeasonFolders called", data = new { seriesPath = seriesPath ?? "NULL", pathExists = !string.IsNullOrWhiteSpace(seriesPath) && Directory.Exists(seriesPath) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
            
            // PHASE 1: Flatten nested season folders INSIDE standard season folders
            // e.g., "Season 01" containing "Season 01" and "Season 01 - Season 1" -> move episodes up, remove nested
            var videoExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" };
            foreach (var seasonFolder in seasonFolders.ToList())
            {
                var seasonFolderName = Path.GetFileName(seasonFolder);
                var innerSubdirs = Directory.GetDirectories(seasonFolder);
                foreach (var innerDir in innerSubdirs)
                {
                    var innerName = Path.GetFileName(innerDir);
                    if (!containsSeasonPattern.IsMatch(innerName))
                    {
                        continue;
                    }
                    var innerFiles = Directory.GetFiles(innerDir);
                    var hasVideoFiles = innerFiles.Any(f => videoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
                    if (!hasVideoFiles && Directory.GetDirectories(innerDir).Length == 0)
                    {
                        continue;
                    }
                    _logger.LogInformation("[MR] Found nested season folder '{Nested}' inside '{Parent}'. Moving {Count} file(s) up...",
                        innerName, seasonFolderName, innerFiles.Length);
                    foreach (var file in innerFiles)
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
                            _logger.LogInformation("[MR] ✓ Moved from nested: {FileName}", fileName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[MR] ERROR moving file from nested folder: {FileName}", fileName);
                        }
                    }
                    try
                    {
                        if (Directory.GetFiles(innerDir).Length == 0 && Directory.GetDirectories(innerDir).Length == 0)
                        {
                            Directory.Delete(innerDir);
                            _logger.LogInformation("[MR] ✓ Removed empty nested folder: {Path}", innerDir);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[MR] Could not remove nested folder: {Path}", innerDir);
                    }
                }
                // Also remove empty nested season-named folders (e.g., empty "Season 01" inside "Season 01")
                foreach (var innerDir in Directory.GetDirectories(seasonFolder).ToList())
                {
                    var innerName = Path.GetFileName(innerDir);
                    if (containsSeasonPattern.IsMatch(innerName) &&
                        Directory.GetFiles(innerDir).Length == 0 &&
                        Directory.GetDirectories(innerDir).Length == 0)
                    {
                        try
                        {
                            Directory.Delete(innerDir);
                            _logger.LogInformation("[MR] ✓ Removed empty nested folder: {Path}", innerDir);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[MR] Could not remove empty nested folder: {Path}", innerDir);
                        }
                    }
                }
            }
            
            // PHASE 2: Rename non-standard season folders (e.g., "Season 01 - Season 1" -> "Season 01")
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
                    DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "NESTED-SEASON", location = "RenameCoordinator.cs:956", message = "Checking for nested season folder", data = new { seasonFolderName = seasonFolderName, desiredSeason1Name = desiredSeason1Name, nestedPath = nestedSeason01Path, nestedExists = Directory.Exists(nestedSeason01Path) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
                // Path may be stale after series folder rename - try to derive correct path
                _logger.LogWarning("[MR] [DEBUG] [SEASON-PATH-STALE] Season path from metadata does not exist (may be stale after series folder rename). Attempting to derive correct path...");
                _logger.LogWarning("[MR] [DEBUG] [SEASON-PATH-STALE] Stale path: {StalePath}", path);
                
                // Extract season folder name from stale path
                var staleSeasonFolderName = Path.GetFileName(path);
                _logger.LogInformation("[MR] [DEBUG] [SEASON-PATH-STALE] Extracted season folder name from stale path: {FolderName}", staleSeasonFolderName);
                
                // Try to get current series path from file system (more reliable than metadata which may be stale)
                // First try metadata path, but if it doesn't exist, derive from stale path's parent directory
                var seriesPathFromMetadata = season.Series?.Path;
                var seriesPath = seriesPathFromMetadata;
                
                // If metadata series path doesn't exist, try to derive it from the stale season path
                if (string.IsNullOrWhiteSpace(seriesPath) || !Directory.Exists(seriesPath))
                {
                    _logger.LogWarning("[MR] [DEBUG] [SEASON-PATH-STALE] Series path from metadata is also stale or null. Attempting to derive from stale season path...");
                    _logger.LogWarning("[MR] [DEBUG] [SEASON-PATH-STALE] Metadata series path: {MetadataPath}", seriesPathFromMetadata ?? "NULL");
                    
                    // Extract parent directory from stale season path (this is the OLD series path)
                    var staleSeriesPath = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(staleSeriesPath))
                    {
                        _logger.LogInformation("[MR] [DEBUG] [SEASON-PATH-STALE] Stale series path from season path: {StaleSeriesPath}", staleSeriesPath);
                        
                        // Get the parent directory of the stale series path (the library root)
                        var libraryRoot = Path.GetDirectoryName(staleSeriesPath);
                        if (!string.IsNullOrWhiteSpace(libraryRoot))
                        {
                            _logger.LogInformation("[MR] [DEBUG] [SEASON-PATH-STALE] Library root: {LibraryRoot}", libraryRoot);
                            
                            // Search for the series folder in the library root
                            // Strategy: First try to match by provider ID (most reliable), then fall back to name-based search
                            try
                            {
                                var series = season.Series;
                                if (series != null)
                                {
                                    // Strategy 1: Try to match by provider ID in folder name (most reliable)
                                    // Look for folders containing provider ID patterns like [tmdb-12345], [tvdb-12345], etc.
                                    if (series.ProviderIds != null && series.ProviderIds.Count > 0)
                                    {
                                        var allFolders = Directory.GetDirectories(libraryRoot, "*", SearchOption.TopDirectoryOnly);
                                        foreach (var providerId in series.ProviderIds)
                                        {
                                            var providerLabel = providerId.Key.ToLowerInvariant();
                                            var providerValue = providerId.Value;
                                            
                                            // Search for folders containing [providerLabel-providerValue] pattern
                                            var searchPattern = $"[{providerLabel}-{providerValue}]";
                                            var matchingFolders = allFolders.Where(folder => 
                                                Path.GetFileName(folder).Contains(searchPattern, StringComparison.OrdinalIgnoreCase)).ToList();
                                            
                                            if (matchingFolders.Count == 1)
                                            {
                                                // Perfect match - exactly one folder with this provider ID
                                                seriesPath = matchingFolders[0];
                                                _logger.LogWarning("[MR] [DEBUG] [SEASON-PATH-STALE] Found series folder by provider ID match: {FoundPath} (Provider: {Provider}={Value})", 
                                                    seriesPath, providerLabel, providerValue);
                                                break;
                                            }
                                            else if (matchingFolders.Count > 1)
                                            {
                                                // Multiple matches - try to narrow down by series name
                                                var seriesName = series.Name;
                                                if (!string.IsNullOrWhiteSpace(seriesName))
                                                {
                                                    var nameMatches = matchingFolders.Where(folder => 
                                                        Path.GetFileName(folder).StartsWith(seriesName, StringComparison.OrdinalIgnoreCase)).ToList();
                                                    if (nameMatches.Count == 1)
                                                    {
                                                        seriesPath = nameMatches[0];
                                                        _logger.LogWarning("[MR] [DEBUG] [SEASON-PATH-STALE] Found series folder by provider ID + name match: {FoundPath} (Provider: {Provider}={Value})", 
                                                            seriesPath, providerLabel, providerValue);
                                                        break;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    
                                    // Strategy 2: Fall back to name-based search if provider ID search didn't find a unique match
                                    if (string.IsNullOrWhiteSpace(seriesPath) || !Directory.Exists(seriesPath))
                                    {
                                        var seriesName = series.Name;
                                        if (!string.IsNullOrWhiteSpace(seriesName))
                                        {
                                            // Escape special characters in series name for search pattern
                                            // Note: Directory.GetDirectories uses simple wildcards (* and ?), not regex
                                            // So we need to escape special characters that might interfere
                                            var escapedName = seriesName.Replace("[", "[[]").Replace("]", "[]]");
                                            
                                            try
                                            {
                                                // Try exact name match first (case-insensitive)
                                                var allFolders = Directory.GetDirectories(libraryRoot, "*", SearchOption.TopDirectoryOnly);
                                                var exactMatches = allFolders.Where(folder => 
                                                    string.Equals(Path.GetFileName(folder), seriesName, StringComparison.OrdinalIgnoreCase)).ToList();
                                                
                                                if (exactMatches.Count == 1)
                                                {
                                                    seriesPath = exactMatches[0];
                                                    _logger.LogWarning("[MR] [DEBUG] [SEASON-PATH-STALE] Found series folder by exact name match: {FoundPath}", seriesPath);
                                                }
                                                else
                                                {
                                                    // Fall back to prefix match
                                                    var prefixMatches = allFolders.Where(folder => 
                                                        Path.GetFileName(folder).StartsWith(seriesName, StringComparison.OrdinalIgnoreCase)).ToList();
                                                    
                                                    if (prefixMatches.Count == 1)
                                                    {
                                                        seriesPath = prefixMatches[0];
                                                        _logger.LogWarning("[MR] [DEBUG] [SEASON-PATH-STALE] Found series folder by name prefix match: {FoundPath}", seriesPath);
                                                    }
                                                    else if (prefixMatches.Count > 1)
                                                    {
                                                        // Multiple matches - log warning but use first match
                                                        _logger.LogWarning("[MR] [DEBUG] [SEASON-PATH-STALE] Multiple series folders found with name prefix '{SeriesName}'. Using first match: {FoundPath} (Total matches: {Count})", 
                                                            seriesName, prefixMatches[0], prefixMatches.Count);
                                                        seriesPath = prefixMatches[0];
                                                    }
                                                }
                                            }
                                            catch (Exception nameSearchEx)
                                            {
                                                _logger.LogWarning(nameSearchEx, "[MR] [DEBUG] [SEASON-PATH-STALE] Error in name-based search: {Error}", nameSearchEx.Message);
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception searchEx)
                            {
                                _logger.LogWarning(searchEx, "[MR] [DEBUG] [SEASON-PATH-STALE] Error searching for series folder: {Error}", searchEx.Message);
                            }
                        }
                    }
                }
                
                if (string.IsNullOrWhiteSpace(seriesPath))
                {
                    _logger.LogWarning("[MR] [DEBUG] [SEASON-SKIP-PATH-NOT-EXISTS] SKIP: Season path does not exist and cannot derive from series (series path is null or not found). Path={Path}, SeasonId={Id}, Name={Name}, SeasonNumber={SeasonNumber}", 
                        path, season.Id, season.Name ?? "NULL", season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                    return;
                }
                
                // Verify the series path exists
                if (string.IsNullOrWhiteSpace(seriesPath) || !Directory.Exists(seriesPath))
                {
                    _logger.LogWarning("[MR] [DEBUG] [SEASON-SKIP-PATH-NOT-EXISTS] SKIP: Derived series path does not exist or is null. SeriesPath={SeriesPath}, SeasonId={Id}, Name={Name}, SeasonNumber={SeasonNumber}", 
                        seriesPath ?? "NULL", season.Id, season.Name ?? "NULL", season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                    return;
                }
                
                // Validate that season folder name was extracted successfully
                if (string.IsNullOrWhiteSpace(staleSeasonFolderName))
                {
                    _logger.LogWarning("[MR] [DEBUG] [SEASON-SKIP-PATH-NOT-EXISTS] SKIP: Could not extract season folder name from stale path. StalePath={StalePath}, SeasonId={Id}, Name={Name}, SeasonNumber={SeasonNumber}", 
                        path, season.Id, season.Name ?? "NULL", season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                    return;
                }
                
                // Construct potential path using current series path + season folder name
                var potentialSeasonPath = Path.Combine(seriesPath, staleSeasonFolderName);
                _logger.LogInformation("[MR] [DEBUG] [SEASON-PATH-STALE] Derived potential path: {PotentialPath}", potentialSeasonPath);
                
                if (Directory.Exists(potentialSeasonPath))
                {
                    _logger.LogWarning("[MR] [DEBUG] [SEASON-PATH-FIX] Found season folder using derived path: {Path}", potentialSeasonPath);
                    path = potentialSeasonPath; // Use the derived path
                }
                else
                {
                    // Final fallback: Try case-insensitive search for season folder within the series path
                    // This handles cases where the season folder name might have different casing
                    try
                    {
                        var seriesSubdirectories = Directory.GetDirectories(seriesPath, "*", SearchOption.TopDirectoryOnly);
                        var caseInsensitiveMatch = seriesSubdirectories.FirstOrDefault(dir => 
                            string.Equals(Path.GetFileName(dir), staleSeasonFolderName, StringComparison.OrdinalIgnoreCase));
                        
                        if (caseInsensitiveMatch != null && Directory.Exists(caseInsensitiveMatch))
                        {
                            _logger.LogWarning("[MR] [DEBUG] [SEASON-PATH-FIX] Found season folder using case-insensitive match: {Path} (Original: {Original})", 
                                caseInsensitiveMatch, staleSeasonFolderName);
                            path = caseInsensitiveMatch;
                        }
                        else
                        {
                            _logger.LogWarning("[MR] [DEBUG] [SEASON-SKIP-PATH-NOT-EXISTS] SKIP: Season path does not exist on disk (tried metadata path, derived path, and case-insensitive search). MetadataPath={MetadataPath}, DerivedPath={DerivedPath}, SeasonId={Id}, Name={Name}, SeasonNumber={SeasonNumber}", 
                                season.Path ?? "NULL", potentialSeasonPath, season.Id, season.Name ?? "NULL", season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                            return;
                        }
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogWarning(fallbackEx, "[MR] [DEBUG] [SEASON-SKIP-PATH-NOT-EXISTS] SKIP: Error in case-insensitive fallback search. MetadataPath={MetadataPath}, DerivedPath={DerivedPath}, SeasonId={Id}, Name={Name}, SeasonNumber={SeasonNumber}", 
                            season.Path ?? "NULL", potentialSeasonPath, season.Id, season.Name ?? "NULL", season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                        return;
                    }
                }
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
            
            // Log season name status
            // IMPORTANT: Even if metadata only displays the season name (e.g., "East Blue"), 
            // Jellyfin's IndexNumber always contains the season number (e.g., 1, 2, 3).
            // We use IndexNumber for the season number and Name for the season name.
            if (!string.IsNullOrWhiteSpace(seasonName))
            {
                _logger.LogInformation("[MR] [SEASON-NAME] Season {SeasonNum} has name: '{SeasonNameVal}' (will render as 'Season {RenderSeason:00} - {RenderName}')", 
                    seasonNumber.Value, seasonName, seasonNumber.Value, seasonName);
            }
            else
            {
                _logger.LogInformation("[MR] [SEASON-NAME] Season {SeasonNum} has no name (will render as 'Season {RenderSeason:00}' only)", 
                    seasonNumber.Value, seasonNumber.Value);
            }
            
            _logger.LogInformation("[MR] [DEBUG] [SEASON-METADATA-VALIDATION] Season metadata validated: SeasonNumber={SeasonNumber}, SeasonName={SeasonName}", 
                seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), seasonName ?? "NULL");

            // Build desired folder name using METADATA VALUES ONLY
            _logger.LogInformation("[MR] [SEASON-FOLDER-RENDER] Rendering season folder name: Format='{Format}', Season={Season}, SeasonName='{SeasonName}'", 
                cfg.SeasonFolderFormat, 
                seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                seasonName ?? "NULL");
            
            var desiredFolderName = SafeName.RenderSeasonFolder(
                cfg.SeasonFolderFormat,
                seasonNumber,
                seasonName);
            
            _logger.LogInformation("[MR] [SEASON-FOLDER-RENDER] Rendered season folder name: '{FolderName}'", desiredFolderName);

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
                DebugLogHelper.SafeAppend( logJson);
            }
            catch (Exception logEx)
            {
                _logger.LogError(logEx, "[MR] [DEBUG] [SEASON-FOLDER-RENAME-START] ERROR logging rename start: {Error}", logEx.Message);
            }
            // #endregion
            
            // Safeguard: ensure desired season folder name is valid before calling rename service
            if (!SafeName.IsValidFolderOrFileName(desiredFolderName))
            {
                _logger.LogWarning("[MR] [SAFEGUARD] Season folder rename skipped: desired folder name is invalid. Season: {Name}, Id: {Id}, SeasonNumber: {SeasonNumber}",
                    season.Name ?? "NULL", season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            }
            else
            {
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
                    DebugLogHelper.SafeAppend( logJson);
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
                    DebugLogHelper.SafeAppend( logJson);
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, "[MR] [DEBUG] [SEASON-FOLDER-RENAME-ERROR] ERROR logging rename error: {Error}", logEx.Message);
                }
                // #endregion
                
                _logger.LogError(seasonRenameEx, "[MR] ERROR renaming season folder: {Message}", seasonRenameEx.Message);
            }
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
                DebugLogHelper.SafeAppend( logJson);
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
            // Safeguard: reject null episode or config
            if (episode == null)
            {
                _logger.LogWarning("[MR] [SAFEGUARD] HandleEpisodeUpdate: episode is null. Aborting.");
                return;
            }
            if (cfg == null)
            {
                _logger.LogWarning("[MR] [SAFEGUARD] HandleEpisodeUpdate: PluginConfiguration is null. Aborting.");
                return;
            }

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
                DebugLogHelper.SafeAppend( logJson);
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
                DebugLogHelper.SafeAppend( logJson);
                _logger.LogInformation("[MR] [DEBUG-HYP-A] IndexNumber accessed in HandleEpisodeUpdate: IndexNumber={IndexNumber}, ParentIndexNumber={ParentIndexNumber}, EpisodeId={Id}", 
                    indexNumberValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", 
                    parentIndexNumberValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episode.Id);
            }
            catch (Exception ex)
            {
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "RenameCoordinator.cs:445", message = "ERROR accessing IndexNumber", data = new { error = ex.Message, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                DebugLogHelper.SafeAppend( logJson);
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
                DebugLogHelper.SafeAppend( logJson);
                _logger.LogInformation("[MR] [DEBUG-HYP-B] Episode type: {Type}, Relevant properties: {Properties}", episodeType.FullName, string.Join(", ", relevantProps.Select(kvp => $"{kvp.Key}={kvp.Value}")));
            }
            catch (Exception ex)
            {
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "B", location = "RenameCoordinator.cs:465", message = "ERROR in reflection", data = new { error = ex.Message, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                DebugLogHelper.SafeAppend( logJson);
                _logger.LogError(ex, "[MR] [DEBUG-HYP-B] ERROR in reflection: {Error}", ex.Message);
            }
            // #endregion

            // Per-item cooldown (skip during bulk processing so all episodes in one run get processed)
            if (!isBulkProcessing && _lastAttemptUtcByItem.TryGetValue(episode.Id, out var lastTry))
            {
                var timeSinceLastTry = (now - lastTry).TotalSeconds;
                if (timeSinceLastTry < cfg.PerItemCooldownSeconds)
                {
                    // #region agent log - MULTI-EPISODE-HYP-C: Track episodes skipped due to cooldown
                    DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-C", location = "RenameCoordinator.cs:525", message = "Episode skipped - cooldown active", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNum, episodeNumber = episodeNum, timeSinceLastTry = timeSinceLastTry.ToString(System.Globalization.CultureInfo.InvariantCulture), cooldownSeconds = cfg.PerItemCooldownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-D", location = "RenameCoordinator.cs:539", message = "Episode skipped - no path", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNum, episodeNumber = episodeNum }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                // #endregion
                _logger.LogWarning("[MR] [DEBUG] [EPISODE-SKIP-NO-PATH] SKIP: Episode has no path. EpisodeId={Id}, Name={Name}, Season={Season}, Episode={Episode}", episode.Id, episode.Name, seasonNum, episodeNum);
                _logger.LogInformation("[MR] ===== Episode Processing Complete (Skipped - No Path) =====");
                return;
            }

            if (!File.Exists(path))
            {
                // #region agent log - MULTI-EPISODE-HYP-E: Track episodes skipped due to file not existing
                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-E", location = "RenameCoordinator.cs:546", message = "Episode skipped - file does not exist", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNum, episodeNumber = episodeNum, path = path }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "NESTED-SEASON", location = "RenameCoordinator.cs:1293", message = "Calling FixNestedSeasonFolders from HandleEpisodeUpdate", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seriesPath = seriesPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                // #endregion
                FixNestedSeasonFolders(seriesPath, cfg);
            }

            // Skip when episode is already in the correct season folder (per metadata) and already correctly named. Must match metadata.
            if (episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue && !string.IsNullOrWhiteSpace(seriesPath) && Directory.Exists(seriesPath))
            {
                var episodeTitleForSkip = ExtractCleanEpisodeTitle(episode);
                var seasonFolderNameForSkip = SafeName.RenderSeasonFolder(cfg.SeasonFolderFormat, episode.ParentIndexNumber.Value, null);
                var correctSeasonFolderForSkip = Path.Combine(seriesPath, seasonFolderNameForSkip);
                var currentEpisodeDir = Path.GetDirectoryName(path);
                var normCurrent = currentEpisodeDir?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normCorrect = correctSeasonFolderForSkip.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normCurrent, normCorrect, StringComparison.OrdinalIgnoreCase))
                {
                    int? yearForSkip = episode.Series?.ProductionYear ?? (episode.Series?.PremiereDate.HasValue == true ? episode.Series.PremiereDate.Value.Year : (int?)null);
                    var desiredFileNameForSkip = SafeName.RenderEpisodeFileName(cfg.EpisodeFileFormat, episode.SeriesName?.Trim() ?? "", episode.ParentIndexNumber.Value, episode.IndexNumber.Value, episodeTitleForSkip ?? "", yearForSkip);
                    var currentFileNameForSkip = Path.GetFileNameWithoutExtension(path);
                    if (SafeName.DoFilenamesMatch(currentFileNameForSkip, desiredFileNameForSkip))
                    {
                        _logger.LogInformation("[MR] [SKIP] Episode already in correct season folder and correctly named (matches metadata). Skipping. Path={Path}, Season={Season}, Episode={Episode}", path, episode.ParentIndexNumber.Value, episode.IndexNumber.Value);
                        return;
                    }
                }
            }

            // Check if episode is directly in series folder (no season folder)
            // CRITICAL: We must verify that episodeDirectory is NOT a season folder before checking if it equals seriesPath
            // This prevents creating Season 1 folders inside other season folders
            var isInSeriesRoot = false;
            if (!string.IsNullOrWhiteSpace(seriesPath) && !string.IsNullOrWhiteSpace(episodeDirectory))
            {
                var normalizedEpisodeDir = episodeDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedSeriesPath = seriesPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                
                // First check: Do the paths match?
                var pathsMatch = string.Equals(normalizedEpisodeDir, normalizedSeriesPath, StringComparison.OrdinalIgnoreCase);
                
                if (pathsMatch)
                {
                    // Paths match, but we need to verify episodeDirectory is NOT a season folder
                    // (This prevents the bug where seriesPath incorrectly points to a season folder)
                    var episodeDirName = Path.GetFileName(normalizedEpisodeDir);
                    if (!string.IsNullOrWhiteSpace(episodeDirName))
                    {
                        // Check if the directory name matches season folder patterns
                        var seasonPattern = new System.Text.RegularExpressions.Regex(
                            @"^(Season\s*\d+|S\d+|Season\s*\d{2,})$",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        
                        var isSeasonFolder = seasonPattern.IsMatch(episodeDirName);
                        
                        if (isSeasonFolder)
                        {
                            // episodeDirectory is a season folder, so episode is NOT in series root
                            // This means seriesPath was incorrectly derived (pointing to season folder instead of series folder)
                            _logger.LogWarning("[MR] [DEBUG] [SEASON-FOLDER-BUG-PREVENTION] Detected episode in season folder '{SeasonFolder}' but seriesPath incorrectly points to same folder. Skipping Season 1 folder creation to prevent nested structure.", episodeDirName);
                            isInSeriesRoot = false;
                        }
                        else
                        {
                            // episodeDirectory is NOT a season folder and paths match, so episode is in series root
                            isInSeriesRoot = true;
                        }
                    }
                    else
                    {
                        // Can't determine directory name, assume paths match means series root
                        isInSeriesRoot = true;
                    }
                }
                else
                {
                    // Paths don't match, episode is definitely in a season folder (or somewhere else)
                    isInSeriesRoot = false;
                }
            }
            
            // #region agent log - MULTI-EPISODE-HYP-F: Track isInSeriesRoot detection for each episode
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-F", location = "RenameCoordinator.cs:557", message = "isInSeriesRoot check", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", episodePath = path, episodeDirectory = episodeDirectory ?? "NULL", seriesPath = seriesPath ?? "NULL", isInSeriesRoot = isInSeriesRoot }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            // #endregion
            
            // Remember if we were in series root BEFORE moving (capture before isInSeriesRoot is modified)
            var wasInSeriesRootBeforeMove = isInSeriesRoot;
            
            // CRITICAL FIX: Never create Season 1 folder for episodes that are in Season 2+ according to metadata
            // This prevents creating nested "Season 01" folders inside other season folders (e.g., Season 21/Season 01)
            // Note: isSeason2Plus is already defined earlier in the method (line 4040)
            
            if (isInSeriesRoot && isSeason2Plus)
            {
                _logger.LogWarning("[MR] [DEBUG] [SEASON-FOLDER-BUG-PREVENTION] SKIP: Episode is in series root but has Season {Season} metadata. Skipping Season 1 folder creation to prevent nested structure. Episode: {Name}, Path: {Path}", 
                    episode.ParentIndexNumber ?? -1, episode.Name ?? "NULL", path);
            }
            
            if (isInSeriesRoot && !isSeason2Plus)
            {
                _logger.LogInformation("[MR] Episode is directly in series folder (no season folder structure)");
                
                // Get Season 1 name from metadata if available
                string? season1NameFromMetadata = null;
                if (episode.Series != null)
                {
                    try
                    {
                        // Get all seasons for the series
                        var seasons = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                        {
                            ParentId = episode.Series.Id,
                            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Season },
                            Recursive = false
                        }).Cast<MediaBrowser.Controller.Entities.TV.Season>().ToList();
                        
                        // Find Season 1
                        var season1 = seasons.FirstOrDefault(s => s.IndexNumber == 1);
                        if (season1 != null)
                        {
                            season1NameFromMetadata = season1.Name?.Trim();
                            if (!string.IsNullOrWhiteSpace(season1NameFromMetadata))
                            {
                                _logger.LogInformation("[MR] [SEASON-NAME] Found Season 1 name from metadata: '{SeasonName}'", season1NameFromMetadata);
                            }
                            else
                            {
                                _logger.LogInformation("[MR] [SEASON-NAME] Season 1 exists in metadata but has no name (will use season number only)");
                            }
                        }
                        else
                        {
                            _logger.LogWarning("[MR] [SEASON-NAME] Season 1 not found in metadata (will use season number only)");
                        }
                    }
                    catch (Exception seasonEx)
                    {
                        _logger.LogWarning(seasonEx, "[MR] [SEASON-NAME] Could not retrieve Season 1 name from metadata: {Error}", seasonEx.Message);
                    }
                }
                
                // Create "Season 1" folder and move episode into it
                // This ensures Jellyfin shows "Season 1" instead of "Season Unknown"
                _logger.LogInformation("[MR] === Creating Season 1 Folder for Flat Structure ===");
                _logger.LogInformation("[MR] Season Folder Format: {Format}", cfg.SeasonFolderFormat ?? "Season {Season:00} - {SeasonName}");
                _logger.LogInformation("[MR] Season Number for folder: 1");
                
                _logger.LogInformation("[MR] [SEASON-FOLDER-RENDER] Rendering Season 1 folder name: Format='{Format}', Season=1, SeasonName='{SeasonName}'", 
                    cfg.SeasonFolderFormat, 
                    season1NameFromMetadata ?? "NULL");
                
                var season1FolderName = SafeName.RenderSeasonFolder(cfg.SeasonFolderFormat, 1, season1NameFromMetadata);
                
                _logger.LogInformation("[MR] [SEASON-FOLDER-RENDER] Rendered Season 1 folder name: '{FolderName}'", season1FolderName);
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
                        DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-G", location = "RenameCoordinator.cs:612", message = "Episode moved to Season 1 folder", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", fromPath = path, toPath = newEpisodePath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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
            // - ALWAYS use metadata season number if available (most reliable)
            // - Only use Season 1 if episode was actually in series root AND metadata doesn't indicate Season 2+
            // - Default to 1 if season number is null
            int? seasonNumber = episode.ParentIndexNumber;
            int? episodeNumberWithinSeasonFromMapping = null; // set when folder-count-mismatch maps absolute ep -> (season, ep within season) for filename
            
            // CRITICAL FIX: Never override metadata season number for Season 2+ episodes
            // This prevents generating incorrect filenames like "S01E575" when episode is actually Season 10
            if (wasInSeriesRootBeforeMove && !isSeason2Plus)
            {
                // Episode was in series root (flat structure) and we moved it to Season 1
                // Only use Season 1 if metadata doesn't indicate Season 2+
                seasonNumber = 1;
                _logger.LogInformation("[MR] Episode was in series root (flat structure). Using Season 1 for renaming after moving to Season 1 folder.");
            }
            else if (wasInSeriesRootBeforeMove && isSeason2Plus)
            {
                // Episode was incorrectly detected as being in series root, but metadata says Season 2+
                // Use metadata season number (don't override with Season 1)
                _logger.LogWarning("[MR] [DEBUG] [SEASON-NUMBER-FIX] Episode was incorrectly detected as in series root, but metadata indicates Season {Season}. Using metadata season number instead of Season 1.", 
                    seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                // seasonNumber already set from episode.ParentIndexNumber above - don't override
            }
            else if (seasonNumber == null)
            {
                // Episode is in a season folder but metadata doesn't have season number - try path first, then fallback to 1
                var seasonFromPathHere = TryGetSeasonNumberFromFolderPath(path);
                if (seasonFromPathHere.HasValue)
                {
                    seasonNumber = seasonFromPathHere.Value;
                    _logger.LogInformation("[MR] [SEASON-FROM-PATH] Episode metadata season is NULL; derived Season {Season} from path.", seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    seasonNumber = 1;
                    _logger.LogInformation("[MR] Episode is in a season folder but metadata season number is NULL. Using Season 1 as fallback.");
                }
            }
            else
            {
                // Episode is already in a season folder - use the actual season number from metadata
                _logger.LogInformation("[MR] Episode is already in a season folder. Using season number from metadata: Season {Season}", 
                    seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // Universal: compare folder episode count to metadata episode count for this season.
            // If metadata says Season 1 = episodes 1-36 and folder has 36 files, count matches — use metadata as-is.
            // Compare directory to metadata: correct amount of episodes per season comes from metadata (not a fixed number).
            // Redistribute when folder has more files than metadata says for this season, or when folder is suspiciously overstuffed (wrong library structure).
            const int EpisodesPerSeasonHeuristic = 50; // fallback when metadata season lookup fails
            const int OverstuffedFolderThreshold = 100; // folder with more than this in one season may have wrong layout (metadata might also be wrong)
            const int MaxReasonablePerSeasonForHeuristic = 200; // only use heuristic split when metadata has a season with 200+ episodes (clearly wrong "all in one")
            var episodeDirectoryPath = Path.GetDirectoryName(path);
            var currentFolderSeason = TryGetSeasonNumberFromFolderPath(path);
            var episodeNumberForMapping = episode.IndexNumber ?? SafeName.ParseEpisodeNumberFromFileName(Path.GetFileNameWithoutExtension(path) ?? string.Empty);
            var isOverstuffedSeasonFolder = false; // used later to skip path/filename overrides when we've already fixed season from count mismatch
            if (!string.IsNullOrWhiteSpace(episodeDirectoryPath) && Directory.Exists(episodeDirectoryPath) && episode.Series != null && currentFolderSeason.HasValue)
            {
                var videoExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" };
                var folderFileCount = Directory.GetFiles(episodeDirectoryPath)
                    .Count(f => videoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
                var metadataEpisodeCountForThisSeason = GetMetadataEpisodeCountForSeason(episode.Series, currentFolderSeason.Value);
                _logger.LogInformation("[MR] [FOLDER-COUNT-DEBUG] Path={Path}, currentFolderSeason={FolderSeason}, folderFileCount={FolderCount}, metadataEpisodeCountForSeason={MetadataCount}, episodeNumberForMapping={EpNum}",
                    path, currentFolderSeason.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), folderFileCount.ToString(System.Globalization.CultureInfo.InvariantCulture), metadataEpisodeCountForThisSeason?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episodeNumberForMapping?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                var useMismatchLogic = false;
                if (metadataEpisodeCountForThisSeason.HasValue && folderFileCount > metadataEpisodeCountForThisSeason.Value)
                {
                    useMismatchLogic = true;
                    _logger.LogWarning("[MR] [FOLDER-COUNT-MISMATCH] Folder has {FolderCount} video files but metadata says Season {Season} has {MetadataCount} episodes. Using episode number to determine correct season. EpisodeId={EpisodeId}",
                        folderFileCount, currentFolderSeason.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), metadataEpisodeCountForThisSeason.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), episode.Id);
                }
                else if (folderFileCount > OverstuffedFolderThreshold && currentFolderSeason.Value == 1 && (seasonNumber == null || seasonNumber.Value == 1))
                {
                    // Folder has suspiciously many files in Season 01; metadata might also be wrong (e.g. all under S1). Redistribute using metadata counts or heuristic.
                    useMismatchLogic = true;
                    _logger.LogWarning("[MR] [FOLDER-OVERSTUFFED] Folder has {FolderCount} files (over {Threshold}) in Season 01. Redistributing to match metadata episode counts per season. EpisodeId={EpisodeId}",
                        folderFileCount, OverstuffedFolderThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture), episode.Id);
                }
                if (useMismatchLogic)
                {
                    isOverstuffedSeasonFolder = true;
                    // Prefer metadata for season/episode so we don't change episode numbers; only use position-based mapping when metadata is missing.
                    var hasMetadataSeasonAndEpisode = episode.IndexNumber.HasValue && episode.ParentIndexNumber.HasValue;
                    if (hasMetadataSeasonAndEpisode)
                    {
                        seasonNumber = episode.ParentIndexNumber;
                        episodeNumberWithinSeasonFromMapping = episode.IndexNumber;
                        _logger.LogInformation("[MR] [METADATA-PREFER] Using metadata for filename: Season {Season} Episode {Episode} (no episode number change). EpisodeId={EpisodeId}",
                            seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), episodeNumberWithinSeasonFromMapping?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?", episode.Id);
                    }
                    else
                    {
                        var cachedMapping = GetOrBuildCachedSeasonEpisodeForFile(episodeDirectoryPath, path, episode.Series, folderFileCount, MaxReasonablePerSeasonForHeuristic);
                        if (cachedMapping.HasValue)
                        {
                            seasonNumber = cachedMapping.Value.season;
                            episodeNumberWithinSeasonFromMapping = cachedMapping.Value.episodeWithinSeason;
                            _logger.LogInformation("[MR] [METADATA-MATCH] Stable mapping (metadata missing): folder {Folder} → Season {Season} Episode {EpWithin}. EpisodeId={EpisodeId}",
                                episodeDirectoryPath, seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), episodeNumberWithinSeasonFromMapping?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?", episode.Id);
                        }
                    }
                    var positionBasedAssigned = hasMetadataSeasonAndEpisode || (episodeNumberWithinSeasonFromMapping.HasValue && seasonNumber.HasValue);
                    if (!positionBasedAssigned)
                    {
                        // Fallback: use episode number from filename → metadata or heuristic
                        if (episodeNumberForMapping.HasValue && episodeNumberForMapping.Value > 0)
                        {
                            var fileNameForInfer = Path.GetFileNameWithoutExtension(path);
                            var seasonFromFileNameMismatch = SafeName.ParseMaxSeasonNumberFromFileName(fileNameForInfer ?? string.Empty);
                            if (seasonFromFileNameMismatch.HasValue && seasonFromFileNameMismatch.Value >= 2)
                            {
                                seasonNumber = seasonFromFileNameMismatch.Value;
                                _logger.LogInformation("[MR] [FOLDER-COUNT-MISMATCH] Using filename-derived season {Season}. FileName={FileName}", seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), fileNameForInfer ?? "NULL");
                            }
                            else
                            {
                                var (targetSeason, episodeWithinSeason) = TryGetSeasonAndEpisodeFromAbsoluteEpisodeNumber(episode.Series, episodeNumberForMapping.Value);
                                if (targetSeason.HasValue && targetSeason.Value >= 1)
                                {
                                    seasonNumber = targetSeason.Value;
                                    if (episodeWithinSeason.HasValue && episodeWithinSeason.Value >= 1)
                                        episodeNumberWithinSeasonFromMapping = episodeWithinSeason.Value;
                                    _logger.LogInformation("[MR] [FOLDER-COUNT-MISMATCH] Episode {Episode} maps to metadata Season {Season} Episode {EpWithin}. Moving to correct season folder.", episodeNumberForMapping.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), episodeNumberWithinSeasonFromMapping?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
                                }
                                else
                                {
                                    var inferredSeason = Math.Min(1 + (episodeNumberForMapping.Value - 1) / EpisodesPerSeasonHeuristic, 30);
                                    seasonNumber = inferredSeason;
                                    episodeNumberWithinSeasonFromMapping = 1 + (episodeNumberForMapping.Value - 1) % EpisodesPerSeasonHeuristic;
                                    _logger.LogWarning("[MR] [FOLDER-COUNT-MISMATCH] Metadata season lookup failed; using heuristic season {Season} from episode {Episode} ({EpsPerSeason} eps/season).", inferredSeason.ToString(System.Globalization.CultureInfo.InvariantCulture), episodeNumberForMapping.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), EpisodesPerSeasonHeuristic);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogWarning("[MR] [FOLDER-COUNT-MISMATCH] Could not get episode number or position; keeping season {Season}. EpisodeId={EpisodeId}, FileName={FileName}", seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episode.Id, Path.GetFileName(path) ?? "NULL");
                        }
                    }
                }
                else if (metadataEpisodeCountForThisSeason.HasValue && folderFileCount <= metadataEpisodeCountForThisSeason.Value)
                {
                    _logger.LogInformation("[MR] [FOLDER-COUNT-OK] Folder has {FolderCount} files, metadata Season {Season} has {MetadataCount} episodes. Count matches or is under; using metadata.", folderFileCount.ToString(System.Globalization.CultureInfo.InvariantCulture), currentFolderSeason.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), metadataEpisodeCountForThisSeason.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (!metadataEpisodeCountForThisSeason.HasValue)
                {
                    _logger.LogInformation("[MR] [FOLDER-COUNT-DEBUG] No metadata episode count for Season {Season}; skipping count-based redistribution.", currentFolderSeason.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            // When metadata has season number, never change it (user wants season/episode from metadata; only fix title and format).
            // Only use path/filename when metadata season is missing or clearly wrong (e.g. provider maps all to S1).
            var hasMetadataSeason = episode.ParentIndexNumber.HasValue;
            var seasonFromPath = TryGetSeasonNumberFromFolderPath(path);
            if (!hasMetadataSeason && !isOverstuffedSeasonFolder && seasonFromPath.HasValue && seasonFromPath.Value >= 2 &&
                (seasonNumber == null || seasonNumber.Value == 1))
            {
                _logger.LogWarning("[MR] [SEASON-PATH-PREFER] Metadata has no season; episode is in folder indicating Season {PathSeason}. Using path-derived season.",
                    seasonFromPath.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                seasonNumber = seasonFromPath.Value;
            }
            else if (hasMetadataSeason && (seasonNumber == null || seasonNumber.Value == 1) && seasonFromPath.HasValue && seasonFromPath.Value >= 2)
            {
                _logger.LogInformation("[MR] [SEASON-METADATA-PREFER] Using metadata season {Season} (not changing to path-derived {PathSeason}). EpisodeId={EpisodeId}",
                    seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", seasonFromPath.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), episode.Id);
            }

            var currentFileNameForSeason = Path.GetFileNameWithoutExtension(path);
            var seasonFromFilename = SafeName.ParseMaxSeasonNumberFromFileName(currentFileNameForSeason ?? string.Empty);
            if (!hasMetadataSeason && !isOverstuffedSeasonFolder && seasonFromFilename.HasValue && seasonFromFilename.Value >= 2 &&
                (seasonNumber == null || seasonNumber.Value == 1) &&
                (seasonFromPath == null || seasonFromPath.Value == 1))
            {
                _logger.LogWarning("[MR] [SEASON-FILENAME-PREFER] Metadata has no season; filename indicates Season {FilenameSeason}. Using filename-derived season. EpisodeId={EpisodeId}, FileName={FileName}",
                    seasonFromFilename.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), episode.Id, currentFileNameForSeason ?? "NULL");
                seasonNumber = seasonFromFilename.Value;
            }
            
            // Move episode to the correct season folder according to metadata (seasonNumber = episode.ParentIndexNumber when metadata exists).
            // Then rename with correct format and title. We do not change season or episode numbers when metadata exists.
            // CRITICAL: Target folder must match metadata so each season folder contains the right episodes.
            if (seasonNumber.HasValue && !string.IsNullOrWhiteSpace(seriesPath) && Directory.Exists(seriesPath))
            {
                // Get season name from metadata
                string? seasonNameFromMetadata = null;
                if (episode.Series != null)
                {
                    try
                    {
                        // Get all seasons for the series
                        var seasons = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                        {
                            ParentId = episode.Series.Id,
                            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Season },
                            Recursive = false
                        }).Cast<MediaBrowser.Controller.Entities.TV.Season>().ToList();
                        
                        // Find the season matching the episode's season number
                        var matchingSeason = seasons.FirstOrDefault(s => s.IndexNumber == seasonNumber.Value);
                        if (matchingSeason != null)
                        {
                            seasonNameFromMetadata = matchingSeason.Name?.Trim();
                            if (!string.IsNullOrWhiteSpace(seasonNameFromMetadata))
                            {
                                _logger.LogInformation("[MR] [SEASON-NAME] Found season name from metadata: Season {Season} = '{SeasonName}'", 
                                    seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), 
                                    seasonNameFromMetadata);
                            }
                            else
                            {
                                _logger.LogInformation("[MR] [SEASON-NAME] Season {Season} exists in metadata but has no name (will use season number only)", 
                                    seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            }
                        }
                        else
                        {
                            _logger.LogWarning("[MR] [SEASON-NAME] Season {Season} not found in metadata (will use season number only)", 
                                seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }
                    catch (Exception seasonEx)
                    {
                        _logger.LogWarning(seasonEx, "[MR] [SEASON-NAME] Could not retrieve season name from metadata: {Error}", seasonEx.Message);
                    }
                }
                
                // Determine the correct season folder name
                _logger.LogInformation("[MR] [SEASON-FOLDER-RENDER] Rendering season folder name: Format='{Format}', Season={Season}, SeasonName='{SeasonName}'", 
                    cfg.SeasonFolderFormat, 
                    seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    seasonNameFromMetadata ?? "NULL");
                
                var correctSeasonFolderName = SafeName.RenderSeasonFolder(
                    cfg.SeasonFolderFormat,
                    seasonNumber.Value,
                    seasonNameFromMetadata);
                var correctSeasonFolderPath = Path.Combine(seriesPath, correctSeasonFolderName);
                
                _logger.LogInformation("[MR] [SEASON-FOLDER-RENDER] Rendered season folder name: '{FolderName}'", correctSeasonFolderName);
                
                // Check if episode is currently in the correct season folder
                var currentEpisodeDirectory = Path.GetDirectoryName(path);
                var isInCorrectSeasonFolder = string.Equals(
                    currentEpisodeDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    correctSeasonFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
                
                if (!isInCorrectSeasonFolder)
                {
                    _logger.LogWarning("[MR] [EPISODE-WRONG-SEASON-FOLDER] Episode is in wrong season folder!");
                    _logger.LogWarning("[MR] [EPISODE-WRONG-SEASON-FOLDER] Current folder: {Current}", currentEpisodeDirectory ?? "NULL");
                    _logger.LogWarning("[MR] [EPISODE-WRONG-SEASON-FOLDER] Correct folder: {Correct}", correctSeasonFolderPath);
                    _logger.LogWarning("[MR] [EPISODE-WRONG-SEASON-FOLDER] Episode metadata: Season {Season}, Episode {Episode}", 
                        seasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                    
                    // Create the correct season folder if it doesn't exist
                    if (!Directory.Exists(correctSeasonFolderPath))
                    {
                        if (cfg.DryRun)
                        {
                            _logger.LogWarning("[MR] [EPISODE-WRONG-SEASON-FOLDER] DRY RUN: Would create season folder: {Path}", correctSeasonFolderPath);
                        }
                        else
                        {
                            Directory.CreateDirectory(correctSeasonFolderPath);
                            _logger.LogInformation("[MR] [EPISODE-WRONG-SEASON-FOLDER] ✓ Created correct season folder: {Path}", correctSeasonFolderPath);
                        }
                    }
                    
                    // Move episode to correct season folder
                    var fileName = Path.GetFileName(path);
                    var newEpisodePath = Path.Combine(correctSeasonFolderPath, fileName);
                    
                    if (!File.Exists(newEpisodePath))
                    {
                        if (cfg.DryRun)
                        {
                            _logger.LogWarning("[MR] [EPISODE-WRONG-SEASON-FOLDER] DRY RUN: Would move episode from {From} to {To}", path, newEpisodePath);
                        }
                        else
                        {
                            File.Move(path, newEpisodePath);
                            _logger.LogInformation("[MR] [EPISODE-WRONG-SEASON-FOLDER] ✓ Moved episode to correct season folder");
                            _logger.LogInformation("[MR] [EPISODE-WRONG-SEASON-FOLDER] From: {From}", path);
                            _logger.LogInformation("[MR] [EPISODE-WRONG-SEASON-FOLDER] To: {To}", newEpisodePath);
                            
                            // Update path for subsequent processing
                            path = newEpisodePath;
                            episodeDirectory = correctSeasonFolderPath;
                            isInSeriesRoot = false;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[MR] [EPISODE-WRONG-SEASON-FOLDER] Target file already exists in correct season folder. Skipping move. Path: {Path}", newEpisodePath);
                        // Update path for subsequent processing
                        path = newEpisodePath;
                        episodeDirectory = correctSeasonFolderPath;
                        isInSeriesRoot = false;
                    }
                }
                else
                {
                    _logger.LogInformation("[MR] [EPISODE-CORRECT-SEASON-FOLDER] Episode is already in correct season folder: {Path}", correctSeasonFolderPath);
                }
            }
            
            // #region agent log - Hypothesis C: Check IndexNumber at metadata access point
            try
            {
                var indexNumberBeforeAccess = episode.IndexNumber;
                var parentIndexNumberBeforeAccess = episode.ParentIndexNumber;
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "C", location = "RenameCoordinator.cs:609", message = "IndexNumber accessed at metadata point", data = new { indexNumber = indexNumberBeforeAccess?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", parentIndexNumber = parentIndexNumberBeforeAccess?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                DebugLogHelper.SafeAppend( logJson);
                _logger.LogInformation("[MR] [DEBUG-HYP-C] IndexNumber at metadata access point: IndexNumber={IndexNumber}, ParentIndexNumber={ParentIndexNumber}, EpisodeId={Id}, Name={Name}", 
                    indexNumberBeforeAccess?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", 
                    parentIndexNumberBeforeAccess?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episode.Id, episode.Name ?? "NULL");
            }
            catch (Exception ex)
            {
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "C", location = "RenameCoordinator.cs:609", message = "ERROR accessing IndexNumber at metadata point", data = new { error = ex.Message, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                DebugLogHelper.SafeAppend( logJson);
                _logger.LogError(ex, "[MR] [DEBUG-HYP-C] ERROR accessing IndexNumber at metadata point: {Error}", ex.Message);
            }
            // #endregion
            
            // Use within-season episode from folder-count-mismatch mapping when set (e.g. absolute 37 -> S2E01)
            var episodeNumber = episodeNumberWithinSeasonFromMapping ?? episode.IndexNumber; // Episode number from metadata or mapping
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
                DebugLogHelper.SafeAppend( logJson);
                _logger.LogInformation("[MR] [DEBUG-HYP-D] Episode.Series state: Type={Type}, Id={Id}, Name={Name}, EpisodeId={EpisodeId}", 
                    seriesType, seriesId, seriesNameFromObj, episode.Id);
            }
            catch (Exception ex)
            {
                var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "RenameCoordinator.cs:625", message = "ERROR accessing Series object", data = new { error = ex.Message, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                DebugLogHelper.SafeAppend( logJson);
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
                    DebugLogHelper.SafeAppend( logJson);
                    _logger.LogWarning("[MR] [DEBUG-HYP-E] IndexNumber is NULL! Episode type: {Type}, EpisodeId: {Id}, All properties: {Properties}", 
                        episodeType.FullName, episode.Id, string.Join("; ", allProps.Select(kvp => $"{kvp.Key}={kvp.Value}")));
                }
                catch (Exception ex)
                {
                    var logData = new { sessionId = "debug-session", runId = "run1", hypothesisId = "E", location = "RenameCoordinator.cs:640", message = "ERROR getting full episode state", data = new { error = ex.Message, episodeId = episode.Id.ToString() }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                    var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                    DebugLogHelper.SafeAppend( logJson);
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
            
            // CRITICAL: Use only metadata for season/episode numbers in the output filename so they never change.
            // Directories (series/season folders) already match metadata; the filename must use episode.ParentIndexNumber and episode.IndexNumber when available.
            var seasonNumberForFilename = episode.ParentIndexNumber ?? seasonNumber;
            var episodeNumberForFilename = episode.IndexNumber ?? episodeNumber;
            
            var desiredFileName = SafeName.RenderEpisodeFileName(
                cfg.EpisodeFileFormat,
                seriesName,
                seasonNumberForFilename,
                episodeNumberForFilename,
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
            _logger.LogInformation("[MR] ✓ Using metadata for filename (season/episode never changed): Season={Season}, Episode={Episode}, Title={Title}", 
                seasonNumberForFilename?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "N/A",
                episodeNumberForFilename?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "N/A",
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
                DebugLogHelper.SafeAppend( logJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MR] [DEBUG] [FILENAME-COMPARISON] ERROR logging filename comparison: {Error}", ex.Message);
                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "FILENAME-COMPARISON", location = "RenameCoordinator.cs:2430", message = "ERROR logging filename comparison", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
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

            // Safeguard: ensure desired filename is valid and SxxExx matches metadata before calling rename service
            if (!SafeName.IsValidFolderOrFileName(desiredFileName))
            {
                _logger.LogWarning("[MR] [SAFEGUARD] Episode file rename skipped: desired file name is invalid. EpisodeId: {Id}, S{Season}E{Episode}",
                    episode.Id, seasonNumberForFilename?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?", episodeNumberForFilename?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
            }
            else if (!SafeName.DesiredEpisodeFileNameMatchesMetadata(desiredFileName, episode.ParentIndexNumber, episode.IndexNumber))
            {
                _logger.LogError("[MR] [SAFEGUARD] Episode file rename skipped: desired filename SxxExx does not match metadata. Desired: '{Desired}', Metadata S{Season}E{Episode}. EpisodeId: {Id}",
                    desiredFileName, episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?", episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?", episode.Id);
            }
            else
            {
                // Pass the updated path if file was moved to Season 1 folder
                _pathRenamer.TryRenameEpisodeFile(episode, desiredFileName, fileExtension, cfg.DryRun, path);
            }
            
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
            var seasonNum = episode?.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
            var episodeNum = episode?.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
            _logger.LogError(ex, "[MR] ERROR in HandleEpisodeUpdate for episode {EpisodeName} (S{Season}E{Episode}, ID: {EpisodeId}): {Message}", 
                episode?.Name ?? "Unknown", seasonNum, episodeNum, episode?.Id, ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
        }
    }

    /// <summary>
    /// Gets the ordered list of (season index, episode count) from metadata so directory can match exactly.
    /// E.g. [(1, 61), (2, 54), (3, 48)] means Season 1 has 61 episodes, Season 2 has 54, etc. Uses metadata as-is.
    /// Only if a season has more than maxReasonablePerSeason (e.g. 200) do we use a heuristic split (wrong "all in one" library structure).
    /// </summary>
    private System.Collections.Generic.List<(int seasonIndex, int episodeCount)> GetMetadataSeasonEpisodeCountsOrdered(MediaBrowser.Controller.Entities.TV.Series series, int folderFileCount, int maxReasonablePerSeason = 200)
    {
        if (series == null)
            return null;
        try
        {
            var seasons = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                ParentId = series.Id,
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Season },
                Recursive = false
            }).Cast<MediaBrowser.Controller.Entities.TV.Season>()
              .OrderBy(s => s.IndexNumber ?? int.MaxValue)
              .ToList();
            var result = new System.Collections.Generic.List<(int, int)>();
            bool useHeuristic = false;
            foreach (var season in seasons)
            {
                var seasonIndex = season.IndexNumber ?? 0;
                if (seasonIndex < 1)
                    continue;
                var count = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    ParentId = season.Id,
                    IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Episode },
                    Recursive = false
                }).Count;
                if (count > maxReasonablePerSeason)
                    useHeuristic = true;
                result.Add((seasonIndex, count));
            }
            if (result.Count == 0)
                return null;
            if (useHeuristic && folderFileCount > maxReasonablePerSeason)
            {
                const int heuristicPerSeason = 50;
                var numSeasons = Math.Min(30, (folderFileCount + heuristicPerSeason - 1) / heuristicPerSeason);
                result.Clear();
                for (var i = 1; i <= numSeasons; i++)
                    result.Add((i, i < numSeasons ? heuristicPerSeason : (folderFileCount - (numSeasons - 1) * heuristicPerSeason)));
                _logger.LogInformation("[MR] [METADATA-MATCH] Metadata had one season with >{Max} episodes (wrong structure). Using heuristic ({EpsPerSeason} eps/season, {Seasons} seasons) so directory can match.", maxReasonablePerSeason.ToString(System.Globalization.CultureInfo.InvariantCulture), heuristicPerSeason.ToString(System.Globalization.CultureInfo.InvariantCulture), numSeasons.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MR] [METADATA-MATCH] Could not get season episode counts: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Gets this file's 1-based position when all video files in the folder are sorted by episode number (from filename).
    /// So we can match "first 32 in order → Season 1, next 54 → Season 2" exactly to metadata.
    /// </summary>
    private (int position1Based, int totalCount)? GetPositionInFolderByEpisodeOrder(string folderPath, string currentFilePath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(currentFilePath) || !Directory.Exists(folderPath))
            return null;
        try
        {
            var videoExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" };
            var files = Directory.GetFiles(folderPath)
                .Where(f => videoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new { Path = f, EpisodeNumber = SafeName.ParseEpisodeNumberFromFileName(Path.GetFileNameWithoutExtension(f) ?? string.Empty) })
                .OrderBy(x => x.EpisodeNumber ?? int.MaxValue)
                .ThenBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var currentFileName = Path.GetFileName(currentFilePath);
            for (var i = 0; i < files.Count; i++)
            {
                if (string.Equals(Path.GetFileName(files[i].Path), currentFileName, StringComparison.OrdinalIgnoreCase))
                    return (i + 1, files.Count);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MR] [METADATA-MATCH] Could not get position in folder: {Error}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Maps 1-based position (e.g. 33) to (season, episode within season) using metadata counts.
    /// E.g. counts [(1,32), (2,54)] → position 1-32 → (1,1)..(1,32), position 33-86 → (2,1)..(2,54).
    /// </summary>
    private (int season, int episodeWithinSeason)? MapPositionToSeasonAndEpisode(System.Collections.Generic.List<(int seasonIndex, int episodeCount)> counts, int position1Based)
    {
        if (counts == null || counts.Count == 0 || position1Based < 1)
            return null;
        var cumulative = 0;
        var lastSeason = (seasonIndex: 0, episodeCount: 0);
        foreach (var (seasonIndex, episodeCount) in counts)
        {
            lastSeason = (seasonIndex, episodeCount);
            var start = cumulative + 1;
            var end = cumulative + episodeCount;
            if (position1Based >= start && position1Based <= end)
                return (seasonIndex, position1Based - start + 1);
            cumulative = end;
        }
        // Overflow: folder has more files than metadata total; assign to last season so every file gets a stable (season, ep).
        if (position1Based > cumulative && lastSeason.seasonIndex >= 1)
            return (lastSeason.seasonIndex, lastSeason.episodeCount + (position1Based - cumulative));
        return null;
    }

    /// <summary>
    /// Gets or builds a one-time (season, episodeWithinSeason) mapping for the folder and returns the assignment for the current file.
    /// Prevents duplicate S01E01/S01E02 etc. by fixing each file's assignment when we first see the folder, instead of recomputing position after moves.
    /// </summary>
    private (int season, int episodeWithinSeason)? GetOrBuildCachedSeasonEpisodeForFile(string folderPath, string currentFilePath, MediaBrowser.Controller.Entities.TV.Series series, int folderFileCount, int overstuffedThreshold)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(currentFilePath) || !Directory.Exists(folderPath) || series == null)
            return null;
        var normalizedFolder = Path.GetFullPath(folderPath);
        lock (_folderSeasonEpisodeMapCacheLock)
        {
            if (_folderSeasonEpisodeMapCache.TryGetValue(normalizedFolder, out var fileMap) && fileMap.TryGetValue(currentFilePath, out var cached))
                return cached;

            var videoExtensions = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" };
            var files = Directory.GetFiles(folderPath)
                .Where(f => videoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(f => new { Path = f, EpisodeNumber = SafeName.ParseEpisodeNumberFromFileName(Path.GetFileNameWithoutExtension(f) ?? string.Empty) })
                .OrderBy(x => x.EpisodeNumber ?? int.MaxValue)
                .ThenBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var metadataCounts = GetMetadataSeasonEpisodeCountsOrdered(series, folderFileCount, overstuffedThreshold);
            if (metadataCounts == null || metadataCounts.Count == 0)
                return null;
            var newMap = new Dictionary<string, (int season, int episodeWithinSeason)>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < files.Count; i++)
            {
                var position1Based = i + 1;
                var mapped = MapPositionToSeasonAndEpisode(metadataCounts, position1Based);
                if (mapped.HasValue)
                    newMap[files[i].Path] = mapped.Value;
            }
            _folderSeasonEpisodeMapCache[normalizedFolder] = newMap;
            return newMap.TryGetValue(currentFilePath, out var result) ? result : null;
        }
    }

    /// <summary>
    /// Gets the episode count for a given season from the library metadata (number of episode items under that season).
    /// Used to compare folder file count vs metadata: e.g. metadata says Season 1 has 36 episodes; if folder has more, redistribute.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="seasonNumber">1-based season number (e.g. 1 for Season 1).</param>
    /// <returns>Episode count for that season in metadata, or null if unable to determine.</returns>
    private int? GetMetadataEpisodeCountForSeason(MediaBrowser.Controller.Entities.TV.Series series, int seasonNumber)
    {
        if (series == null || seasonNumber < 1)
            return null;
        try
        {
            var seasons = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                ParentId = series.Id,
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Season },
                Recursive = false
            }).Cast<MediaBrowser.Controller.Entities.TV.Season>()
              .OrderBy(s => s.IndexNumber ?? int.MaxValue)
              .ToList();
            var season = seasons.FirstOrDefault(s => (s.IndexNumber ?? 0) == seasonNumber);
            if (season == null)
                return null;
            var count = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                ParentId = season.Id,
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Episode },
                Recursive = false
            }).Count;
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MR] [METADATA-EPISODE-COUNT] Could not get episode count for Season {Season}: {Error}", seasonNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Maps an absolute episode number (1-based across the whole series) to the metadata season that contains it,
    /// using the library's per-season episode counts (e.g. Season 1 = 1-36, Season 2 = 37-90). Works universally.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <param name="absoluteEpisodeNumber">Absolute episode index (e.g. 37 = first episode of season 2 if S1 has 36).</param>
    /// <returns>The 1-based season number that contains this episode, or null if unable to determine.</returns>
    private int? TryGetSeasonFromAbsoluteEpisodeNumber(MediaBrowser.Controller.Entities.TV.Series series, int absoluteEpisodeNumber)
    {
        if (series == null || absoluteEpisodeNumber < 1)
            return null;
        try
        {
            var seasons = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                ParentId = series.Id,
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Season },
                Recursive = false
            }).Cast<MediaBrowser.Controller.Entities.TV.Season>()
              .OrderBy(s => s.IndexNumber ?? int.MaxValue)
              .ToList();
            if (seasons.Count == 0)
                return null;
            var cumulativeEnd = 0;
            foreach (var season in seasons)
            {
                var seasonIndex = season.IndexNumber ?? 0;
                if (seasonIndex < 1)
                    continue;
                var episodeCount = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    ParentId = season.Id,
                    IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Episode },
                    Recursive = false
                }).Count;
                cumulativeEnd += episodeCount;
                if (absoluteEpisodeNumber <= cumulativeEnd)
                {
                    _logger.LogInformation("[MR] [SEASON-FROM-METADATA] Absolute episode {AbsEp} maps to metadata Season {Season} (season has {Count} episodes, cumulative end {CumEnd}).",
                        absoluteEpisodeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), seasonIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), episodeCount.ToString(System.Globalization.CultureInfo.InvariantCulture), cumulativeEnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return seasonIndex;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MR] [SEASON-FROM-METADATA] Could not map absolute episode {Ep} to season: {Error}", absoluteEpisodeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Maps an absolute episode number to (metadata season, episode number within that season).
    /// E.g. if metadata says S1=1-36, S2=37-90, then absolute 37 returns (2, 1), absolute 90 returns (2, 54).
    /// </summary>
    private (int? season, int? episodeWithinSeason) TryGetSeasonAndEpisodeFromAbsoluteEpisodeNumber(MediaBrowser.Controller.Entities.TV.Series series, int absoluteEpisodeNumber)
    {
        if (series == null || absoluteEpisodeNumber < 1)
            return (null, null);
        try
        {
            var seasons = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                ParentId = series.Id,
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Season },
                Recursive = false
            }).Cast<MediaBrowser.Controller.Entities.TV.Season>()
              .OrderBy(s => s.IndexNumber ?? int.MaxValue)
              .ToList();
            if (seasons.Count == 0)
                return (null, null);
            var cumulativeEnd = 0;
            foreach (var season in seasons)
            {
                var seasonIndex = season.IndexNumber ?? 0;
                if (seasonIndex < 1)
                    continue;
                var episodeCount = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
                {
                    ParentId = season.Id,
                    IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Episode },
                    Recursive = false
                }).Count;
                var cumulativeStart = cumulativeEnd;
                cumulativeEnd += episodeCount;
                if (absoluteEpisodeNumber <= cumulativeEnd)
                {
                    var episodeWithinSeason = absoluteEpisodeNumber - cumulativeStart;
                    _logger.LogInformation("[MR] [SEASON-FROM-METADATA] Absolute episode {AbsEp} -> Season {Season} Episode {EpWithin} (range {Start}-{End}).",
                        absoluteEpisodeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), seasonIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), episodeWithinSeason.ToString(System.Globalization.CultureInfo.InvariantCulture), (cumulativeStart + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), cumulativeEnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return (seasonIndex, episodeWithinSeason);
                }
            }
            return (null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MR] [SEASON-FROM-METADATA] Could not map absolute episode {Ep} to season/episode: {Error}", absoluteEpisodeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
            return (null, null);
        }
    }

    /// <summary>
    /// Tries to extract the season number from an episode's path by parsing the parent directory name.
    /// Handles patterns like "Season 01", "Season 11", "S01", "S11".
    /// </summary>
    /// <param name="episodeFilePath">Full path to the episode file.</param>
    /// <returns>The season number if the parent directory matches a season folder pattern; otherwise null.</returns>
    private static int? TryGetSeasonNumberFromFolderPath(string episodeFilePath)
    {
        if (string.IsNullOrWhiteSpace(episodeFilePath))
        {
            return null;
        }

        var episodeDirectory = Path.GetDirectoryName(episodeFilePath);
        if (string.IsNullOrWhiteSpace(episodeDirectory))
        {
            return null;
        }

        var directoryName = Path.GetFileName(episodeDirectory);
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            return null;
        }

        var seasonMatch = System.Text.RegularExpressions.Regex.Match(
            directoryName,
            @"(?:Season\s*|S)(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (seasonMatch.Success && int.TryParse(seasonMatch.Groups[1].Value, out var seasonNum))
        {
            return seasonNum;
        }

        return null;
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
