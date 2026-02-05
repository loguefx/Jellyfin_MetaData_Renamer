using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MetadataRenamer.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
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
    private readonly Dictionary<Guid, Dictionary<string, string>> _previousProviderIdsByItem = new();

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
            _previousProviderIdsByItem.Clear();
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
                // Debounce global spam for Season items only
                var timeSinceLastAction = now - _lastGlobalActionUtc;
                if (timeSinceLastAction < _globalMinInterval)
                {
                    _logger.LogInformation("[MR] SKIP: Global debounce active. Time since last action: {Seconds} seconds (min: {MinSeconds})",
                        timeSinceLastAction.TotalSeconds, _globalMinInterval.TotalSeconds);
                    return;
                }
                _lastGlobalActionUtc = now;

                if (!cfg.RenameSeasonFolders)
                {
                    _logger.LogInformation("[MR] SKIP: RenameSeasonFolders is disabled in configuration");
                    return;
                }
                HandleSeasonUpdate(season, cfg, now);
            return;
        }

            // Handle Episode items
            if (e.Item is Episode episode)
            {
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
                
                if (!cfg.RenameEpisodeFiles)
                {
                    // #region agent log - MULTI-EPISODE-HYP-B: Track episodes skipped due to config
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-B", location = "RenameCoordinator.cs:148", message = "Episode skipped - RenameEpisodeFiles disabled", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    // #endregion
                    _logger.LogInformation("[MR] SKIP: RenameEpisodeFiles is disabled in configuration");
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

            _logger.LogInformation("[MR] === Year Detection from Metadata ===");
            _logger.LogInformation("[MR] Series: {Name}, ID: {Id}", name ?? "NULL", series.Id);
            _logger.LogInformation("[MR] ProductionYear (from Jellyfin): {ProductionYear}", 
                series.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] PremiereDate (from Jellyfin): {PremiereDate}, Year: {PremiereDateYear}", 
                series.PremiereDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                series.PremiereDate?.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] Final Year Selected: {FinalYear} (Source: {YearSource})", 
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
            // Logic:
            // - If ProcessDuringLibraryScans is false: Only process when provider IDs change (Identify flow)
            // - If ProcessDuringLibraryScans is true: Process during library scans regardless of provider ID changes
            // - The "Identify" flow (provider IDs change) always works regardless of ProcessDuringLibraryScans
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

            // Determine if we should proceed with rename
            bool shouldProceed = false;
            string proceedReason = string.Empty;

            if (cfg.OnlyRenameWhenProviderIdsChange)
            {
                // OnlyRenameWhenProviderIdsChange is enabled
                if (providerIdsChanged || isFirstTime)
                {
                    // Provider IDs changed or first time - always proceed (Identify flow)
                    shouldProceed = true;
                    proceedReason = providerIdsChanged ? "Provider IDs changed (Identify flow)" : "First time processing";
                }
                else if (cfg.ProcessDuringLibraryScans)
                {
                    // Provider IDs unchanged, but ProcessDuringLibraryScans is enabled - proceed during library scan
                    shouldProceed = true;
                    proceedReason = "ProcessDuringLibraryScans enabled (library scan)";
                }
                else
                {
                    // Provider IDs unchanged and ProcessDuringLibraryScans is disabled - skip
                    shouldProceed = false;
                    proceedReason = "Provider IDs unchanged and ProcessDuringLibraryScans disabled";
                }
            }
            else
            {
                // OnlyRenameWhenProviderIdsChange is disabled - always proceed
                shouldProceed = true;
                proceedReason = "OnlyRenameWhenProviderIdsChange disabled";
            }

            if (!shouldProceed)
            {
                // #region agent log
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "B", location = "RenameCoordinator.cs:138", message = "SKIP: ProviderIds unchanged", data = new { seriesName = name, hash = newHash, reason = proceedReason }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
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
                    
                    // Validate and correct year now that we have the provider ID
                    year = ValidateAndCorrectYear(year, providerLabel, providerId, name, path);
                    
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
                        
                        // Validate and correct year now that we have the provider ID
                        year = ValidateAndCorrectYear(year, providerLabel, providerId, name, path);
                        
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

            // Build final desired folder name: Name (Year) [provider-id] or Name (Year) if no provider IDs
            var currentFolderName = Path.GetFileName(path);
            
            // #region agent log - Before folder name generation
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "YEAR-DETECT", location = "RenameCoordinator.cs:594", message = "Before folder name generation", data = new { seriesName = name, year = year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", yearSource = yearSource, providerLabel = providerLabel ?? "NULL", providerId = providerId ?? "NULL", currentFolderName = currentFolderName, format = cfg.SeriesFolderFormat, productionYear = series.ProductionYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", premiereDate = series.PremiereDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            
            _logger.LogInformation("[MR] === Folder Name Generation ===");
            _logger.LogInformation("[MR] Format: {Format}", cfg.SeriesFolderFormat);
            _logger.LogInformation("[MR] Values: Name={Name}, Year={Year} (Source: {YearSource}), Provider={Provider}, ID={Id}", 
                name ?? "NULL",
                year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                yearSource,
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

            var renameSuccessful = _pathRenamer.TryRenameSeriesFolder(series, desiredFolderName, cfg.DryRun);

            // After successful series rename, scan for episodes in the series root and process them
            if (renameSuccessful && cfg.RenameEpisodeFiles && !cfg.DryRun)
            {
                _logger.LogInformation("[MR] === Scanning for episodes in series root after rename ===");
                // Calculate the new path (series.Path is still the old path after rename)
                var parentDirectory = Path.GetDirectoryName(path);
                var newSeriesPath = Path.Combine(parentDirectory, desiredFolderName);
                _logger.LogInformation("[MR] Using new series path for episode processing: {NewPath}", newSeriesPath);
                ProcessEpisodesInSeriesRoot(newSeriesPath, cfg, now);
            }

            _logger.LogInformation("[MR] ===== Processing Complete =====");
    }

    /// <summary>
    /// Handles movie folder renaming.
    /// </summary>
    private void HandleMovieUpdate(Movie movie, PluginConfiguration cfg, DateTime now)
    {
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
            _logger.LogInformation("[MR] Processing Season: Name={Name}, Id={Id}, Path={Path}, Season Number={SeasonNumber}", 
                season.Name, season.Id, season.Path, season.IndexNumber);

            // Per-item cooldown
            if (_lastAttemptUtcByItem.TryGetValue(season.Id, out var lastTry))
            {
                var timeSinceLastTry = (now - lastTry).TotalSeconds;
                if (timeSinceLastTry < cfg.PerItemCooldownSeconds)
                {
                    _logger.LogInformation(
                        "[MR] SKIP: Cooldown active. SeasonId={Id}, Name={Name}, Time since last try: {Seconds} seconds (cooldown: {CooldownSeconds})",
                        season.Id, season.Name, timeSinceLastTry.ToString(System.Globalization.CultureInfo.InvariantCulture), cfg.PerItemCooldownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }
            }

            _lastAttemptUtcByItem[season.Id] = now;

            var path = season.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                _logger.LogWarning("[MR] SKIP: Season has no path. SeasonId={Id}, Name={Name}", season.Id, season.Name);
            return;
        }

            if (!Directory.Exists(path))
            {
                _logger.LogWarning("[MR] SKIP: Season path does not exist on disk. Path={Path}, SeasonId={Id}, Name={Name}", path, season.Id, season.Name);
                return;
            }

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
                _logger.LogWarning("[MR] SKIP: Season missing season number. SeasonId={Id}, Name={Name}", season.Id, season.Name);
                return;
            }

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

            _pathRenamer.TryRenameSeasonFolder(season, desiredFolderName, cfg.DryRun);

            _logger.LogInformation("[MR] ===== Season Processing Complete =====");
        }
        catch (Exception ex)
        {
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
    private void HandleEpisodeUpdate(Episode episode, PluginConfiguration cfg, DateTime now)
    {
        try
        {
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "RenameCoordinator.cs:439", message = "HandleEpisodeUpdate entry", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", episodePath = episode.Path ?? "NULL", episodeType = episode.GetType().FullName }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            
            _logger.LogInformation("[MR] Processing Episode: Name={Name}, Id={Id}, Path={Path}", episode.Name, episode.Id, episode.Path);
            
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
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-C", location = "RenameCoordinator.cs:525", message = "Episode skipped - cooldown active", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", timeSinceLastTry = timeSinceLastTry.ToString(System.Globalization.CultureInfo.InvariantCulture), cooldownSeconds = cfg.PerItemCooldownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    // #endregion
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
                // #region agent log - MULTI-EPISODE-HYP-D: Track episodes skipped due to no path
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-D", location = "RenameCoordinator.cs:539", message = "Episode skipped - no path", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                // #endregion
                _logger.LogWarning("[MR] SKIP: Episode has no path. EpisodeId={Id}, Name={Name}", episode.Id, episode.Name);
                return;
            }

            if (!File.Exists(path))
            {
                // #region agent log - MULTI-EPISODE-HYP-E: Track episodes skipped due to file not existing
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "MULTI-EP-E", location = "RenameCoordinator.cs:546", message = "Episode skipped - file does not exist", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", path = path }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                // #endregion
                _logger.LogWarning("[MR] SKIP: Episode file does not exist on disk. Path={Path}, EpisodeId={Id}, Name={Name}", path, episode.Id, episode.Name);
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
            var episodeTitle = episode.Name?.Trim() ?? string.Empty;
            
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
                    _logger.LogWarning("[MR] SKIP: Episode missing episode number in metadata AND could not parse from filename. Cannot determine correct episode number. Season={Season}, Episode={Episode}, Filename={FileName}", 
                        seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                        episodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL",
                        currentFileName);
                    return;
                }
            }

            // SAFETY CHECK: Parse episode number from filename and compare with metadata
            // Only rename if the episode number in filename matches metadata episode number
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
                    _logger.LogWarning("[MR] SKIP: Episode number mismatch! Filename says episode {FilenameEp}, but metadata says episode {MetadataEp}. " +
                        "This prevents incorrect renames (e.g., renaming 'episode 1' to 'episode 5'). " +
                        "Please verify the file is correctly identified in Jellyfin.",
                        filenameEpisodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        episodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
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

            // Episode title is preferred but not required - we can still rename with just episode number
            if (string.IsNullOrWhiteSpace(episodeTitle))
            {
                _logger.LogWarning("[MR] Warning: Episode title is missing in metadata. Will use episode number only for renaming. EpisodeId={Id}", episode.Id);
                episodeTitle = string.Empty; // Use empty string, format will handle it
            }

            // Build desired file name (without extension) using METADATA VALUES ONLY
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
            _logger.LogInformation("[MR] Format Template: {Format}", cfg.EpisodeFileFormat);
            _logger.LogInformation("[MR] ✓ Safety check passed: Filename episode number matches metadata episode number");
            _logger.LogInformation("[MR] ✓ Using metadata values: Season={Season}, Episode={Episode}, Title={Title}", 
                seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "N/A",
                episodeNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(episodeTitle) ? "(no title)" : episodeTitle);
            _logger.LogInformation("[MR] Dry Run Mode: {DryRun}", cfg.DryRun);

            // Pass the updated path if file was moved to Season 1 folder
            _pathRenamer.TryRenameEpisodeFile(episode, desiredFileName, fileExtension, cfg.DryRun, path);

            _logger.LogInformation("[MR] ===== Episode Processing Complete =====");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] ERROR in HandleEpisodeUpdate: {Message}", ex.Message);
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
