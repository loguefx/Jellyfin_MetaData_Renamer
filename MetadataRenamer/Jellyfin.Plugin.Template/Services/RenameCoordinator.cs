using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.MetadataRenamer.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

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
    /// Handles item update events and triggers renaming if conditions are met.
    /// </summary>
    /// <param name="e">The item change event arguments.</param>
    /// <param name="cfg">The plugin configuration.</param>
    public void HandleItemUpdated(ItemChangeEventArgs e, PluginConfiguration cfg)
    {
        // Log all item updates to debug
        _logger.LogDebug("[MR] ItemUpdated event: Type={Type} Name={Name} Id={Id}", e.Item?.GetType().Name ?? "null", e.Item?.Name ?? "null", e.Item?.Id ?? Guid.Empty);

        if (!cfg.Enabled)
        {
            _logger.LogDebug("[MR] Plugin disabled, skipping");
            return;
        }

        if (!cfg.RenameSeriesFolders)
        {
            _logger.LogDebug("[MR] RenameSeriesFolders disabled, skipping");
            return;
        }

        // Debounce global spam
        var now = DateTime.UtcNow;
        if (now - _lastGlobalActionUtc < _globalMinInterval)
        {
            _logger.LogDebug("[MR] Global debounce active, skipping");
            return;
        }

        _lastGlobalActionUtc = now;

        if (e.Item is not Series series)
        {
            _logger.LogDebug("[MR] Item is not a Series (Type={Type}), skipping", e.Item?.GetType().Name ?? "null");
            return;
        }

        _logger.LogDebug("[MR] Processing Series: Name={Name} Id={Id} Path={Path}", series.Name, series.Id, series.Path);

        // Per-item cooldown
        if (_lastAttemptUtcByItem.TryGetValue(series.Id, out var lastTry))
        {
            if ((now - lastTry).TotalSeconds < cfg.PerItemCooldownSeconds)
            {
                _logger.LogDebug("[MR] Cooldown skip for SeriesId={Id} Name={Name}", series.Id, series.Name);
                return;
            }
        }

        _lastAttemptUtcByItem[series.Id] = now;

        var path = series.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogDebug("[MR] Skip: no path. SeriesId={Id}", series.Id);
            return;
        }

        if (!Directory.Exists(path))
        {
            _logger.LogDebug("[MR] Skip: path not found: {Path}", path);
            return;
        }

        // Must be matched
        if (cfg.RequireProviderIdMatch)
        {
            if (series.ProviderIds == null || series.ProviderIds.Count == 0)
            {
                _logger.LogDebug("[MR] Skip: no ProviderIds. Name={Name}", series.Name);
                return;
            }
        }

        var name = series.Name?.Trim();
        var year = series.ProductionYear;

        _logger.LogDebug("[MR] Series details: Name={Name} Year={Year} ProviderIds={ProviderIds}", name, year, series.ProviderIds != null ? string.Join(", ", series.ProviderIds.Select(kv => $"{kv.Key}={kv.Value}")) : "null");

        if (string.IsNullOrWhiteSpace(name) || year is null)
        {
            _logger.LogInformation("[MR] Skip: missing name/year. Name={Name} Year={Year}", name, year);
            return;
        }

        // Identify inference: only rename when provider ids changed
        if (cfg.OnlyRenameWhenProviderIdsChange && series.ProviderIds != null)
        {
            var newHash = ProviderIdHelper.ComputeProviderHash(series.ProviderIds);
            _providerHashByItem.TryGetValue(series.Id, out var oldHash);

            _logger.LogInformation("[MR] Provider hash check: Old={OldHash} New={NewHash} Name={Name}", oldHash ?? "(none)", newHash, name);

            if (string.Equals(newHash, oldHash, StringComparison.Ordinal))
            {
                _logger.LogInformation("[MR] Skip: ProviderIds unchanged. Name={Name} Hash={Hash}", name, newHash);
                return;
            }

            _logger.LogInformation("[MR] ProviderIds changed! Old={OldHash} New={NewHash}", oldHash ?? "(none)", newHash);
            _providerHashByItem[series.Id] = newHash;
        }

        // Choose best provider for naming suffix
        if (series.ProviderIds == null)
        {
            _logger.LogDebug("[MR] Skip: ProviderIds is null. Name={Name}", name);
            return;
        }

        var best = ProviderIdHelper.GetBestProvider(series.ProviderIds, cfg.PreferredSeriesProviders);
        if (best is null)
        {
            _logger.LogDebug("[MR] Skip: no usable provider id. Name={Name}", name);
            return;
        }

        // Build final desired folder name: Name (Year) [provider-id]
        var desiredFolderName = SafeName.RenderSeriesFolder(
            cfg.SeriesFolderFormat,
            name,
            year.Value,
            best.Value.ProviderLabel,
            best.Value.Id);

        _logger.LogInformation("[MR] Desired folder: {Folder} (from {Path})", desiredFolderName, path);

        _pathRenamer.TryRenameSeriesFolder(series, desiredFolderName, cfg.DryRun);
    }
}
