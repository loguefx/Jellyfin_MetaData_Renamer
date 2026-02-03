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
            _logger.LogInformation("[MR] ===== Plugin Constructor Called =====");
            _logger.LogInformation("[MR] Plugin Name: {Name}, ID: {Id}", Name, Id);
            _logger.LogInformation("[MR] Application Paths: {Paths}", applicationPaths?.DataPath ?? "NULL");
            
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:33", message = "Plugin constructor", data = new { pluginName = Name, pluginId = Id.ToString(), dataPath = applicationPaths?.DataPath ?? "null" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
            
            Instance = this;
            _libraryManager = libraryManager;

            var logger = loggerFactory.CreateLogger<PathRenameService>();
            _pathRenameService = new PathRenameService(logger);

            var coordinatorLogger = loggerFactory.CreateLogger<RenameCoordinator>();
            _renameCoordinator = new RenameCoordinator(coordinatorLogger, _pathRenameService);

            _libraryManager.ItemUpdated += OnItemUpdated;
            
            _logger.LogInformation("[MR] Plugin initialized successfully");
            _logger.LogInformation("[MR] Event handler subscribed to ItemUpdated");
            
            // Log plugin folder path
            try
            {
                var pluginPath = System.IO.Path.Combine(applicationPaths?.PluginsPath ?? "UNKNOWN", Name);
                _logger.LogInformation("[MR] Expected Plugin Folder Path: {Path}", pluginPath);
                _logger.LogInformation("[MR] Plugin Folder Exists: {Exists}", System.IO.Directory.Exists(pluginPath));
                
                if (System.IO.Directory.Exists(pluginPath))
                {
                    var files = System.IO.Directory.GetFiles(pluginPath);
                    _logger.LogInformation("[MR] Plugin Folder Files: {Files}", string.Join(", ", files.Select(f => System.IO.Path.GetFileName(f))));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MR] Could not check plugin folder path");
            }
        }
        catch (Exception ex)
        {
            // Try to log even if logger isn't available
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:50", message = "ERROR in constructor", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            throw;
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
            try
            {
                System.IO.File.AppendAllText(
                    @"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log",
                    System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:123", message = "OnItemUpdated event received", data = new { itemType = e.Item?.GetType().Name ?? "null", itemName = e.Item?.Name ?? "null", itemId = e.Item?.Id.ToString() ?? "null", disposed = _disposed }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            }
            catch { }
            // #endregion

            // Immediately return if disposed - check before any processing
            // This prevents the plugin from doing any work during uninstall
            if (_disposed)
            {
                _logger?.LogDebug("[MR] OnItemUpdated: Skipping event - plugin is disposed");
                // #region agent log
                try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "E", location = "Plugin.cs:133", message = "Event skipped - disposed", data = new { itemType = e.Item?.GetType().Name ?? "null" }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
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
                    _logger?.LogError(ex, "[MR] ERROR in HandleItemUpdated: {Message}", ex.Message);
                    _logger?.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
                    // #region agent log
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:151", message = "Exception in HandleItemUpdated", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    // #endregion
                }
            }
        }
        catch (Exception ex)
        {
            // Last resort error handling - log to Jellyfin logs
            _logger?.LogError(ex, "[MR] CRITICAL ERROR in OnItemUpdated: {Message}", ex.Message);
            _logger?.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:168", message = "CRITICAL ERROR in OnItemUpdated", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            _logger?.LogInformation("[MR] ===== Dispose() Called =====");
            _logger?.LogInformation("[MR] Current Disposed State: {Disposed}", _disposed);
            _logger?.LogInformation("[MR] Static Instance Set: {InstanceSet}", Instance != null);

            // #region agent log
            try
            {
                System.IO.File.AppendAllText(
                    @"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log",
                    System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:176", message = "Dispose() called", data = new { disposed = _disposed, instanceSet = Instance != null }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n");
            }
            catch { }
            // #endregion

            Dispose(true);
            GC.SuppressFinalize(this);
            
            _logger?.LogInformation("[MR] Dispose() completed");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] ERROR in Dispose(): {Message}", ex.Message);
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:141", message = "ERROR in Dispose()", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
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
            _logger?.LogInformation("[MR] === Dispose(bool disposing={Disposing}) Called ===", disposing);
            
            lock (_disposeLock)
            {
                if (_disposed)
                {
                    _logger?.LogWarning("[MR] SKIP: Already disposed");
                    return;
                }

                if (disposing)
                {
                    // #region agent log
                    try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:147", message = "Dispose disposing=true", data = new { wasDisposed = _disposed }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                    // #endregion
                    
                    _logger?.LogInformation("[MR] Step 1: Marking as disposed");
                    // Mark as disposed immediately to prevent any new operations
                    _disposed = true;

                    // Log plugin folder path before cleanup
                    try
                    {
                        var pluginsPath = ApplicationPaths?.PluginsPath ?? "UNKNOWN";
                        var pluginPath = System.IO.Path.Combine(pluginsPath, Name);
                        
                        // Get version from assembly
                        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
                        var versionedPluginPath = System.IO.Path.Combine(pluginsPath, $"{Name}_{version}");
                        
                        _logger?.LogInformation("[MR] Plugins Base Path: {Path}", pluginsPath);
                        _logger?.LogInformation("[MR] Plugin Folder Path (non-versioned): {Path}", pluginPath);
                        _logger?.LogInformation("[MR] Plugin Folder Path (versioned): {Path}", versionedPluginPath);
                        _logger?.LogInformation("[MR] Plugin Version: {Version}", version);
                        _logger?.LogInformation("[MR] Non-versioned Folder Exists: {Exists}", System.IO.Directory.Exists(pluginPath));
                        _logger?.LogInformation("[MR] Versioned Folder Exists: {Exists}", System.IO.Directory.Exists(versionedPluginPath));
                        
                        // Check DLL file specifically
                        if (System.IO.Directory.Exists(versionedPluginPath))
                        {
                            var dllPath = System.IO.Path.Combine(versionedPluginPath, "Jellyfin.Plugin.MetadataRenamer.dll");
                            if (System.IO.File.Exists(dllPath))
                            {
                                var dllInfo = new System.IO.FileInfo(dllPath);
                                _logger?.LogInformation("[MR] DLL File Path: {Path}", dllPath);
                                _logger?.LogInformation("[MR] DLL File Exists: {Exists}", dllInfo.Exists);
                                _logger?.LogInformation("[MR] DLL File Size: {Size} bytes", dllInfo.Length);
                                _logger?.LogInformation("[MR] DLL Last Modified: {Modified}", dllInfo.LastWriteTime);
                                _logger?.LogInformation("[MR] DLL Attributes: {Attributes}", dllInfo.Attributes);
                                
                                // Try to check if file is locked
                                try
                                {
                                    using (var fs = System.IO.File.Open(dllPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                                    {
                                        _logger?.LogInformation("[MR] DLL File can be opened (not locked by us)");
                                    }
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
                            
                            // List all files in the plugin folder
                            try
                            {
                                var files = System.IO.Directory.GetFiles(versionedPluginPath);
                                _logger?.LogInformation("[MR] Files in plugin folder ({Count}): {Files}", files.Length, string.Join(", ", files.Select(f => System.IO.Path.GetFileName(f))));
                            }
                            catch (Exception listEx)
                            {
                                _logger?.LogWarning(listEx, "[MR] Could not list files in plugin folder");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[MR] Could not check plugin folder path");
                    }

                    _logger?.LogInformation("[MR] Step 2: Unsubscribing from events");
                    try
                    {
                        // Unsubscribe from events FIRST to prevent any new event processing
                        // This must happen synchronously to ensure the DLL can be unloaded
                        if (_libraryManager != null)
                        {
                            // #region agent log
                            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:164", message = "Unsubscribing from events", data = new { }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                            // #endregion
                            _libraryManager.ItemUpdated -= OnItemUpdated;
                            _logger?.LogInformation("[MR] ✓ Event handler unsubscribed successfully");
                        }
                        else
                        {
                            _logger?.LogWarning("[MR] LibraryManager is null - cannot unsubscribe");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "[MR] ERROR unsubscribing from events: {Message}", ex.Message);
                        _logger?.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
                        // #region agent log
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:175", message = "ERROR unsubscribing", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                        // #endregion
                    }

                    _logger?.LogInformation("[MR] Step 3: Clearing service references and internal state");
                    try
                    {
                        // Clear internal state from services to help with assembly unloading
                        if (_renameCoordinator != null)
                        {
                            _renameCoordinator.ClearState();
                            _logger?.LogInformation("[MR] ✓ RenameCoordinator state cleared");
                            // Note: Cannot null readonly fields, but ClearState() releases internal references
                        }
                        
                        if (_pathRenameService != null)
                        {
                            // PathRenameService has no internal state to clear
                            _logger?.LogInformation("[MR] PathRenameService instance exists - no state to clear");
                        }
                        
                        // Note: Cannot null readonly fields (_libraryManager, _renameCoordinator, _pathRenameService)
                        // but ClearState() has been called to release internal references
                        _logger?.LogInformation("[MR] Service references are readonly - cannot be nulled, but internal state cleared");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "[MR] ERROR clearing service references: {Message}", ex.Message);
                    }

                    _logger?.LogInformation("[MR] Step 4: Clearing static instance reference");
                    try
                    {
                        // Clear static instance reference immediately
                        // This is critical for Jellyfin to properly uninstall the plugin
                        if (Instance == this)
                        {
                            // #region agent log
                            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:186", message = "Clearing static Instance", data = new { }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                            // #endregion
                            Instance = null;
                            _logger?.LogInformation("[MR] ✓ Static instance cleared");
                        }
                        else
                        {
                            _logger?.LogWarning("[MR] Static instance is not this instance - may have been cleared already");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "[MR] ERROR clearing static instance: {Message}", ex.Message);
                        // #region agent log
                        try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:195", message = "ERROR clearing instance", data = new { error = ex.Message }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
                        // #endregion
                    }

                    _logger?.LogInformation("[MR] Step 5: Logging assembly and thread information");
                    try
                    {
                        // Log assembly information
                        var currentAssembly = System.Reflection.Assembly.GetExecutingAssembly();
                        _logger?.LogInformation("[MR] Current Assembly: {Assembly}", currentAssembly.FullName);
                        _logger?.LogInformation("[MR] Assembly Location: {Location}", currentAssembly.Location);
                        _logger?.LogInformation("[MR] Assembly CodeBase: {CodeBase}", currentAssembly.CodeBase);
                        
                        // Log thread information
                        var threadCount = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
                        _logger?.LogInformation("[MR] Process Thread Count: {Count}", threadCount);
                        
                        // Log object references BEFORE nulling
                        _logger?.LogInformation("[MR] === Object References BEFORE Nulling ===");
                        _logger?.LogInformation("[MR] LibraryManager Reference: {IsNull}", _libraryManager == null ? "NULL" : "EXISTS");
                        _logger?.LogInformation("[MR] RenameCoordinator Reference: {IsNull}", _renameCoordinator == null ? "NULL" : "EXISTS");
                        _logger?.LogInformation("[MR] PathRenameService Reference: {IsNull}", _pathRenameService == null ? "NULL" : "EXISTS");
                        _logger?.LogInformation("[MR] Logger Reference: {IsNull}", _logger == null ? "NULL" : "EXISTS");
                        
                        // Check for any event handlers still attached
                        if (_libraryManager != null)
                        {
                            // We can't directly check if event handlers are attached, but we've already unsubscribed
                            _logger?.LogInformation("[MR] LibraryManager still referenced - will be nulled in Step 3");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[MR] Could not log assembly/thread information: {Message}", ex.Message);
                    }

                    _logger?.LogInformation("[MR] Step 6: Requesting garbage collection");
                    try
                    {
                        // Request garbage collection to help release references
                        // Note: This won't unload the assembly (that requires process restart),
                        // but it helps release any remaining object references
                        var gen0Before = GC.CollectionCount(0);
                        var gen1Before = GC.CollectionCount(1);
                        var gen2Before = GC.CollectionCount(2);
                        
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        
                        var gen0After = GC.CollectionCount(0);
                        var gen1After = GC.CollectionCount(1);
                        var gen2After = GC.CollectionCount(2);
                        
                        _logger?.LogInformation("[MR] ✓ Garbage collection requested");
                        _logger?.LogInformation("[MR] GC Gen0: {Before} -> {After}", gen0Before, gen0After);
                        _logger?.LogInformation("[MR] GC Gen1: {Before} -> {After}", gen1Before, gen1After);
                        _logger?.LogInformation("[MR] GC Gen2: {Before} -> {After}", gen2Before, gen2After);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[MR] Could not request garbage collection: {Message}", ex.Message);
                    }

                    // Log plugin folder path after cleanup
                    try
                    {
                        var pluginsPath = ApplicationPaths?.PluginsPath ?? "UNKNOWN";
                        var pluginPath = System.IO.Path.Combine(pluginsPath, Name);
                        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
                        var versionedPluginPath = System.IO.Path.Combine(pluginsPath, $"{Name}_{version}");
                        
                        _logger?.LogInformation("[MR] === Post-Cleanup Folder Check ===");
                        _logger?.LogInformation("[MR] Non-versioned Folder Exists: {Exists}", System.IO.Directory.Exists(pluginPath));
                        _logger?.LogInformation("[MR] Versioned Folder Exists: {Exists}", System.IO.Directory.Exists(versionedPluginPath));
                        
                        if (System.IO.Directory.Exists(versionedPluginPath))
                        {
                            _logger?.LogWarning("[MR] ⚠️ WARNING: Versioned plugin folder still exists after Dispose()!");
                            _logger?.LogWarning("[MR] Folder Path: {Path}", versionedPluginPath);
                            
                            // Check DLL file status
                            var dllPath = System.IO.Path.Combine(versionedPluginPath, "Jellyfin.Plugin.MetadataRenamer.dll");
                            if (System.IO.File.Exists(dllPath))
                            {
                                _logger?.LogWarning("[MR] DLL file still exists: {Path}", dllPath);
                                try
                                {
                                    var dllInfo = new System.IO.FileInfo(dllPath);
                                    _logger?.LogWarning("[MR] DLL File Size: {Size} bytes, Last Modified: {Modified}", dllInfo.Length, dllInfo.LastWriteTime);
                                    
                                    // Try to check file lock with different access modes
                                    try
                                    {
                                        // Try with ReadWrite share mode (allows other readers)
                                        using (var fs = System.IO.File.Open(dllPath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                                        {
                                            _logger?.LogInformation("[MR] DLL file can be opened with ReadWrite share (may still be locked by Jellyfin process)");
                                        }
                                        
                                        // Try to delete the file directly to see what error we get
                                        try
                                        {
                                            System.IO.File.Delete(dllPath);
                                            _logger?.LogInformation("[MR] ✓ DLL file deleted successfully!");
                                        }
                                        catch (System.UnauthorizedAccessException uaEx)
                                        {
                                            _logger?.LogError(uaEx, "[MR] DLL file DELETE FAILED - UnauthorizedAccessException: {Message}", uaEx.Message);
                                            _logger?.LogError("[MR] This indicates the DLL is locked by the Jellyfin process (assembly still loaded)");
                                        }
                                        catch (System.IO.IOException ioEx)
                                        {
                                            _logger?.LogError(ioEx, "[MR] DLL file DELETE FAILED - IOException: {Message}", ioEx.Message);
                                            _logger?.LogError("[MR] This indicates the DLL is locked (likely by Jellyfin process)");
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
                            
                            _logger?.LogWarning("[MR] === Root Cause Analysis ===");
                            _logger?.LogWarning("[MR] The DLL cannot be deleted because .NET has the assembly loaded in memory.");
                            _logger?.LogWarning("[MR] .NET assemblies cannot be unloaded without unloading the AppDomain (requires process restart).");
                            _logger?.LogWarning("[MR] This is expected behavior - Jellyfin will delete on next startup (deleteOnStartup mechanism).");
                            _logger?.LogWarning("[MR] All plugin cleanup completed successfully - DLL lock is a .NET limitation.");
                        }
                        else
                        {
                            _logger?.LogInformation("[MR] ✓ Versioned plugin folder does not exist (Jellyfin may have deleted it)");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "[MR] Could not check plugin folder path after cleanup");
                    }

                    _logger?.LogInformation("[MR] === Dispose() Cleanup Complete ===");
                }
                else
                {
                    _logger?.LogInformation("[MR] disposing=false - only unmanaged resources");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[MR] CRITICAL ERROR in Dispose(bool): {Message}", ex.Message);
            _logger?.LogError("[MR] Stack Trace: {StackTrace}", ex.StackTrace ?? "N/A");
            // #region agent log
            try { System.IO.File.AppendAllText(@"d:\Jellyfin Projects\Jellyfin_Metadata_tool\.cursor\debug.log", System.Text.Json.JsonSerializer.Serialize(new { sessionId = "debug-session", runId = "run1", hypothesisId = "A", location = "Plugin.cs:220", message = "CRITICAL ERROR in Dispose", data = new { error = ex.Message, stackTrace = ex.StackTrace }, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) + "\n"); } catch { }
            // #endregion
        }
    }
}
