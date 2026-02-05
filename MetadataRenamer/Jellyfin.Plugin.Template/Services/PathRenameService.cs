using System;
using System.IO;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
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
    /// <returns>True if the rename was successful, false otherwise.</returns>
    public bool TryRenameSeriesFolder(Series series, string desiredFolderName, bool dryRun)
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
                // #region agent log
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:66", message = "DRY RUN - not renaming", data = new { from = currentPath, to = newFullPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                // #endregion
                _logger.LogWarning("[MR] DRY RUN MODE: Would rename {From} -> {To}", currentPath, newFullPath);
                _logger.LogWarning("[MR] DRY RUN: No actual rename performed. Disable Dry Run mode to perform actual renames.");
                return false;
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
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:80", message = "RENAME FAILED - UnauthorizedAccess", data = new { from = currentPath, to = newFullPath, error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
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
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:80", message = "RENAME FAILED - IOException", data = new { from = currentPath, to = newFullPath, error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            _logger.LogError(ex, "[MR] ERROR: IOException - File system error. From: {From}, To: {To}, Message: {Message}", currentPath, newFullPath, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "D", location = "PathRenameService.cs:80", message = "RENAME FAILED", data = new { from = currentPath, to = newFullPath, error = ex.Message, exceptionType = ex.GetType().Name }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            _logger.LogError(ex, "[MR] ERROR: Unexpected exception during rename. Type: {Type}, Message: {Message}", ex.GetType().Name, ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
            return false;
        }
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
            _logger.LogInformation("[MR] === TryRenameSeasonFolder Called ===");
            _logger.LogInformation("[MR] Season: {Name}, ID: {Id}, Season Number: {SeasonNumber}", season.Name, season.Id, season.IndexNumber);
            _logger.LogInformation("[MR] Desired Folder Name: {Desired}", desiredFolderName);
            _logger.LogInformation("[MR] Dry Run: {DryRun}", dryRun);

            currentPath = season.Path;
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                _logger.LogWarning("[MR] SKIP: Season.Path is null or empty");
                return;
            }

            _logger.LogInformation("[MR] Current Path: {Path}", currentPath);

            var currentDir = new DirectoryInfo(currentPath);
            if (!currentDir.Exists)
            {
                _logger.LogError("[MR] ERROR: Season folder does not exist: {Path}", currentPath);
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
                _logger.LogWarning("[MR] DRY RUN MODE: Would rename {From} -> {To}", currentPath, newFullPath);
                _logger.LogWarning("[MR] DRY RUN: No actual rename performed. Disable Dry Run mode to perform actual renames.");
                return;
            }

            _logger.LogInformation("[MR] === Attempting Actual Rename ===");
            _logger.LogInformation("[MR] From: {From}", currentPath);
            _logger.LogInformation("[MR] To: {To}", newFullPath);
            
            Directory.Move(currentPath, newFullPath);
            
            _logger.LogInformation("[MR] ✓✓✓ SUCCESS: Season folder renamed successfully!");
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
            _logger.LogError(ex, "[MR] ERROR: UnauthorizedAccessException - Permission denied. From: {From}, To: {To}", currentPath, newFullPath);
        }
        catch (DirectoryNotFoundException ex)
        {
            _logger.LogError(ex, "[MR] ERROR: DirectoryNotFoundException - Source directory not found. Path: {Path}", currentPath);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "[MR] ERROR: IOException - File system error. From: {From}, To: {To}, Message: {Message}", currentPath, newFullPath, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] ERROR: Unexpected exception during rename. Type: {Type}, Message: {Message}", ex.GetType().Name, ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
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
            _logger.LogInformation("[MR] === TryRenameEpisodeFile Called ===");
            _logger.LogInformation("[MR] Episode: {Name}, ID: {Id}", episode.Name, episode.Id);
            _logger.LogInformation("[MR] Desired File Name: {Desired}{Extension}", desiredFileName, fileExtension);
            _logger.LogInformation("[MR] Dry Run: {DryRun}", dryRun);

            currentPath = overridePath ?? episode.Path;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                _logger.LogInformation("[MR] Using override path (file was moved): {Path}", overridePath);
            }
            if (string.IsNullOrWhiteSpace(currentPath))
            {
                _logger.LogWarning("[MR] SKIP: Episode.Path is null or empty");
                return;
            }

            _logger.LogInformation("[MR] Current Path: {Path}", currentPath);

            var currentFile = new FileInfo(currentPath);
            if (!currentFile.Exists)
            {
                _logger.LogError("[MR] ERROR: Episode file does not exist: {Path}", currentPath);
                return;
            }

            _logger.LogInformation("[MR] Current File Name: {Name}", currentFile.Name);
            _logger.LogInformation("[MR] Current File Exists: {Exists}", currentFile.Exists);

            var directory = currentFile.Directory;
            if (directory == null)
            {
                _logger.LogError("[MR] ERROR: Cannot determine directory for file: {Path}", currentPath);
                return;
            }

            _logger.LogInformation("[MR] Directory: {Directory}", directory.FullName);

            var currentFileNameWithoutExt = Path.GetFileNameWithoutExtension(currentFile.Name);
            var newFileName = desiredFileName + fileExtension;

            if (string.Equals(currentFileNameWithoutExt, desiredFileName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[MR] SKIP: File name already matches desired name. Current: {Current}, Desired: {Desired}", 
                    currentFileNameWithoutExt, desiredFileName);
                return;
            }

            newFullPath = Path.Combine(directory.FullName, newFileName);
            _logger.LogInformation("[MR] New Full Path: {NewPath}", newFullPath);

            if (File.Exists(newFullPath))
            {
                _logger.LogError("[MR] ERROR: Target file already exists. Cannot rename. From: {From}, To: {To}", currentPath, newFullPath);
                return;
            }

            if (dryRun)
            {
                _logger.LogWarning("[MR] DRY RUN MODE: Would rename {From} -> {To}", currentPath, newFullPath);
                _logger.LogWarning("[MR] DRY RUN: No actual rename performed. Disable Dry Run mode to perform actual renames.");
                return;
            }

            _logger.LogInformation("[MR] === Attempting Actual Rename ===");
            _logger.LogInformation("[MR] From: {From}", currentPath);
            _logger.LogInformation("[MR] To: {To}", newFullPath);
            
            File.Move(currentPath, newFullPath);
            
            _logger.LogInformation("[MR] ✓✓✓ SUCCESS: Episode file renamed successfully!");
            _logger.LogInformation("[MR] Old: {From}", currentPath);
            _logger.LogInformation("[MR] New: {To}", newFullPath);
            
            // Verify the rename
            if (File.Exists(newFullPath))
            {
                _logger.LogInformation("[MR] ✓ Verification: New file exists");
            }
            else
            {
                _logger.LogError("[MR] ✗ Verification FAILED: New file does not exist after rename!");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "[MR] ERROR: UnauthorizedAccessException - Permission denied. From: {From}, To: {To}", currentPath, newFullPath);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogError(ex, "[MR] ERROR: FileNotFoundException - Source file not found. Path: {Path}", currentPath);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "[MR] ERROR: IOException - File system error. From: {From}, To: {To}, Message: {Message}", currentPath, newFullPath, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MR] ERROR: Unexpected exception during rename. Type: {Type}, Message: {Message}", ex.GetType().Name, ex.Message);
            _logger.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
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
            _logger.LogInformation("[MR] === TryRenameMovieFolder Called ===");
            _logger.LogInformation("[MR] Movie: {Name}, ID: {Id}", movie.Name, movie.Id);
            _logger.LogInformation("[MR] Desired Folder Name: {Desired}", desiredFolderName);
            _logger.LogInformation("[MR] Dry Run: {DryRun}", dryRun);

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
}
