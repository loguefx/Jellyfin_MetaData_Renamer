using System;
using System.IO;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetadataRenamer.Services;

/// <summary>
/// Service for safely renaming series folders on disk.
/// </summary>
public class PathRenameService
{
    private readonly ILogger<PathRenameService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PathRenameService"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public PathRenameService(ILogger<PathRenameService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Attempts to rename a series folder to the desired name.
    /// </summary>
    /// <param name="series">The series entity.</param>
    /// <param name="desiredFolderName">The desired folder name.</param>
    /// <param name="dryRun">Whether to perform a dry run (log only, no actual rename).</param>
    public void TryRenameSeriesFolder(Series series, string desiredFolderName, bool dryRun)
    {
        var currentPath = series.Path;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return;
        }

        var currentDir = new DirectoryInfo(currentPath);
        if (!currentDir.Exists)
        {
            _logger.LogWarning("[MR] Series folder not found: {Path}", currentPath);
            return;
        }

        var parent = currentDir.Parent;
        if (parent == null)
        {
            _logger.LogWarning("[MR] Cannot rename root directory: {Path}", currentPath);
            return;
        }

        if (string.Equals(currentDir.Name, desiredFolderName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("[MR] Already correct: {Path}", currentPath);
            return;
        }

        var newFullPath = Path.Combine(parent.FullName, desiredFolderName);

        if (Directory.Exists(newFullPath))
        {
            _logger.LogWarning("[MR] Target exists, skipping. From={From} To={To}", currentPath, newFullPath);
            return;
        }

        if (dryRun)
        {
            _logger.LogInformation("[MR] DRY RUN rename: {From} -> {To}", currentPath, newFullPath);
            return;
        }

        try
        {
            _logger.LogInformation("[MR] Renaming: {From} -> {To}", currentPath, newFullPath);
            Directory.Move(currentPath, newFullPath);
            _logger.LogInformation("[MR] RENAMED OK: {From} -> {To}", currentPath, newFullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] Rename failed: {From} -> {To}", currentPath, newFullPath);
        }
    }
}
