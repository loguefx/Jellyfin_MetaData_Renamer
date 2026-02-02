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
        if (!cfg.Enabled)
        {
            return;
        }

        if (!cfg.RenameSeriesFolders)
        {
            return;
        }

        // Debounce global spam
        var now = DateTime.UtcNow;
        if (now - _lastGlobalActionUtc < _globalMinInterval)
        {
            return;
        }

        _lastGlobalActionUtc = now;

        if (e.Item is not Series series)
        {
            return;
        }

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

        if (string.IsNullOrWhiteSpace(name) || year is null)
        {
            _logger.LogDebug("[MR] Skip: missing name/year. Name={Name} Year={Year}", name, year);
            return;
        }

        // Identify inference: only rename when provider ids changed
        if (cfg.OnlyRenameWhenProviderIdsChange && series.ProviderIds != null)
        {
            var newHash = ProviderIdHelper.ComputeProviderHash(series.ProviderIds);
            _providerHashByItem.TryGetValue(series.Id, out var oldHash);

            if (string.Equals(newHash, oldHash, StringComparison.Ordinal))
            {
                _logger.LogDebug("[MR] Skip: ProviderIds unchanged. Name={Name}", name);
                return;
            }

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
