using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MetadataRenamer.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        Enabled = true;
        DryRun = true;
        RenameSeriesFolders = true;
        RequireProviderIdMatch = true;
        SeriesFolderFormat = "{Name} ({Year}) [{Provider}-{Id}]";
        PreferredSeriesProviders = new Collection<string> { "Tvdb", "Tmdb", "Imdb" };
        OnlyRenameWhenProviderIdsChange = true;
        PerItemCooldownSeconds = 60;
        AllowedLibraryNames = new Collection<string>();
    }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to perform dry runs (log only, no actual renames).
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to rename series folders.
    /// </summary>
    public bool RenameSeriesFolders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to require a provider ID match before renaming.
    /// </summary>
    public bool RequireProviderIdMatch { get; set; }

    /// <summary>
    /// Gets or sets the format string for series folder names.
    /// Supported placeholders: {Name}, {Year}, {Provider}, {Id}.
    /// </summary>
    public string SeriesFolderFormat { get; set; }

    /// <summary>
    /// Gets or sets the list of preferred provider keys in order of preference.
    /// </summary>
    [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Setter required for XML serialization")]
    public Collection<string> PreferredSeriesProviders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to only rename when provider IDs change (inferring Identify/refresh just happened).
    /// </summary>
    public bool OnlyRenameWhenProviderIdsChange { get; set; }

    /// <summary>
    /// Gets or sets the cooldown period in seconds between rename attempts for the same item.
    /// </summary>
    public int PerItemCooldownSeconds { get; set; }

    /// <summary>
    /// Gets or sets the list of allowed library names (empty = all libraries).
    /// </summary>
    [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Setter required for XML serialization")]
    public Collection<string> AllowedLibraryNames { get; set; }
}
