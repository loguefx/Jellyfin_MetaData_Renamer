using System;
using System.Collections.Generic;
using System.Globalization;
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
        Instance = this;
        _libraryManager = libraryManager;

        var logger = loggerFactory.CreateLogger<PathRenameService>();
        _pathRenameService = new PathRenameService(logger);

        var coordinatorLogger = loggerFactory.CreateLogger<RenameCoordinator>();
        _renameCoordinator = new RenameCoordinator(coordinatorLogger, _pathRenameService);

        _libraryManager.ItemUpdated += OnItemUpdated;
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
        _renameCoordinator.HandleItemUpdated(e, Configuration);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by the plugin and optionally releases the managed resources.
    /// </summary>
    /// <param name="disposing">True to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _libraryManager.ItemUpdated -= OnItemUpdated;
            }

            _disposed = true;
        }
    }
}
