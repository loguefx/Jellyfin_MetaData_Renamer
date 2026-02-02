using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MetadataRenamer.Services;

/// <summary>
/// Helper class for working with provider IDs.
/// </summary>
public static class ProviderIdHelper
{
    /// <summary>
    /// Gets the best provider ID from the preferred list or falls back to any available provider.
    /// </summary>
    /// <param name="providerIds">The dictionary of provider IDs.</param>
    /// <param name="preferredKeys">The list of preferred provider keys in order of preference.</param>
    /// <returns>A tuple containing the provider key, label, and ID, or null if none found.</returns>
    public static (string ProviderKey, string ProviderLabel, string Id)? GetBestProvider(
        IReadOnlyDictionary<string, string> providerIds,
        IList<string> preferredKeys)
    {
        foreach (var key in preferredKeys)
        {
            if (providerIds.TryGetValue(key, out var id) && !string.IsNullOrWhiteSpace(id))
            {
                var label = key.Trim().ToLowerInvariant(); // tvdb/tmdb/imdb
                return (key, label, id.Trim());
            }
        }

        // fallback to any available provider id
        foreach (var kv in providerIds.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(kv.Value))
            {
                var label = kv.Key.Trim().ToLowerInvariant();
                return (kv.Key, label, kv.Value.Trim());
            }
        }

        return null;
    }

    /// <summary>
    /// Computes a hash of provider IDs to detect meaningful changes.
    /// </summary>
    /// <param name="providerIds">The dictionary of provider IDs.</param>
    /// <returns>A hash string representing the provider IDs.</returns>
    public static string ComputeProviderHash(IReadOnlyDictionary<string, string> providerIds)
    {
        if (providerIds.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            "|",
            providerIds
                .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key.Trim()}={kv.Value?.Trim() ?? string.Empty}"));
    }
}
