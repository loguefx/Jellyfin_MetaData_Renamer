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
        RenameSeasonFolders = true;
        RenameEpisodeFiles = true;
        RenameMovieFolders = true;
        RequireProviderIdMatch = true;
        SeriesFolderFormat = "{Name} ({Year}) [{Provider}-{Id}]";
        SeasonFolderFormat = "Season {Season:00} - {SeasonName}";
        EpisodeFileFormat = "S{Season:00}E{Episode:00} - {Title}";
        MovieFolderFormat = "{Name} ({Year}) [{Provider}-{Id}]";
        PreferredSeriesProviders = new Collection<string> { "Tmdb", "Tvdb", "Imdb" };
        PreferredMovieProviders = new Collection<string> { "Tmdb", "Imdb" };
        OnlyRenameWhenProviderIdsChange = true;
        ProcessDuringLibraryScans = true;
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
    /// Gets or sets a value indicating whether to rename season folders.
    /// </summary>
    public bool RenameSeasonFolders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to rename episode files.
    /// </summary>
    public bool RenameEpisodeFiles { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to rename movie folders.
    /// </summary>
    public bool RenameMovieFolders { get; set; }

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
    /// Gets or sets the format string for season folder names.
    /// Supported placeholders: {Season}, {Season:00}, {SeasonName}.
    /// </summary>
    public string SeasonFolderFormat { get; set; }

    /// <summary>
    /// Gets or sets the format string for episode file names.
    /// Supported placeholders: {SeriesName}, {Season}, {Episode}, {Title}, {Year}.
    /// </summary>
    public string EpisodeFileFormat { get; set; }

    /// <summary>
    /// Gets or sets the format string for movie folder names.
    /// Supported placeholders: {Name}, {Year}, {Provider}, {Id}.
    /// </summary>
    public string MovieFolderFormat { get; set; }

    /// <summary>
    /// Gets or sets the list of preferred provider keys in order of preference for series.
    /// </summary>
    [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Setter required for XML serialization")]
    public Collection<string> PreferredSeriesProviders { get; set; }

    /// <summary>
    /// Gets or sets the list of preferred provider keys in order of preference for movies.
    /// </summary>
    [SuppressMessage("Microsoft.Usage", "CA2227:CollectionPropertiesShouldBeReadOnly", Justification = "Setter required for XML serialization")]
    public Collection<string> PreferredMovieProviders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to only rename when provider IDs change (inferring Identify/refresh just happened).
    /// </summary>
    public bool OnlyRenameWhenProviderIdsChange { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to process items during library scans.
    /// When enabled, library scans will automatically update folder names and episode files to match metadata.
    /// When disabled, only the "Identify" flow will trigger updates (when provider IDs change).
    /// </summary>
    public bool ProcessDuringLibraryScans { get; set; }

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
