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
        string currentPath = string.Empty;
        string newFullPath = string.Empty;

        try
        {
            _logger.LogInformation("[MR] === TryRenameSeriesFolder Called ===");
            _logger.LogInformation("[MR] Series: {Name}, ID: {Id}", series.Name, series.Id);
            _logger.LogInformation("[MR] Desired Folder Name: {Desired}", desiredFolderName);
            _logger.LogInformation("[MR] Dry Run: {DryRun}", dryRun);

            currentPath = series.Path;
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                _logger.LogWarning("[MR] SKIP: Series.Path is null or empty");
                return;
            }

            _logger.LogInformation("[MR] Current Path: {Path}", currentPath);

            var currentDir = new DirectoryInfo(currentPath);
            if (!currentDir.Exists)
            {
                _logger.LogError("[MR] ERROR: Series folder does not exist: {Path}", currentPath);
                return;
            }

            _logger.LogInformation("[MR] Current Directory Name: {Name}", currentDir.Name);
            _logger.LogInformation("[MR] Current Directory Exists: {Exists}", currentDir.Exists);

            var parent = currentDir.Parent;
            if (parent == null)
            {
                _logger.LogError("[MR] ERROR: Cannot rename root directory: {Path}", currentPath);
                return;
            }

            _logger.LogInformation("[MR] Parent Directory: {Parent}", parent.FullName);

            if (string.Equals(currentDir.Name, desiredFolderName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[MR] SKIP: Folder name already matches desired name. Current: {Current}, Desired: {Desired}", currentDir.Name, desiredFolderName);
                return;
            }

            newFullPath = Path.Combine(parent.FullName, desiredFolderName);
            _logger.LogInformation("[MR] New Full Path: {NewPath}", newFullPath);

            if (Directory.Exists(newFullPath))
            {
                _logger.LogError("[MR] ERROR: Target folder already exists. Cannot rename. From: {From}, To: {To}", currentPath, newFullPath);
                return;
            }

            if (dryRun)
            {
                // #region agent log
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:66", message = "DRY RUN - not renaming", data = new { from = currentPath, to = newFullPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                // #endregion
                _logger.LogWarning("[MR] DRY RUN MODE: Would rename {From} -> {To}", currentPath, newFullPath);
                _logger.LogWarning("[MR] DRY RUN: No actual rename performed. Disable Dry Run mode to perform actual renames.");
                return;
            }

            _logger.LogInformation("[MR] === Attempting Actual Rename ===");
            _logger.LogInformation("[MR] From: {From}", currentPath);
            _logger.LogInformation("[MR] To: {To}", newFullPath);

            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:74", message = "ACTUAL RENAME starting", data = new { from = currentPath, to = newFullPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            
            Directory.Move(currentPath, newFullPath);
            
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:76", message = "RENAME SUCCESS", data = new { from = currentPath, to = newFullPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            
            _logger.LogInformation("[MR] ✓✓✓ SUCCESS: Folder renamed successfully!");
            _logger.LogInformation("[MR] Old: {From}", currentPath);
            _logger.LogInformation("[MR] New: {To}", newFullPath);
            
            // Verify the rename
            if (Directory.Exists(newFullPath))
            {
                _logger.LogInformation("[MR] ✓ Verification: New folder exists");
            }
            else
            {
                _logger.LogError("[MR] ✗ Verification FAILED: New folder does not exist after rename!");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:80", message = "RENAME FAILED - UnauthorizedAccess", data = new { from = currentPath, to = newFullPath, error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            _logger.LogError(ex, "[MR] ERROR: UnauthorizedAccessException - Permission denied. From: {From}, To: {To}", currentPath, newFullPath);
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogError(ex, "[MR] ERROR: DirectoryNotFoundException - Source directory not found. Path: {Path}", currentPath);
        }
        catch (IOException ex)
        {
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:80", message = "RENAME FAILED - IOException", data = new { from = currentPath, to = newFullPath, error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            _logger.LogError(ex, "[MR] ERROR: IOException - File system error. From: {From}, To: {To}, Message: {Message}", currentPath, newFullPath, ex.Message);
        }
        catch (Exception ex)
        {
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:80", message = "RENAME FAILED", data = new { from = currentPath, to = newFullPath, error = ex.Message, exceptionType = ex.GetType().Name }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            _logger.LogError(ex, "[MR] ERROR: Unexpected exception during rename. Type: {Type}, Message: {Message}", ex.GetType().Name, ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
        }
    }
}
