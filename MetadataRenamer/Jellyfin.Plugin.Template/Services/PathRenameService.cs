using System;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetadataRenamer.Services;

/// <summary>
/// Service for safely renaming series folders on disk.
/// </summary>
public class PathRenameService
{
    private const string DryRunLogMessage = "[MR] Dry Run: {DryRun}";

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
    /// <returns>True if the rename was successful, false otherwise.</returns>
    public bool TryRenameSeriesFolder(Series series, string desiredFolderName, bool dryRun)
    {
        string currentPath = string.Empty;
        string newFullPath = string.Empty;

        try
        {
            // Safeguard: reject null series
            if (series == null)
            {
                _logger.LogError("[MR] [SAFEGUARD] TryRenameSeriesFolder: series is null. Aborting.");
                return false;
            }

            // Safeguard: desired folder name must be valid (non-empty, safe for filesystem)
            if (!SafeName.IsValidFolderOrFileName(desiredFolderName))
            {
                _logger.LogWarning("[MR] [SAFEGUARD] TryRenameSeriesFolder: desired folder name is null, empty, or invalid. Series: {Name}, Id: {Id}. Aborting.",
                    series.Name ?? "NULL", series.Id);
                return false;
            }

            desiredFolderName = SafeName.SanitizeFileName(desiredFolderName?.Trim() ?? string.Empty);
            _logger.LogInformation("[MR] === TryRenameSeriesFolder Called ===");
            _logger.LogInformation("[MR] Series: {Name}, ID: {Id}", series.Name, series.Id);
            _logger.LogInformation("[MR] Desired Folder Name: {Desired}", desiredFolderName);
            _logger.LogInformation(DryRunLogMessage, dryRun);

            currentPath = series.Path;
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                _logger.LogWarning("[MR] SKIP: Series.Path is null or empty");
                return false;
            }

            _logger.LogInformation("[MR] Current Path: {Path}", currentPath);

            var currentDir = new DirectoryInfo(currentPath);
            if (!currentDir.Exists)
            {
                _logger.LogError("[MR] ERROR: Series folder does not exist: {Path}", currentPath);
                return false;
            }

            _logger.LogInformation("[MR] Current Directory Name: {Name}", currentDir.Name);
            _logger.LogInformation("[MR] Current Directory Exists: {Exists}", currentDir.Exists);

            var parent = currentDir.Parent;
            if (parent == null)
            {
                _logger.LogError("[MR] ERROR: Cannot rename root directory: {Path}", currentPath);
                return false;
            }

            _logger.LogInformation("[MR] Parent Directory: {Parent}", parent.FullName);

            if (ShouldSkipSeriesRenameBecauseNameMatches(series, currentDir, desiredFolderName))
                return false;

            newFullPath = Path.Combine(parent.FullName, desiredFolderName);
            _logger.LogInformation("[MR] New Full Path: {NewPath}", newFullPath);

            if (Directory.Exists(newFullPath))
            {
                _logger.LogError("[MR] ERROR: Target folder already exists. Cannot rename. From: {From}, To: {To}", currentPath, newFullPath);
                return false;
            }

            if (dryRun)
            {
                // #region agent log
                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:66", message = "DRY RUN - not renaming", data = new { from = currentPath, to = newFullPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                // #endregion
                _logger.LogWarning("[MR] DRY RUN MODE: Would rename {From} -> {To}", currentPath, newFullPath);
                _logger.LogWarning("[MR] DRY RUN: No actual rename performed. Disable Dry Run mode to perform actual renames.");
                return false;
            }

            _logger.LogInformation("[MR] === Attempting Actual Rename ===");
            _logger.LogInformation("[MR] From: {From}", currentPath);
            _logger.LogInformation("[MR] To: {To}", newFullPath);

            // #region agent log
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:74", message = "ACTUAL RENAME starting", data = new { from = currentPath, to = newFullPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            // #endregion
            
            Directory.Move(currentPath, newFullPath);
            
            // #region agent log
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:76", message = "RENAME SUCCESS", data = new { from = currentPath, to = newFullPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            // #endregion
            
            _logger.LogInformation("[MR] ✓✓✓ SUCCESS: Folder renamed successfully!");
            _logger.LogInformation("[MR] Old: {From}", currentPath);
            _logger.LogInformation("[MR] New: {To}", newFullPath);
            if (!Directory.Exists(newFullPath))
            {
                _logger.LogError("[MR] ✗ Verification FAILED: New folder does not exist after rename!");
                return false;
            }
            _logger.LogInformation("[MR] ✓ Verification: New folder exists");
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            // #region agent log
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:80", message = "RENAME FAILED - UnauthorizedAccess", data = new { from = currentPath, to = newFullPath, error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            // #endregion
            _logger.LogError(ex, "[MR] ERROR: UnauthorizedAccessException - Permission denied. From: {From}, To: {To}", currentPath, newFullPath);
            return false;
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogError(ex, "[MR] ERROR: DirectoryNotFoundException - Source directory not found. Path: {Path}", currentPath);
            return false;
        }
        catch (IOException ex)
        {
            // #region agent log
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:80", message = "RENAME FAILED - IOException", data = new { from = currentPath, to = newFullPath, error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            // #endregion
            _logger.LogError(ex, "[MR] ERROR: IOException - File system error. From: {From}, To: {To}, Message: {Message}", currentPath, newFullPath, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // #region agent log
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:80", message = "RENAME FAILED", data = new { from = currentPath, to = newFullPath, error = ex.Message, exceptionType = ex.GetType().Name }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            // #endregion
            _logger.LogError(ex, "[MR] ERROR: Unexpected exception during rename. Type: {Type}, Message: {Message}", ex.GetType().Name, ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
            return false;
        }
    }

    /// <summary>
    /// Decides whether to skip series rename because folder name already matches (and provider IDs agree).
    /// Returns true to skip, false to proceed (either name differs or we should force re-rename for provider ID mismatch).
    /// </summary>
    private bool ShouldSkipSeriesRenameBecauseNameMatches(Series series, DirectoryInfo currentDir, string desiredFolderName)
    {
        if (!string.Equals(currentDir.Name, desiredFolderName, StringComparison.OrdinalIgnoreCase))
            return false;

        var providerIdFromFolder = ExtractProviderIdFromFolderName(currentDir.Name);
        var providerIdFromMetadata = ExtractProviderIdFromDesiredName(desiredFolderName);
        bool folderIdMatchesAnyMetadataId = FolderProviderIdMatchesAnyMetadataId(series, providerIdFromFolder);

        if (!string.IsNullOrWhiteSpace(providerIdFromFolder) &&
            !string.IsNullOrWhiteSpace(providerIdFromMetadata) &&
            !string.Equals(providerIdFromFolder, providerIdFromMetadata, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[MR] ⚠️ Folder name matches but provider ID mismatch detected!");
            _logger.LogWarning("[MR] ⚠️ Provider ID in folder name: {FolderId}", providerIdFromFolder);
            _logger.LogWarning("[MR] ⚠️ Provider ID in metadata (selected): {MetadataId}", providerIdFromMetadata);
            _logger.LogWarning("[MR] ⚠️ Forcing re-rename to correct provider ID mismatch");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(providerIdFromFolder) && !folderIdMatchesAnyMetadataId)
        {
            _logger.LogWarning("[MR] ⚠️ Folder name matches but provider ID in folder doesn't match ANY metadata provider ID!");
            _logger.LogWarning("[MR] ⚠️ Provider ID in folder name: {FolderId}", providerIdFromFolder);
            _logger.LogWarning("[MR] ⚠️ All metadata provider IDs: {AllIds}",
                series.ProviderIds != null
                    ? string.Join(", ", series.ProviderIds.Select(kv => $"{kv.Key.ToLowerInvariant()}-{kv.Value}"))
                    : "NONE");
            _logger.LogWarning("[MR] ⚠️ Forcing re-rename to correct provider ID mismatch");
            return false;
        }

        _logger.LogInformation("[MR] SKIP: Folder name already matches desired name. Current: {Current}, Desired: {Desired}", currentDir.Name, desiredFolderName);
        if (!string.IsNullOrWhiteSpace(providerIdFromFolder) && !string.IsNullOrWhiteSpace(providerIdFromMetadata))
            _logger.LogInformation("[MR] Provider IDs match: {Id}", providerIdFromFolder);
        return true;
    }

    private static bool FolderProviderIdMatchesAnyMetadataId(Series series, string? providerIdFromFolder)
    {
        if (string.IsNullOrWhiteSpace(providerIdFromFolder) || series.ProviderIds == null || series.ProviderIds.Count == 0)
            return false;
        foreach (var kv in series.ProviderIds)
        {
            var metadataProviderId = $"{kv.Key.ToLowerInvariant()}-{kv.Value}";
            if (string.Equals(providerIdFromFolder, metadataProviderId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Attempts to rename a season folder to the desired name.
    /// </summary>
    /// <param name="season">The season entity.</param>
    /// <param name="desiredFolderName">The desired folder name.</param>
    /// <param name="dryRun">Whether to perform a dry run (log only, no actual rename).</param>
    public void TryRenameSeasonFolder(MediaBrowser.Controller.Entities.TV.Season season, string desiredFolderName, bool dryRun)
    {
        string currentPath = string.Empty;
        string newFullPath = string.Empty;

        try
        {
            // Safeguard: reject null season
            if (season == null)
            {
                _logger.LogError("[MR] [SAFEGUARD] TryRenameSeasonFolder: season is null. Aborting.");
                return;
            }

            // Safeguard: desired folder name must be valid
            if (!SafeName.IsValidFolderOrFileName(desiredFolderName))
            {
                _logger.LogWarning("[MR] [SAFEGUARD] TryRenameSeasonFolder: desired folder name is null, empty, or invalid. Season: {Name}, Id: {Id}, SeasonNumber: {SeasonNumber}. Aborting.",
                    season.Name ?? "NULL", season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                return;
            }

            desiredFolderName = SafeName.SanitizeFileName(desiredFolderName?.Trim() ?? string.Empty);
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-SERVICE-ENTRY] === TryRenameSeasonFolder Called ===");
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-SERVICE-ENTRY] Season: {Name}, ID: {Id}, Season Number: {SeasonNumber}", 
                season.Name ?? "NULL", season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-SERVICE-ENTRY] Desired Folder Name: {Desired}", desiredFolderName);
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-SERVICE-ENTRY] Dry Run: {DryRun}", dryRun);
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-SERVICE-ENTRY] Series: {SeriesName}, SeriesId: {SeriesId}",
                season.Series?.Name ?? "NULL", season.Series?.Id.ToString() ?? "NULL");

            currentPath = season.Path;
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                _logger.LogWarning("[MR] [DEBUG] [SEASON-RENAME-SKIP-NO-PATH] SKIP: Season.Path is null or empty. SeasonId={Id}, SeasonNumber={SeasonNumber}", 
                    season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                return;
            }

            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-PATH-VALIDATION] Current Path: {Path}", currentPath);

            var currentDir = new DirectoryInfo(currentPath);
            if (!currentDir.Exists)
            {
                _logger.LogError("[MR] [DEBUG] [SEASON-RENAME-ERROR-PATH-NOT-EXISTS] ERROR: Season folder does not exist: {Path}, SeasonId={Id}, SeasonNumber={SeasonNumber}", 
                    currentPath, season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                return;
            }

            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-PATH-VALIDATION] Current Directory Name: {Name}", currentDir.Name);
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-PATH-VALIDATION] Current Directory Exists: {Exists}", currentDir.Exists);
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-PATH-VALIDATION] Current Directory FullName: {FullName}", currentDir.FullName);

