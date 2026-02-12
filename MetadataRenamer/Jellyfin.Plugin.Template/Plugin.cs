using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MetadataRenamer.Configuration;
using Jellyfin.Plugin.MetadataRenamer.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetadataRenamer;

/// <summary>
/// The main plugin.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IDisposable
{
    private const string DebugSessionId = "debug-session";
    private const string UnknownPathPlaceholder = "UNKNOWN";
    private const string DefaultVersionFallback = "1.0.0.0";
    private const string DeleteOnStartupMarkerFileName = ".deleteOnStartup";
    private const string RunIdStartup = "startup";

    private readonly ILibraryManager _libraryManager;
    private readonly RenameCoordinator _renameCoordinator;
    private readonly PathRenameService _pathRenameService;
    private readonly ILogger<Plugin> _logger;
    private readonly object _disposeLock = new object();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
        {
        try
        {
            _logger = loggerFactory.CreateLogger<Plugin>();
            _logger.LogDebug("[MR] Plugin constructor: Name={Name}, Paths={Paths}", Name, applicationPaths?.DataPath ?? "NULL");
            
            // #region agent log
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "A", location = "Plugin.cs:33", message = "Plugin constructor", data = new { pluginName = Name, pluginId = Id.ToString(), dataPath = applicationPaths?.DataPath ?? "null" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            // #endregion
            
            var pluginsPath = applicationPaths?.PluginsPath ?? UnknownPathPlaceholder;
            var pluginVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? DefaultVersionFallback;
            var versionedPluginPath = System.IO.Path.Combine(pluginsPath, $"{Name}_{pluginVersion}");
            var nonVersionedPluginPath = System.IO.Path.Combine(pluginsPath, Name);
            var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var dllDirectory = !string.IsNullOrWhiteSpace(assemblyLocation) ? System.IO.Path.GetDirectoryName(assemblyLocation) : null;

            LogVersionedFolderDiagnostics(versionedPluginPath);
            var allMarkerPaths = BuildDeleteMarkerPathList(pluginsPath, versionedPluginPath, nonVersionedPluginPath, dllDirectory);
            CheckDeleteMarkerAndThrowIfFound(allMarkerPaths, pluginVersion, pluginsPath);

            _logger.LogInformation("[MR] ✓ No delete marker found - plugin will load normally");
            TryUnblockPluginFiles(dllDirectory ?? versionedPluginPath);
            
            // #region agent log - Uninstall debugging
            try
            {
                var logData = new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "UNINSTALL-A", location = "Plugin.cs:156", message = "No delete marker found - plugin loading", data = new { pluginName = Name, pluginVersion = pluginVersion, pluginsPath = pluginsPath, versionedFolderExists = System.IO.Directory.Exists(versionedPluginPath), nonVersionedFolderExists = System.IO.Directory.Exists(nonVersionedPluginPath) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                DebugLogHelper.SafeAppend( logJson);
            }
            catch
            {
                // Intentionally ignore: debug log write must not impact plugin load.
            }
            // #endregion
            
            Instance = this;
            _libraryManager = libraryManager;

            var logger = loggerFactory.CreateLogger<PathRenameService>();
            _pathRenameService = new PathRenameService(logger);

            var coordinatorLogger = loggerFactory.CreateLogger<RenameCoordinator>();
            _renameCoordinator = new RenameCoordinator(coordinatorLogger, _pathRenameService, _libraryManager);

            _libraryManager.ItemUpdated += OnItemUpdated;
            _logger.LogInformation("[MR] Plugin initialized successfully");
            
            // Log plugin folder path and check for deleteOnStartup marker
            try
            {
                _logger.LogDebug("[MR] Startup check: VersionedFolder={Exists}", System.IO.Directory.Exists(versionedPluginPath));
                
                // #region agent log
                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = RunIdStartup, hypothesisId = "H1", location = "Plugin.cs:71", message = "Plugin constructor called - checking folder existence", data = new { pluginsPath = pluginsPath, versionedFolderPath = versionedPluginPath, versionedFolderExists = System.IO.Directory.Exists(versionedPluginPath), nonVersionedFolderExists = System.IO.Directory.Exists(System.IO.Path.Combine(pluginsPath, Name)) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                // #endregion
                
                if (System.IO.Directory.Exists(versionedPluginPath))
                {
                    var dllPath = System.IO.Path.Combine(versionedPluginPath, "Jellyfin.Plugin.MetadataRenamer.dll");
                    if (System.IO.File.Exists(dllPath))
                    {
                        var dllInfo = new System.IO.FileInfo(dllPath);
                        // #region agent log
                        DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = RunIdStartup, hypothesisId = "H2", location = "Plugin.cs:85", message = "DLL file exists and plugin is being loaded", data = new { dllPath = dllPath, dllSize = dllInfo.Length, lastModified = dllInfo.LastWriteTime.ToString(System.Globalization.CultureInfo.InvariantCulture) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                        // #endregion
                        
                        // Try to check if file is locked (open succeeds = not locked by others)
                        try
                        {
                            using (var fs = System.IO.File.Open(dllPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                            {
                                _ = fs.Length; // Use stream to satisfy S108; verifies file is readable
                            }
                        }
                        catch (Exception fileEx)
                        {
                            _logger.LogWarning(fileEx, "[MR] DLL file appears to be locked: {Message}", fileEx.Message);
                            // #region agent log
                            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = RunIdStartup, hypothesisId = "H3", location = "Plugin.cs:95", message = "DLL file is locked at constructor time", data = new { error = fileEx.Message, dllPath = dllPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                            // #endregion
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MR] Could not check plugin folder path");
                // #region agent log
                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = RunIdStartup, hypothesisId = "H5", location = "Plugin.cs:110", message = "ERROR checking plugin folder path", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                // #endregion
            }
        }
        catch (Exception ex)
        {
            // Try to log even if logger isn't available
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "A", location = "Plugin.cs:50", message = "ERROR in constructor", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            throw;
        }
    }

    private void LogVersionedFolderDiagnostics(string versionedPluginPath)
    {
        _logger.LogDebug("[MR] Delete marker check: Versioned={Versioned}", System.IO.Directory.Exists(versionedPluginPath));
        if (!System.IO.Directory.Exists(versionedPluginPath)) return;
        try
        {
            var files = System.IO.Directory.GetFiles(versionedPluginPath, "*", System.IO.SearchOption.AllDirectories);
            _logger.LogDebug("[MR] Versioned folder: {Count} files", files.Length);
            var deleteRelated = files.Where(f => System.IO.Path.GetFileName(f).Contains("delete", StringComparison.OrdinalIgnoreCase)).ToList();
            if (deleteRelated.Any())
            {
                _logger.LogWarning("[MR] ⚠️ Found files with 'delete' in name: {Files}", string.Join(", ", deleteRelated.Select(f => System.IO.Path.GetFileName(f))));
            }
            var parentDir = System.IO.Path.GetDirectoryName(versionedPluginPath);
            if (string.IsNullOrWhiteSpace(parentDir) || !System.IO.Directory.Exists(parentDir)) return;
            var parentFiles = System.IO.Directory.GetFiles(parentDir, "*", System.IO.SearchOption.TopDirectoryOnly);
            var parentDelete = parentFiles.Where(f => System.IO.Path.GetFileName(f).Contains("delete", StringComparison.OrdinalIgnoreCase)).ToList();
            if (parentDelete.Any())
            {
                _logger.LogWarning("[MR] ⚠️ Found files with 'delete' in name in parent: {Files}", string.Join(", ", parentDelete.Select(f => System.IO.Path.GetFileName(f))));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MR] Could not list versioned folder: {Message}", ex.Message);
        }
    }

    private static List<string> BuildDeleteMarkerPathList(string pluginsPath, string versionedPluginPath, string nonVersionedPluginPath, string? dllDirectory)
    {
        var deleteMarkerPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(dllDirectory))
        {
            deleteMarkerPaths.Add(System.IO.Path.Combine(dllDirectory, DeleteOnStartupMarkerFileName));
        }
        deleteMarkerPaths.Add(System.IO.Path.Combine(versionedPluginPath, DeleteOnStartupMarkerFileName));
        deleteMarkerPaths.Add(System.IO.Path.Combine(nonVersionedPluginPath, DeleteOnStartupMarkerFileName));
        deleteMarkerPaths.Add(System.IO.Path.Combine(pluginsPath, DeleteOnStartupMarkerFileName));
        deleteMarkerPaths.Add(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(versionedPluginPath) ?? pluginsPath, DeleteOnStartupMarkerFileName));
        if (!string.IsNullOrWhiteSpace(dllDirectory))
        {
            var dllParent = System.IO.Path.GetDirectoryName(dllDirectory);
            if (!string.IsNullOrWhiteSpace(dllParent))
            {
                deleteMarkerPaths.Add(System.IO.Path.Combine(dllParent, DeleteOnStartupMarkerFileName));
            }
        }
        var basePaths = new List<string> { versionedPluginPath, nonVersionedPluginPath, pluginsPath };
        if (!string.IsNullOrWhiteSpace(dllDirectory)) basePaths.Add(dllDirectory);
        var variations = new List<string>();
        foreach (var basePath in basePaths.Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            variations.Add(System.IO.Path.Combine(basePath, DeleteOnStartupMarkerFileName));
            variations.Add(System.IO.Path.Combine(basePath, ".DELETEONSTARTUP"));
            variations.Add(System.IO.Path.Combine(basePath, ".DeleteOnStartup"));
        }
        var all = deleteMarkerPaths.Concat(variations).Distinct().ToList();
        if (!string.IsNullOrWhiteSpace(dllDirectory))
        {
            var dllMarker = System.IO.Path.Combine(dllDirectory, DeleteOnStartupMarkerFileName);
            if (all.Contains(dllMarker))
            {
                all.Remove(dllMarker);
                all.Insert(0, dllMarker);
            }
        }
        return all;
    }

    private void CheckDeleteMarkerAndThrowIfFound(List<string> allMarkerPaths, string pluginVersion, string pluginsPath)
    {
        string? foundMarkerPath = null;
        foreach (var markerPath in allMarkerPaths)
        {
            try
            {
                if (!System.IO.File.Exists(markerPath))
                {
                    _logger.LogDebug("[MR] Delete marker not at: {Path}", markerPath);
                    continue;
                }
                foundMarkerPath = markerPath;
                _logger.LogWarning("[MR] ⚠️ Delete marker found at {Path} - preventing plugin load", markerPath);
                break;
            }
            catch (Exception checkEx)
            {
                _logger.LogWarning(checkEx, "[MR] Error checking delete marker at {Path}: {Message}", markerPath, checkEx.Message);
            }
        }
        if (foundMarkerPath == null) return;
        try
        {
            var logData = new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "UNINSTALL-A", location = "Plugin.cs:142", message = "Delete marker found - throwing exception", data = new { markerPath = foundMarkerPath, pluginName = Name, pluginVersion = pluginVersion, pluginsPath = pluginsPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
        }
        catch
        {
            // Intentionally ignore: debug log must not impact plugin load.
        }
        throw new InvalidOperationException(
            $"Plugin is marked for deletion (deleteOnStartup marker found). Marker path: {foundMarkerPath}");
    }

    /// <summary>
    /// Attempts to unblock files in the plugin directory that may be blocked by Windows Application Control.
    /// </summary>
    /// <param name="pluginDirectory">The plugin directory path.</param>
    private void TryUnblockPluginFiles(string pluginDirectory)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory) || !Directory.Exists(pluginDirectory))
        {
            _logger.LogWarning("[MR] Cannot unblock files - plugin directory not found: {Path}", pluginDirectory ?? "NULL");
            return;
        }

        try
        {
            var files = Directory.GetFiles(pluginDirectory, "*", SearchOption.AllDirectories);
            var unblockedCount = 0;
            var failedCount = 0;
            foreach (var filePath in files)
            {
                var (unblocked, failed) = TryUnblockSingleFile(filePath);
                if (unblocked) unblockedCount++;
                if (failed) failedCount++;
            }

            if (unblockedCount > 0 || failedCount > 0)
            {
                _logger.LogInformation("[MR] Unblock results: {Total} files, {Unblocked} unblocked, {Failed} failed", files.Length, unblockedCount, failedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MR] Error attempting to unblock plugin files: {Message}. Unblocking is optional.", ex.Message);
        }
    }

    /// <summary>
    /// Tries to unblock a single file (PowerShell Unblock-File or Zone.Identifier removal). Returns (unblocked, failed).
    /// </summary>
    private (bool unblocked, bool failed) TryUnblockSingleFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        try
        {
            if (TryUnblockViaPowerShell(filePath, fileName))
            {
                return (true, false);
            }

            if (TryRemoveZoneIdentifier(filePath, fileName))
            {
                return (true, false);
            }

            return (false, false);
        }
        catch (Exception fileEx)
        {
            _logger.LogWarning(fileEx, "[MR] Error processing file {FileName}: {Message}", fileName, fileEx.Message);
            return (false, true);
        }
    }

    private bool TryUnblockViaPowerShell(string filePath, string fileName)
    {
        try
        {
            var processStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-Command \"Unblock-File -Path '{filePath.Replace("'", "''")}' -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            using (var process = System.Diagnostics.Process.Start(processStartInfo))
            {
                if (process == null) return false;
                process.WaitForExit(5000);
                if (process.ExitCode == 0)
                {
                    _logger.LogDebug("[MR] Unblocked: {FileName}", fileName);
                    return true;
                }
                _logger.LogDebug("[MR] Unblock attempt for {FileName} returned exit code {Code}", fileName, process.ExitCode);
                return false;
            }
        }
        catch (Exception psEx)
        {
            _logger.LogWarning(psEx, "[MR] PowerShell unblock failed for {FileName}: {Message}", fileName, psEx.Message);
            return false;
        }
    }

    private bool TryRemoveZoneIdentifier(string filePath, string fileName)
    {
        try
        {
            var zoneIdentifierPath = $"{filePath}:Zone.Identifier";
            if (!File.Exists(zoneIdentifierPath)) return false;
            File.Delete(zoneIdentifierPath);
            _logger.LogDebug("[MR] Removed Zone.Identifier for: {FileName}", fileName);
            return true;
        }
        catch (Exception adsEx)
        {
            _logger.LogDebug(adsEx, "[MR] Could not remove Zone.Identifier for {FileName}: {Message}", fileName, adsEx.Message);
            return false;
        }
    }

    /// <inheritdoc />
    public override string Name => "MetadataRenamer";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("eb5d7894-8eef-4b36-aa6f-5d124e828ce1");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }

    /// <summary>
    /// Handles item updated events from the library manager.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The item change event arguments.</param>
    private void OnItemUpdated(object? sender, MediaBrowser.Controller.Library.ItemChangeEventArgs e)
    {
        try
        {
            _logger?.LogDebug(
                "[MR] OnItemUpdated event received: Type={Type}, Name={Name}, Id={Id}, Disposed={Disposed}",
                e.Item?.GetType().Name ?? "NULL", e.Item?.Name ?? "NULL", e.Item?.Id.ToString() ?? "NULL", _disposed);

            // #region agent log
            DebugLogHelper.SafeAppend(
                System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "A", location = "Plugin.cs:123", message = "OnItemUpdated event received", data = new { itemType = e.Item?.GetType().Name ?? "null", itemName = e.Item?.Name ?? "null", itemId = e.Item?.Id.ToString() ?? "null", disposed = _disposed }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            // #endregion

            // Immediately return if disposed - check before any processing
            // This prevents the plugin from doing any work during uninstall
            if (_disposed)
            {
                _logger?.LogDebug("[MR] OnItemUpdated: Skipping event - plugin is disposed");
                // #region agent log
                DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "E", location = "Plugin.cs:133", message = "Event skipped - disposed", data = new { itemType = e.Item?.GetType().Name ?? "null" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                // #endregion
                return;
            }

            // Double-check with lock to prevent race conditions during disposal
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    _logger?.LogDebug("[MR] OnItemUpdated: Skipping event - plugin is disposed (double-check)");
                    return;
                }

                try
                {
                    _renameCoordinator.HandleItemUpdated(e, Configuration);
                }
                catch (Exception ex)
                {
                    // Log errors to Jellyfin logs but don't crash - plugin may be disposing
                    _logger?.LogError(ex, "[MR] ERROR in HandleItemUpdated: {Message}. {Stack}", ex.Message, ex.StackTrace ?? "N/A");
                    // #region agent log
                    DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "A", location = "Plugin.cs:151", message = "Exception in HandleItemUpdated", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                    // #endregion
                }
            }
        }
        catch (Exception ex)
        {
            // Last resort error handling - log to Jellyfin logs
            _logger?.LogError(ex, "[MR] CRITICAL ERROR in OnItemUpdated: {Message}. {Stack}", ex.Message, ex.StackTrace ?? "N/A");
            // #region agent log
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "A", location = "Plugin.cs:168", message = "CRITICAL ERROR in OnItemUpdated", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            // #endregion
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            // #region agent log - Uninstall debugging
            try
            {
                var pluginsPath = ApplicationPaths?.PluginsPath ?? UnknownPathPlaceholder;
                var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? DefaultVersionFallback;
                var versionedPluginPath = System.IO.Path.Combine(pluginsPath, $"{Name}_{version}");
                var logData = new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "UNINSTALL-B", location = "Plugin.cs:257", message = "Dispose() called", data = new { disposed = _disposed, instanceSet = Instance != null, pluginName = Name, pluginVersion = version, versionedFolderPath = versionedPluginPath, versionedFolderExists = System.IO.Directory.Exists(versionedPluginPath) }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                DebugLogHelper.SafeAppend( logJson);
            }
            catch
            {
                // Intentionally ignore: debug log write must not impact Dispose.
            }
            // #endregion
            
            _logger?.LogDebug("[MR] Dispose() called: Disposed={Disposed}", _disposed);

            // #region agent log
            DebugLogHelper.SafeAppend(
                System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "A", location = "Plugin.cs:176", message = "Dispose() called", data = new { disposed = _disposed, instanceSet = Instance != null }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            // #endregion

            Dispose(true);
            GC.SuppressFinalize(this);
            
            _logger?.LogDebug("[MR] Dispose() completed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] ERROR in Dispose(): {Message}", ex.Message);
            DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "A", location = "Plugin.cs:141", message = "ERROR in Dispose()", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
    }

    /// <summary>
    /// Releases the unmanaged resources used by the plugin and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    private void Dispose(bool disposing)
    {
        try
        {
            _logger?.LogDebug("[MR] Dispose(bool) called: disposing={Disposing}", disposing);
            
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    _logger?.LogWarning("[MR] SKIP: Already disposed");
                    return;
                }

                if (disposing)
                {
                    DebugLogHelper.SafeAppend( System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "A", location = "Plugin.cs:147", message = "Dispose disposing=true", data = new { wasDisposed = _disposed }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                    _disposed = true;
                    LogPreCleanupPluginFolder();
                    UnsubscribeFromLibraryEvents();
                    ClearServiceState();
                    ClearStaticInstance();
                    try
                    {
                        _logger?.LogDebug("[MR] Dispose: Assembly={Assembly}", System.Reflection.Assembly.GetExecutingAssembly().GetName().Name);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[MR] Could not log assembly/thread information: {Message}", ex.Message);
                    }
                    LogPostCleanupAndDiagnose();
                    _logger?.LogDebug("[MR] Dispose cleanup complete");
                    LogDisposeCompleteDebug();
                }
                else
                {
                    _logger?.LogDebug("[MR] disposing=false - only unmanaged resources");
                }
            }
        }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "[MR] CRITICAL ERROR in Dispose(bool): {Message}. {Stack}", ex.Message, ex.StackTrace ?? "N/A");
            // #region agent log - Uninstall debugging
            try
            {
                var logData = new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "UNINSTALL-E", location = "Plugin.cs:615", message = "CRITICAL ERROR in Dispose", data = new { error = ex.Message, stackTrace = ex.StackTrace, errorType = ex.GetType().FullName }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                var logJson = System.Text.Json.JsonSerializer.Serialize(logData) + "\n";
                DebugLogHelper.SafeAppend( logJson);
                _logger?.LogError("[MR] CRITICAL ERROR in Dispose: {Error}", ex.Message);
            }
            catch
            {
                // Intentionally ignore: debug log must not impact outer Dispose error handling.
            }
            // #endregion
        }
    }

    private void LogPreCleanupPluginFolder()
    {
        try
        {
            var pluginsPath = ApplicationPaths?.PluginsPath ?? UnknownPathPlaceholder;
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? DefaultVersionFallback;
            var versionedPluginPath = System.IO.Path.Combine(pluginsPath, $"{Name}_{version}");
            _logger?.LogDebug("[MR] Pre-cleanup: VersionedFolder exists={Exists}", System.IO.Directory.Exists(versionedPluginPath));
            if (!System.IO.Directory.Exists(versionedPluginPath))
                return;
            var dllPath = System.IO.Path.Combine(versionedPluginPath, "Jellyfin.Plugin.MetadataRenamer.dll");
            if (System.IO.File.Exists(dllPath))
            {
                try
                {
                    using (var fs = System.IO.File.Open(dllPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                        _ = fs.Length;
                }
                catch (Exception fileEx)
                {
                    _logger?.LogWarning(fileEx, "[MR] DLL File appears to be locked: {Message}", fileEx.Message);
                }
            }
            else
            {
                _logger?.LogWarning("[MR] DLL file not found at expected path: {Path}", dllPath);
            }
            try
            {
                var files = System.IO.Directory.GetFiles(versionedPluginPath);
                _logger?.LogDebug("[MR] Plugin folder files: {Count}", files.Length);
            }
            catch (Exception listEx)
            {
                _logger?.LogWarning(listEx, "[MR] Could not list files in plugin folder");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MR] Could not check plugin folder path");
        }
    }

    private void UnsubscribeFromLibraryEvents()
    {
        try
        {
            try
            {
                var logData = new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "UNINSTALL-C", location = "Plugin.cs:550", message = "Unsubscribing from events", data = new { libraryManagerExists = _libraryManager != null }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
                _logger?.LogDebug("[MR] Unsubscribing from events");
            }
            catch { /* Intentionally ignore */ }
            if (_libraryManager != null)
            {
                _libraryManager.ItemUpdated -= OnItemUpdated;
                _logger?.LogDebug("[MR] Event handler unsubscribed");
            }
            else
            {
                _logger?.LogWarning("[MR] LibraryManager is null - cannot unsubscribe");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] ERROR unsubscribing from events: {Message}. {Stack}", ex.Message, ex.StackTrace ?? "N/A");
            try
            {
                var logData = new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "UNINSTALL-C", location = "Plugin.cs:565", message = "ERROR unsubscribing from events", data = new { error = ex.Message, stackTrace = ex.StackTrace, errorType = ex.GetType().FullName }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
                _logger?.LogError("[MR] ERROR unsubscribing: {Error}", ex.Message);
            }
            catch { /* Intentionally ignore */ }
        }
    }

    private void ClearServiceState()
    {
        try
        {
            _renameCoordinator?.ClearState();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] ERROR clearing service references: {Message}", ex.Message);
        }
    }

    private void ClearStaticInstance()
    {
        try
        {
            if (Instance == this)
            {
                DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "A", location = "Plugin.cs:186", message = "Clearing static Instance", data = new { }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
                Instance = null;
            }
            else
            {
                _logger?.LogWarning("[MR] Static instance is not this instance - may have been cleared already");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] ERROR clearing static instance: {Message}", ex.Message);
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "A", location = "Plugin.cs:195", message = "ERROR clearing instance", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
        }
    }

    private void LogPostCleanupAndDiagnose()
    {
        try
        {
            var pluginsPath = ApplicationPaths?.PluginsPath ?? UnknownPathPlaceholder;
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? DefaultVersionFallback;
            var versionedPluginPath = System.IO.Path.Combine(pluginsPath, $"{Name}_{version}");
            _logger?.LogDebug("[MR] Post-cleanup: VersionedFolder exists={Exists}", System.IO.Directory.Exists(versionedPluginPath));
            if (!System.IO.Directory.Exists(versionedPluginPath))
            {
                _logger?.LogDebug("[MR] Versioned plugin folder does not exist");
                return;
            }
            _logger?.LogWarning("[MR] ⚠️ Versioned plugin folder still exists after Dispose: {Path}", versionedPluginPath);
            var dllPath = System.IO.Path.Combine(versionedPluginPath, "Jellyfin.Plugin.MetadataRenamer.dll");
            if (System.IO.File.Exists(dllPath))
            {
                _logger?.LogWarning("[MR] DLL still exists: {Path}", dllPath);
                TryLogDllStatusAndAttemptDelete(dllPath);
            }
            _logger?.LogError("[MR] Uninstall diagnostics: Plugin folder still exists at {Path}. Recommended fix: Stop Jellyfin, delete folder, then restart.", versionedPluginPath);
            DiagnoseUninstallFailure(versionedPluginPath, dllPath);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MR] Could not check plugin folder path after cleanup");
        }
    }

    private void TryLogDllStatusAndAttemptDelete(string dllPath)
    {
        try
        {
            var dllInfo = new System.IO.FileInfo(dllPath);
            _logger?.LogDebug("[MR] DLL: Size={Size}, Modified={Modified}", dllInfo.Length, dllInfo.LastWriteTime);
            try
            {
                using (var fs = System.IO.File.Open(dllPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                    _logger?.LogDebug("[MR] DLL can be opened (may still be locked by Jellyfin)");
                try
                {
                    System.IO.File.Delete(dllPath);
                    _logger?.LogInformation("[MR] DLL file deleted successfully");
                }
                catch (System.UnauthorizedAccessException uaEx)
                {
                    _logger?.LogError(uaEx, "[MR] DLL delete failed (locked): {Message}", uaEx.Message);
                }
                catch (System.IO.IOException ioEx)
                {
                    _logger?.LogError(ioEx, "[MR] DLL delete failed (locked): {Message}", ioEx.Message);
                }
            }
            catch (System.IO.IOException ioEx)
            {
                _logger?.LogError(ioEx, "[MR] DLL file is LOCKED and cannot be opened: {Message}", ioEx.Message);
            }
        }
        catch (Exception fileEx)
        {
            _logger?.LogWarning(fileEx, "[MR] Could not check DLL file status");
        }
    }

    private void LogDisposeCompleteDebug()
    {
        try
        {
            var pluginsPath = ApplicationPaths?.PluginsPath ?? UnknownPathPlaceholder;
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? DefaultVersionFallback;
            var versionedPluginPath = System.IO.Path.Combine(pluginsPath, $"{Name}_{version}");
            var logData = new { sessionId = DebugSessionId, runId = "run1", hypothesisId = "UNINSTALL-E", location = "Plugin.cs:600", message = "Dispose() cleanup complete", data = new { versionedFolderExists = System.IO.Directory.Exists(versionedPluginPath), versionedFolderPath = versionedPluginPath }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            DebugLogHelper.SafeAppend(System.Text.Json.JsonSerializer.Serialize(logData) + "\n");
        }
        catch { /* Intentionally ignore */ }
    }

    /// <summary>
    /// Diagnoses why plugin uninstall may fail by checking common issues.
    /// </summary>
    /// <param name="pluginFolderPath">The plugin folder path.</param>
    /// <param name="dllPath">The DLL file path.</param>
    private void DiagnoseUninstallFailure(string pluginFolderPath, string? dllPath)
    {
        try
        {
            _logger?.LogError("[MR] === Uninstall Failure Diagnostics ===");
            if (!string.IsNullOrWhiteSpace(dllPath) && System.IO.File.Exists(dllPath))
                DiagnoseDllLockStatus(dllPath);
            DiagnoseFolderPermissions(pluginFolderPath);
            DiagnoseDeleteMarker(pluginFolderPath);
            DiagnoseFilesInFolder(pluginFolderPath);
            DiagnoseProcessLocks();
            DiagnoseExternalLocks(dllPath);
            _logger?.LogError("[MR] === End Uninstall Failure Diagnostics ===");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] ERROR in DiagnoseUninstallFailure: {Message}", ex.Message);
        }
    }

    private void DiagnoseDllLockStatus(string dllPath)
    {
        _logger?.LogError("[MR] [DIAGNOSTIC-1] Checking DLL file lock status...");
        try
        {
            using (var fs = System.IO.File.Open(dllPath, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
                _logger?.LogError("[MR] [DIAGNOSTIC-1] ✓ DLL file is NOT locked (can be opened exclusively)");
        }
        catch (System.UnauthorizedAccessException uaEx)
        {
            _logger?.LogError(uaEx, "[MR] [DIAGNOSTIC-1] ✗ DLL LOCKED (UnauthorizedAccessException: {Message}). CAUSE: .NET runtime (assembly still loaded). SOLUTION: Stop Jellyfin completely, then delete folder.", uaEx.Message);
        }
        catch (System.IO.IOException ioEx)
        {
            _logger?.LogError(ioEx, "[MR] [DIAGNOSTIC-1] ✗ DLL LOCKED (IOException: {Message}). CAUSE: Jellyfin/antivirus/file indexing. SOLUTION: Stop Jellyfin, disable AV/indexing temporarily, then delete.", ioEx.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] [DIAGNOSTIC-1] ✗ Error checking DLL lock: {Message}", ex.Message);
        }
    }

    private void DiagnoseFolderPermissions(string pluginFolderPath)
    {
        _logger?.LogError("[MR] [DIAGNOSTIC-2] Checking folder permissions...");
        try
        {
            var folderInfo = new System.IO.DirectoryInfo(pluginFolderPath);
            _ = folderInfo.GetAccessControl();
            _logger?.LogError("[MR] [DIAGNOSTIC-2] ✓ Folder permissions accessible");
        }
        catch (System.UnauthorizedAccessException uaEx)
        {
            _logger?.LogError(uaEx, "[MR] [DIAGNOSTIC-2] ✗ Folder permission issue ({Message}). CAUSE: Insufficient permissions. SOLUTION: Run Jellyfin as Administrator or grant full control to Jellyfin service account.", uaEx.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] [DIAGNOSTIC-2] ✗ Error checking folder permissions: {Message}", ex.Message);
        }
    }

    private void DiagnoseDeleteMarker(string pluginFolderPath)
    {
        _logger?.LogError("[MR] [DIAGNOSTIC-3] Checking for deleteOnStartup marker...");
        var deleteMarkerPath = System.IO.Path.Combine(pluginFolderPath, DeleteOnStartupMarkerFileName);
        if (System.IO.File.Exists(deleteMarkerPath))
            _logger?.LogError("[MR] [DIAGNOSTIC-3] ✓ Delete marker found: {Path}. Jellyfin has marked plugin for deletion on next startup.", deleteMarkerPath);
        else
            _logger?.LogError("[MR] [DIAGNOSTIC-3] ✗ Delete marker NOT found: {Path}. CAUSE: Uninstall may not have been triggered. SOLUTION: Uninstall again via Jellyfin UI or delete folder manually.", deleteMarkerPath);
    }

    private void DiagnoseFilesInFolder(string pluginFolderPath)
    {
        _logger?.LogError("[MR] [DIAGNOSTIC-4] Checking files in plugin folder...");
        try
        {
            var files = System.IO.Directory.GetFiles(pluginFolderPath, "*", System.IO.SearchOption.AllDirectories);
            _logger?.LogError("[MR] [DIAGNOSTIC-4] Found {Count} file(s) in folder", files.Length);
            foreach (var file in files)
                DiagnoseSingleFile(file);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] [DIAGNOSTIC-4] ✗ Error listing files: {Message}", ex.Message);
        }
    }

    private void DiagnoseSingleFile(string file)
    {
        try
        {
            var fileInfo = new System.IO.FileInfo(file);
            var fileName = System.IO.Path.GetFileName(file);
            _logger?.LogError("[MR] [DIAGNOSTIC-4]   - {FileName} ({Size} bytes, Attributes: {Attributes})", fileName, fileInfo.Length, fileInfo.Attributes);
            try
            {
                using (var fs = System.IO.File.Open(file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
                    _ = fs.Length;
            }
            catch (Exception lockEx)
            {
                _logger?.LogError(lockEx, "[MR] [DIAGNOSTIC-4]     ⚠️ File is LOCKED: {FileName}", fileName);
            }
        }
        catch (Exception fileEx)
        {
            _logger?.LogError(fileEx, "[MR] [DIAGNOSTIC-4]     ✗ Error checking file: {FileName}", System.IO.Path.GetFileName(file));
        }
    }

    private void DiagnoseProcessLocks()
    {
        try
        {
            var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var processPath = currentProcess.MainModule?.FileName ?? "NULL";
            _logger?.LogError("[MR] [DIAGNOSTIC-5] Checking process locks. Process: {Name} (PID: {Id}), Path: {Path}", currentProcess.ProcessName, currentProcess.Id, processPath);
            var currentAssembly = System.Reflection.Assembly.GetExecutingAssembly();
            var ourAssembly = System.AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.FullName == currentAssembly.FullName);
            if (ourAssembly != null)
                _logger?.LogError("[MR] [DIAGNOSTIC-5] ✗ Assembly still loaded. CAUSE: .NET cannot unload without AppDomain. SOLUTION: Jellyfin must restart to unload.");
            else
                _logger?.LogError("[MR] [DIAGNOSTIC-5] ✓ Assembly not in current AppDomain (may have been unloaded)");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] [DIAGNOSTIC-5] ✗ Error checking process locks: {Message}", ex.Message);
        }
    }

    private void DiagnoseExternalLocks(string? dllPath)
    {
        _logger?.LogError("[MR] [DIAGNOSTIC-6] Checking for external locks (antivirus/file indexing)...");
        try
        {
            if (string.IsNullOrWhiteSpace(dllPath) || !System.IO.File.Exists(dllPath))
                return;
            var tempName = dllPath + ".tmp";
            var renamed = false;
            try
            {
                System.IO.File.Move(dllPath, tempName);
                renamed = true;
                System.IO.File.Move(tempName, dllPath);
                _logger?.LogError("[MR] [DIAGNOSTIC-6] ✓ DLL can be renamed (no external locks detected)");
            }
            catch (System.UnauthorizedAccessException uaEx)
            {
                if (renamed && System.IO.File.Exists(tempName))
                {
                    try { System.IO.File.Move(tempName, dllPath); } catch (Exception restoreEx) { _ = restoreEx; }
                }
                _logger?.LogError(uaEx, "[MR] [DIAGNOSTIC-6] ✗ DLL cannot be renamed (antivirus/file indexing?). CAUSE: External process. SOLUTION: Disable antivirus/indexing temporarily, then delete folder.");
            }
            catch (System.IO.IOException ioEx)
            {
                if (renamed && System.IO.File.Exists(tempName))
                {
                    try { System.IO.File.Move(tempName, dllPath); } catch { }
                }
                _logger?.LogError(ioEx, "[MR] [DIAGNOSTIC-6] ✗ DLL cannot be renamed ({Message}). File is locked by another process.", ioEx.Message);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] [DIAGNOSTIC-6] ✗ Error checking external locks: {Message}", ex.Message);
        }
    }
}
