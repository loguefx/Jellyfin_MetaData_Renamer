# How MetadataRenamer Works

## Overview

The MetadataRenamer plugin automatically renames series folders when metadata is identified or changed in Jellyfin. It uses a smart detection system to only rename when it's safe to do so.

## How It Works

### Event-Driven System

The plugin listens to Jellyfin's `ItemUpdated` event, which fires whenever:
- A library scan completes
- You manually "Identify" a series
- Metadata is refreshed
- Any metadata change occurs

### Renaming Logic

The plugin will **only rename** when ALL of these conditions are met:

1. ✅ **Item is a Series** (not movies, not other types)
2. ✅ **Series has a valid path** (folder exists on disk)
3. ✅ **Series has Name and ProductionYear** (required for naming)
4. ✅ **Series has at least one Provider ID** (Tvdb, Tmdb, Imdb, etc.)
5. ✅ **Provider IDs have changed** (if `OnlyRenameWhenProviderIdsChange` is enabled - **default: true**)

### Key Setting: `OnlyRenameWhenProviderIdsChange`

**Default: `true`** (enabled)

This is the "smart detection" feature. When enabled:
- The plugin tracks a hash of provider IDs for each series
- It only renames when the hash changes
- This infers that "Identify" or "Refresh Metadata" just happened

**What this means:**

#### ✅ **WILL Rename:**
- When you press **"Identify"** and select a match → Provider IDs change → Rename happens
- When you press **"Identify"** and change to a different match → Provider IDs change → Rename happens
- When you manually refresh metadata and it finds new provider IDs → Rename happens

#### ❌ **WON'T Rename:**
- During normal library scans (if provider IDs don't change)
- When configuring new libraries (if series already has provider IDs)
- On every metadata update (only when provider IDs actually change)

### Example Scenarios

#### Scenario 1: New Library Scan
1. You add a new library with series folders
2. Jellyfin scans and matches some series automatically
3. **Result:** Plugin detects provider ID changes → Renames folders

#### Scenario 2: Manual Identify
1. You have a series "The Flash" that's not matched
2. You press "Identify" and select "The Flash (2014)"
3. **Result:** Provider IDs are added/changed → Plugin renames to "The Flash (2014) [tvdb-279121]"

#### Scenario 3: Change Identity
1. Series is currently "The Flash (2014) [tvdb-279121]"
2. You press "Identify" and change it to "The Flash (2023)"
3. **Result:** Provider IDs change → Plugin renames to "The Flash (2023) [tvdb-XXXXX]"

#### Scenario 4: Regular Library Scan
1. Series already has provider IDs
2. Library scan runs (no metadata changes)
3. **Result:** Provider IDs unchanged → Plugin skips (no rename)

### Naming Format

Default format: `{Name} ({Year}) [{Provider}-{Id}]`

Examples:
- `The Flash (2014) [tvdb-279121]`
- `The Office (2005) [tmdb-2316]`
- `Breaking Bad (2008) [imdb-tt0903747]`

The plugin picks the "best" provider based on your `PreferredSeriesProviders` setting (default: Tvdb, then Tmdb, then Imdb).

### Safety Features

1. **Dry Run Mode** (default: ON)
   - Logs what it would rename without actually renaming
   - Check logs to see what would happen before disabling

2. **Cooldown Period** (default: 60 seconds)
   - Prevents rapid renames of the same series
   - Each series can only be renamed once per cooldown period

3. **Collision Detection**
   - Skips rename if target folder already exists
   - Prevents overwriting existing folders

4. **Provider ID Change Detection**
   - Only renames when provider IDs actually change
   - Prevents unnecessary renames during normal operations

### Configuration Options

- **Enabled**: Turn plugin on/off
- **DryRun**: Log only, don't actually rename (start here!)
- **RenameSeriesFolders**: Enable/disable series renaming
- **RequireProviderIdMatch**: Only rename series with provider IDs
- **OnlyRenameWhenProviderIdsChange**: Only rename when IDs change (smart detection)
- **SeriesFolderFormat**: Customize the naming format
- **PreferredSeriesProviders**: Order of provider preference
- **PerItemCooldownSeconds**: Cooldown between rename attempts

## Testing

1. **Start with DryRun ON** (default)
2. Identify a series in Jellyfin
3. Check Jellyfin logs for `[MR] DRY RUN rename` messages
4. If it looks good, disable DryRun
5. Identify another series to test actual renaming

## Important Notes

- **Only works with Series** - Movies and other types are ignored
- **Requires provider IDs** - Series must be matched to a metadata provider
- **Only renames when IDs change** - Won't rename on every scan (by design)
- **Safe by default** - DryRun is ON, so test first!