            var parent = currentDir.Parent;
            if (parent == null)
            {
                _logger.LogError("[MR] [DEBUG] [SEASON-RENAME-ERROR-NO-PARENT] ERROR: Cannot rename root directory: {Path}, SeasonId={Id}", 
                    currentPath, season.Id);
                return;
            }

            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-PATH-VALIDATION] Parent Directory: {Parent}, Exists: {Exists}", parent.FullName, parent.Exists);

            if (string.Equals(currentDir.Name, desiredFolderName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-SKIP-ALREADY-MATCHES] SKIP: Folder name already matches desired name. Current: {Current}, Desired: {Desired}, SeasonId={Id}, SeasonNumber={SeasonNumber}", 
                    currentDir.Name, desiredFolderName, season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                return;
            }
            
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-COMPARISON] Folder names differ - proceeding with rename. Current: '{Current}', Desired: '{Desired}'", 
                currentDir.Name, desiredFolderName);

            newFullPath = Path.Combine(parent.FullName, desiredFolderName);
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-PATH-CALCULATION] New Full Path: {NewPath}", newFullPath);
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-PATH-CALCULATION] Parent exists: {Exists}, Parent is writable: {Writable}", 
                parent.Exists, parent.Exists && (new DirectoryInfo(parent.FullName).Attributes & System.IO.FileAttributes.ReadOnly) == 0);

            if (Directory.Exists(newFullPath))
            {
                _logger.LogError("[MR] [DEBUG] [SEASON-RENAME-ERROR-TARGET-EXISTS] ERROR: Target folder already exists. Cannot rename. From: {From}, To: {To}, SeasonId={Id}, SeasonNumber={SeasonNumber}", 
                    currentPath, newFullPath, season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                return;
            }

            if (dryRun)
            {
                _logger.LogWarning("[MR] [DEBUG] [SEASON-RENAME-DRY-RUN] DRY RUN MODE: Would rename {From} -> {To}, SeasonId={Id}, SeasonNumber={SeasonNumber}", 
                    currentPath, newFullPath, season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                _logger.LogWarning("[MR] [DEBUG] [SEASON-RENAME-DRY-RUN] DRY RUN: No actual rename performed. Disable Dry Run mode to perform actual renames.");
                return;
            }

            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-ATTEMPT] === Attempting Actual Rename ===");
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-ATTEMPT] From: {From}", currentPath);
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-ATTEMPT] To: {To}", newFullPath);
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-ATTEMPT] SeasonId={Id}, SeasonNumber={SeasonNumber}", 
                season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            
            try
            {
                Directory.Move(currentPath, newFullPath);
                
                _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-SUCCESS] ✓✓✓ SUCCESS: Season folder renamed successfully!");
                _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-SUCCESS] Old: {From}", currentPath);
                _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-SUCCESS] New: {To}", newFullPath);
                _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-SUCCESS] SeasonId={Id}, SeasonNumber={SeasonNumber}", 
                    season.Id, season.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                
                LogSeasonRenameVerification(newFullPath, season);
            }
            catch (Exception renameEx)
            {
                _logger.LogError(renameEx, "[MR] [DEBUG] [SEASON-RENAME-ERROR-DURING-MOVE] ERROR during Directory.Move: Exception Type={Type}, Message={Message}, SeasonId={Id}, SeasonNumber={SeasonNumber}", 
                    renameEx.GetType().FullName, renameEx.Message, season?.Id, season?.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
                _logger.LogError("[MR] [DEBUG] [SEASON-RENAME-ERROR-DURING-MOVE] Stack Trace: {StackTrace}", renameEx.StackTrace ?? "N/A");
                throw; // Re-throw to be caught by outer catch blocks
            }
        }
        catch (Exception ex)
        {
            LogSeasonRenameException(ex, currentPath, newFullPath, season);
        }
    }

    private void LogSeasonRenameException(Exception ex, string currentPath, string newFullPath, MediaBrowser.Controller.Entities.TV.Season season)
    {
        var seasonNum = season?.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
        if (ex is UnauthorizedAccessException)
        {
            _logger.LogError(ex, "[MR] [DEBUG] [SEASON-RENAME-ERROR-UNAUTHORIZED] ERROR: UnauthorizedAccessException - Permission denied. From: {From}, To: {To}, SeasonId={Id}, SeasonNumber={SeasonNumber}",
                currentPath, newFullPath, season?.Id, seasonNum);
            _logger.LogError("[MR] [DEBUG] [SEASON-RENAME-ERROR-UNAUTHORIZED] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
        }
        else if (ex is DirectoryNotFoundException)
        {
            _logger.LogError(ex, "[MR] [DEBUG] [SEASON-RENAME-ERROR-DIRECTORY-NOT-FOUND] ERROR: DirectoryNotFoundException - Source directory not found. Path: {Path}, SeasonId={Id}, SeasonNumber={SeasonNumber}",
                currentPath, season?.Id, seasonNum);
            _logger.LogError("[MR] [DEBUG] [SEASON-RENAME-ERROR-DIRECTORY-NOT-FOUND] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
        }
        else if (ex is IOException ioEx)
        {
            _logger.LogError(ioEx, "[MR] [DEBUG] [SEASON-RENAME-ERROR-IO] ERROR: IOException - File system error. From: {From}, To: {To}, Message: {Message}, SeasonId={Id}, SeasonNumber={SeasonNumber}",
                currentPath, newFullPath, ioEx.Message, season?.Id, seasonNum);
            _logger.LogError("[MR] [DEBUG] [SEASON-RENAME-ERROR-IO] Stack Trace: {StackTrace}", ioEx.StackTrace ?? "N/A");
            _logger.LogError("[MR] [DEBUG] [SEASON-RENAME-ERROR-IO] Inner Exception: {InnerException}", ioEx.InnerException?.Message ?? "N/A");
        }
        else
        {
            _logger.LogError(ex, "[MR] [DEBUG] [SEASON-RENAME-ERROR-UNEXPECTED] ERROR: Unexpected exception during rename. Type: {Type}, Message: {Message}, SeasonId={Id}, SeasonNumber={SeasonNumber}",
                ex.GetType().FullName, ex.Message, season?.Id, seasonNum);
            _logger.LogError("[MR] [DEBUG] [SEASON-RENAME-ERROR-UNEXPECTED] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
            _logger.LogError("[MR] [DEBUG] [SEASON-RENAME-ERROR-UNEXPECTED] Inner Exception: {InnerException}", ex.InnerException?.Message ?? "N/A");
        }
    }

    private void LogSeasonRenameVerification(string newFullPath, MediaBrowser.Controller.Entities.TV.Season season)
    {
        if (!Directory.Exists(newFullPath))
        {
            _logger.LogError("[MR] [DEBUG] [SEASON-RENAME-VERIFICATION-FAILED] ✗ Verification FAILED: New folder does not exist after rename! Expected: {Path}, SeasonId={Id}",
                newFullPath, season?.Id);
        }
        else
        {
            _logger.LogInformation("[MR] [DEBUG] [SEASON-RENAME-VERIFICATION] ✓ Verification: New folder exists at {Path}", newFullPath);
        }
    }

    /// <summary>
    /// Attempts to rename an episode file to the desired name.
    /// </summary>
    /// <param name="episode">The episode entity.</param>
    /// <param name="desiredFileName">The desired file name (without extension).</param>
    /// <param name="fileExtension">The file extension (including the dot).</param>
    /// <param name="dryRun">Whether to perform a dry run (log only, no actual rename).</param>
    /// <param name="overridePath">Optional path to use instead of episode.Path (useful when file was moved).</param>
    public void TryRenameEpisodeFile(Episode episode, string desiredFileName, string fileExtension, bool dryRun, string overridePath = null)
    {
        string currentPath = string.Empty;
        string newFullPath = string.Empty;

        try
        {
            if (episode == null)
            {
                _logger.LogError("[MR] [SAFEGUARD] TryRenameEpisodeFile: episode is null. Aborting.");
                return;
            }
            if (!SafeName.IsValidFolderOrFileName(desiredFileName))
            {
                _logger.LogWarning("[MR] [SAFEGUARD] TryRenameEpisodeFile: desired file name is null, empty, or invalid. EpisodeId: {Id}, Name: {Name}. Aborting.",
                    episode.Id, episode.Name ?? "NULL");
                return;
            }

            NormalizeEpisodeFileInput(ref desiredFileName, ref fileExtension, episode.Path);
            var seasonNumber = episode.ParentIndexNumber;
            var isSeason2Plus = seasonNumber.HasValue && seasonNumber.Value >= 2;
            LogEpisodeRenameEntry(episode, desiredFileName, fileExtension, dryRun, overridePath, isSeason2Plus, seasonNumber);

            if (!TryResolveEpisodePaths(episode, overridePath, desiredFileName, fileExtension, out currentPath, out newFullPath))
                return;
            if (!SafeName.DesiredEpisodeFileNameMatchesMetadata(desiredFileName, episode.ParentIndexNumber, episode.IndexNumber))
            {
                _logger.LogError("[MR] [SAFEGUARD] TryRenameEpisodeFile: desired filename SxxExx does not match episode metadata. Desired: '{Desired}', Metadata S{Season}E{Episode}. Aborting.",
                    desiredFileName,
                    episode.ParentIndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
                    episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
                return;
            }
            if (CheckEpisodeTargetConflict(episode, currentPath, newFullPath, desiredFileName, fileExtension, isSeason2Plus, seasonNumber))
                return;
            if (dryRun)
            {
                _logger.LogWarning("[MR] DRY RUN MODE: Would rename {From} -> {To}", currentPath, newFullPath);
                _logger.LogWarning("[MR] DRY RUN: No actual rename performed. Disable Dry Run mode to perform actual renames.");
                return;
            }

            ExecuteEpisodeFileMove(episode, currentPath, newFullPath, isSeason2Plus, seasonNumber);
            LogEpisodeRenameVerification(newFullPath);
        }
        catch (Exception ex)
        {
            LogEpisodeRenameException(ex, currentPath, newFullPath, episode);
        }
    }

    private static void NormalizeEpisodeFileInput(ref string desiredFileName, ref string fileExtension, string episodePath)
    {
        desiredFileName = SafeName.SanitizeFileName(desiredFileName?.Trim() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileExtension))
            fileExtension = Path.GetExtension(episodePath ?? ".mkv");
        if (!fileExtension.StartsWith(".", StringComparison.Ordinal))
            fileExtension = "." + fileExtension;
    }

    private bool TryResolveEpisodePaths(Episode episode, string overridePath, string desiredFileName, string fileExtension, out string currentPath, out string newFullPath)
    {
        currentPath = overridePath ?? episode.Path;
        newFullPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(overridePath))
            _logger.LogInformation("[MR] Using override path (file was moved): {Path}", overridePath);
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            _logger.LogWarning("[MR] SKIP: Episode.Path is null or empty");
            return false;
        }
        _logger.LogInformation("[MR] Current Path: {Path}", currentPath);
        var currentFile = new FileInfo(currentPath);
        if (!currentFile.Exists)
        {
            _logger.LogError("[MR] ERROR: Episode file does not exist: {Path}", currentPath);
            return false;
        }
        _logger.LogInformation("[MR] Current File Name: {Name}", currentFile.Name);
        _logger.LogInformation("[MR] Current File Exists: {Exists}", currentFile.Exists);
        var directory = currentFile.Directory;
        if (directory == null)
        {
            _logger.LogError("[MR] ERROR: Cannot determine directory for file: {Path}", currentPath);
            return false;
        }
        _logger.LogInformation("[MR] Directory: {Directory}", directory.FullName);
        var currentFileNameWithoutExt = Path.GetFileNameWithoutExtension(currentFile.Name);
        var newFileName = desiredFileName + fileExtension;
        if (string.Equals(currentFileNameWithoutExt, desiredFileName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("[MR] SKIP: File name already matches desired name. Current: {Current}, Desired: {Desired}",
                currentFileNameWithoutExt, desiredFileName);
            return false;
        }
        newFullPath = Path.Combine(directory.FullName, newFileName);
        _logger.LogInformation("[MR] New Full Path: {NewPath}", newFullPath);
        return true;
    }

    private void LogEpisodeRenameEntry(Episode episode, string desiredFileName, string fileExtension, bool dryRun, string overridePath, bool isSeason2Plus, int? seasonNumber)
    {
        if (isSeason2Plus)
        {
            _logger.LogWarning("[MR] === TryRenameEpisodeFile Called (Season 2+) ===");
            _logger.LogWarning("[MR] Episode: {Name}, ID: {Id}", episode.Name, episode.Id);
            _logger.LogWarning("[MR] Season: {Season}, Episode: {Episode} (Season2Plus={IsSeason2Plus})", seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", isSeason2Plus);
            _logger.LogWarning("[MR] Desired File Name: {Desired}", desiredFileName + fileExtension);
            _logger.LogWarning(DryRunLogMessage, dryRun);
        }
        else
        {
            _logger.LogInformation("[MR] === TryRenameEpisodeFile Called ===");
            _logger.LogInformation("[MR] Episode: {Name}, ID: {Id}", episode.Name, episode.Id);
            _logger.LogInformation("[MR] Season: {Season}, Episode: {Episode}", seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
            _logger.LogInformation("[MR] Desired File Name: {Desired}", desiredFileName + fileExtension);
            _logger.LogInformation(DryRunLogMessage, dryRun);
        }
        if (isSeason2Plus)
        {
            try
            {
                var logData = new { runId = "run1", hypothesisId = "MULTI-SEASON-RENAME-ENTRY", location = "PathRenameService.cs", message = "TryRenameEpisodeFile called for Season 2+ episode", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episodeNumber = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", desiredFileName = desiredFileName + fileExtension, dryRun = dryRun, currentPath = overridePath ?? episode.Path ?? "NULL" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
            }
            catch (Exception logEx) { _logger.LogError(logEx, "[MR] [DEBUG] [MULTI-SEASON-RENAME-ENTRY] ERROR logging: {Error}", logEx.Message); }
        }
    }

    private bool CheckEpisodeTargetConflict(Episode episode, string currentPath, string newFullPath, string desiredFileName, string fileExtension, bool isSeason2Plus, int? seasonNumber)
    {
        if (!File.Exists(newFullPath))
            return false;
        var targetFileInfo = new FileInfo(newFullPath);
        var sourceFileInfo = new FileInfo(currentPath);
        var isSameFile = string.Equals(currentPath, newFullPath, StringComparison.OrdinalIgnoreCase);
        var targetFileSize = targetFileInfo.Exists ? targetFileInfo.Length : -1;
        var sourceFileSize = sourceFileInfo.Exists ? sourceFileInfo.Length : -1;
        var filesAreSame = isSameFile || (targetFileSize == sourceFileSize && targetFileSize > 0);
        DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { runId = "run1", hypothesisId = "DUPLICATE-TARGET-FILENAME", location = "PathRenameService.cs", message = "Target file already exists - checking if same file", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episodeNumber = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", isSeason2Plus = isSeason2Plus, sourcePath = currentPath, targetPath = newFullPath, isSameFile = isSameFile, sourceFileSize = sourceFileSize, targetFileSize = targetFileSize, filesAreSame = filesAreSame, desiredFileName = desiredFileName + fileExtension }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        if (filesAreSame)
        {
            _logger.LogInformation("[MR] Target file already exists and is the same as source. File already renamed correctly. From: {From}, To: {To}", currentPath, newFullPath);
            return true;
        }
        _logger.LogError("[MR] ERROR: Target file already exists with different content. This indicates duplicate episode metadata in Jellyfin (multiple episodes mapped to the same episode number/title). From: {From}, To: {To}, EpisodeId: {EpisodeId}, Season: {Season}, Episode: {Episode}", currentPath, newFullPath, episode.Id, seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL");
        _logger.LogError("[MR] Please fix the episode metadata in Jellyfin (correct episode numbers and titles) and try again.");
        return true;
    }

    private void ExecuteEpisodeFileMove(Episode episode, string currentPath, string newFullPath, bool isSeason2Plus, int? seasonNumber)
    {
        if (isSeason2Plus)
        {
            _logger.LogWarning("[MR] === Attempting Actual Rename (Season 2+) ===");
            _logger.LogWarning("[MR] From: {From}", currentPath);
            _logger.LogWarning("[MR] To: {To}", newFullPath);
            try
            {
                var logData = new { runId = "run1", hypothesisId = "MULTI-SEASON-RENAME-ATTEMPT", location = "PathRenameService.cs", message = "About to rename Season 2+ episode file", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episodeNumber = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", fromPath = currentPath, toPath = newFullPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
            }
            catch (Exception logEx) { _logger.LogError(logEx, "[MR] [DEBUG] [MULTI-SEASON-RENAME-ATTEMPT] ERROR logging: {Error}", logEx.Message); }
        }
        else
        {
            _logger.LogInformation("[MR] === Attempting Actual Rename ===");
            _logger.LogInformation("[MR] From: {From}", currentPath);
            _logger.LogInformation("[MR] To: {To}", newFullPath);
        }
        File.Move(currentPath, newFullPath);
        if (isSeason2Plus)
        {
            _logger.LogWarning("[MR] ✓✓✓ SUCCESS: Season 2+ episode file renamed successfully!");
            _logger.LogWarning("[MR] Old: {From}", currentPath);
            _logger.LogWarning("[MR] New: {To}", newFullPath);
            try
            {
                var logData = new { runId = "run1", hypothesisId = "MULTI-SEASON-RENAME-SUCCESS", location = "PathRenameService.cs", message = "Season 2+ episode file renamed successfully", data = new { episodeId = episode.Id.ToString(), episodeName = episode.Name ?? "NULL", seasonNumber = seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", episodeNumber = episode.IndexNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", fromPath = currentPath, toPath = newFullPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
            }
            catch (Exception logEx) { _logger.LogError(logEx, "[MR] [DEBUG] [MULTI-SEASON-RENAME-SUCCESS] ERROR logging: {Error}", logEx.Message); }
        }
        else
        {
            _logger.LogInformation("[MR] ✓✓✓ SUCCESS: Episode file renamed successfully!");
            _logger.LogInformation("[MR] Old: {From}", currentPath);
            _logger.LogInformation("[MR] New: {To}", newFullPath);
        }
    }

    private void LogEpisodeRenameVerification(string newFullPath)
    {
        if (File.Exists(newFullPath))
            _logger.LogInformation("[MR] ✓ Verification: New file exists");
        else
            _logger.LogError("[MR] ✗ Verification FAILED: New file does not exist after rename!");
    }

    private void LogEpisodeRenameException(Exception ex, string currentPath, string newFullPath, Episode episode)
    {
        var seasonNumber = episode?.ParentIndexNumber;
        var isSeason2Plus = seasonNumber.HasValue && seasonNumber.Value >= 2;
        if (ex is UnauthorizedAccessException)
            _logger.LogError(ex, "[MR] ERROR: UnauthorizedAccessException - Permission denied. From: {From}, To: {To} (Season2Plus={IsSeason2Plus})", currentPath, newFullPath, isSeason2Plus);
        else if (ex is FileNotFoundException)
            _logger.LogError(ex, "[MR] ERROR: FileNotFoundException - Source file not found. Path: {Path} (Season2Plus={IsSeason2Plus})", currentPath, isSeason2Plus);
        else if (ex is IOException ioEx)
            _logger.LogError(ioEx, "[MR] ERROR: IOException - File system error. From: {From}, To: {To}, Message: {Message} (Season2Plus={IsSeason2Plus})", currentPath, newFullPath, ioEx.Message, isSeason2Plus);
        else
        {
            _logger.LogError(ex, "[MR] ERROR: Unexpected exception during rename. Type: {Type}, Message: {Message} (Season2Plus={IsSeason2Plus})", ex.GetType().Name, ex.Message, isSeason2Plus);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
        }
        if (isSeason2Plus)
        {
            try
            {
                var logData = new { runId = "run1", hypothesisId = "MULTI-SEASON-RENAME-ERROR", location = "PathRenameService.cs", message = "Exception during Season 2+ episode rename", data = new { episodeId = episode?.Id.ToString() ?? "NULL", episodeName = episode?.Name ?? "NULL", seasonNumber = seasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL", exceptionType = ex.GetType().FullName, exceptionMessage = ex.Message, fromPath = currentPath, toPath = newFullPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
            }
            catch { }
        }
    }

    /// <summary>
    /// Attempts to rename a movie folder to the desired name.
    /// </summary>
    /// <param name="movie">The movie entity.</param>
    /// <param name="desiredFolderName">The desired folder name.</param>
    /// <param name="dryRun">Whether to perform a dry run (log only, no actual rename).</param>
    /// <returns>True if the rename was successful, false otherwise.</returns>
    public bool TryRenameMovieFolder(Movie movie, string desiredFolderName, bool dryRun)
    {
        string currentPath = string.Empty;
        string newFullPath = string.Empty;

        try
        {
            // Safeguard: reject null movie
            if (movie == null)
            {
                _logger.LogError("[MR] [SAFEGUARD] TryRenameMovieFolder: movie is null. Aborting.");
                return false;
            }

            // Safeguard: desired folder name must be valid
            if (!SafeName.IsValidFolderOrFileName(desiredFolderName))
            {
                _logger.LogWarning("[MR] [SAFEGUARD] TryRenameMovieFolder: desired folder name is null, empty, or invalid. Movie: {Name}, Id: {Id}. Aborting.",
                    movie.Name ?? "NULL", movie.Id);
                return false;
            }

            desiredFolderName = SafeName.SanitizeFileName(desiredFolderName?.Trim() ?? string.Empty);
            _logger.LogInformation("[MR] === TryRenameMovieFolder Called ===");
            _logger.LogInformation("[MR] Movie: {Name}, ID: {Id}", movie.Name, movie.Id);
            _logger.LogInformation("[MR] Desired Folder Name: {Desired}", desiredFolderName);
            _logger.LogInformation(DryRunLogMessage, dryRun);

            var movieFilePath = movie.Path;
            if (string.IsNullOrWhiteSpace(movieFilePath))
            {
                _logger.LogWarning("[MR] SKIP: Movie.Path is null or empty");
                return false;
            }

            // For movies, Path points to the movie file, not the folder
            // We need to get the directory containing the movie file
            currentPath = Path.GetDirectoryName(movieFilePath);
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                _logger.LogWarning("[MR] SKIP: Cannot determine movie directory from path: {Path}", movieFilePath);
                return false;
            }

            _logger.LogInformation("[MR] Current Movie Directory: {Path}", currentPath);

            var currentDir = new DirectoryInfo(currentPath);
            if (!currentDir.Exists)
            {
                _logger.LogError("[MR] ERROR: Movie directory does not exist: {Path}", currentPath);
                return false;
            }

            _logger.LogInformation("[MR] Current Directory Name: {Name}", currentDir.Name);
            _logger.LogInformation("[MR] Current Directory Exists: {Exists}", currentDir.Exists);

            var parent = currentDir.Parent;
            if (parent == null)
            {
                _logger.LogError("[MR] ERROR: Cannot rename root directory: {Path}", currentPath);
                return false;
            }

            _logger.LogInformation("[MR] Parent Directory: {Parent}", parent.FullName);

            if (string.Equals(currentDir.Name, desiredFolderName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[MR] SKIP: Folder name already matches desired name. Current: {Current}, Desired: {Desired}", currentDir.Name, desiredFolderName);
                return false;
            }

            newFullPath = Path.Combine(parent.FullName, desiredFolderName);
            _logger.LogInformation("[MR] New Full Path: {NewPath}", newFullPath);

            if (Directory.Exists(newFullPath))
            {
                _logger.LogError("[MR] ERROR: Target folder already exists. Cannot rename. From: {From}, To: {To}", currentPath, newFullPath);
                return false;
            }

            if (dryRun)
            {
                _logger.LogWarning("[MR] DRY RUN MODE: Would rename {From} -> {To}", currentPath, newFullPath);
                _logger.LogWarning("[MR] DRY RUN: No actual rename performed. Disable Dry Run mode to perform actual renames.");
                return false;
            }

            _logger.LogInformation("[MR] === Attempting Actual Rename ===");
            _logger.LogInformation("[MR] From: {From}", currentPath);
            _logger.LogInformation("[MR] To: {To}", newFullPath);

            Directory.Move(currentPath, newFullPath);

            _logger.LogInformation("[MR] ✓✓✓ SUCCESS: Movie folder renamed successfully!");
            _logger.LogInformation("[MR] Old: {From}", currentPath);
            _logger.LogInformation("[MR] New: {To}", newFullPath);

            // Verify the rename
            if (Directory.Exists(newFullPath))
            {
                _logger.LogInformation("[MR] ✓ Verification: New folder exists");
                return true;
            }
            else
            {
                _logger.LogError("[MR] ✗ Verification FAILED: New folder does not exist after rename!");
                return false;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "[MR] ERROR: UnauthorizedAccessException - Permission denied. From: {From}, To: {To}", currentPath, newFullPath);
            return false;
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogError(ex, "[MR] ERROR: DirectoryNotFoundException - Source directory not found. Path: {Path}", currentPath);
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "[MR] ERROR: IOException - File system error. From: {From}, To: {To}, Message: {Message}", currentPath, newFullPath, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] ERROR: Unexpected exception in TryRenameMovieFolder. From: {From}, To: {To}, Message: {Message}", currentPath, newFullPath, ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
            return false;
        }
    }

    /// <summary>
    /// Extracts the provider ID from a folder name (e.g., "Series Name (2020) [tmdb-12345]" -> "tmdb-12345").
    /// </summary>
    /// <param name="folderName">The folder name to extract from.</param>
    /// <returns>The provider ID string (e.g., "tmdb-12345") or null if not found.</returns>
    private static string? ExtractProviderIdFromFolderName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        // Pattern: [provider-id] or [provider-id] at the end of the folder name
        var match = System.Text.RegularExpressions.Regex.Match(folderName, @"\[([a-zA-Z]+)-([^\]]+)\]");
        if (match.Success && match.Groups.Count >= 3)
        {
            var provider = match.Groups[1].Value.ToLowerInvariant();
            var id = match.Groups[2].Value;
            return $"{provider}-{id}";
        }

        return null;
    }

    /// <summary>
    /// Extracts the provider ID from a desired folder name (same as ExtractProviderIdFromFolderName).
    /// </summary>
    /// <param name="desiredFolderName">The desired folder name to extract from.</param>
    /// <returns>The provider ID string (e.g., "tmdb-12345") or null if not found.</returns>
    private static string? ExtractProviderIdFromDesiredName(string desiredFolderName)
    {
        return ExtractProviderIdFromFolderName(desiredFolderName);
    }
}
