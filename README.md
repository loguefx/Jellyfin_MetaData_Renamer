# MetadataRenamer - Jellyfin Plugin

Automatically renames series folders, season folders, episode files, and movie folders when metadata is identified in Jellyfin, using metadata from providers like TVDB, TMDB, and IMDB.

## ⚠️ Important: Library Scan Required for Episode Renaming

**For episode file renaming to take effect, you must perform a library scan after identifying or updating metadata.** The plugin processes episodes when Jellyfin fires `ItemUpdated` events, which occur during library scans. Simply identifying a series will rename the series folder, but episode files will be renamed during the next library scan.

## 🔄 Processing Modes: Normal Scans vs Replace All Metadata

The plugin supports two distinct processing modes:

### Normal Library Scans ("Scan for new and updated files")
- **Only processes identified shows** - When you use "Identify" on a series, the plugin will rename the series folder and process episodes
- **Does NOT process everything** - Normal scans will only trigger renaming when provider IDs change (when shows are identified)
- **Use case:** Ideal for identifying individual shows without processing your entire library
- **Episode processing:** Episodes are automatically processed when a series is identified (no additional scan needed)

### Replace All Metadata (Bulk Refresh)
- **Processes entire library** - When you use "Replace all metadata", the plugin automatically detects this and processes ALL series in your library
- **Bulk processing** - All series folders, season folders, and episode files are updated to match their current metadata
- **Use case:** Perfect for correcting all shows in bulk after metadata updates or configuration changes
- **Episode processing:** All episodes in all series are processed automatically

**How it works:** The plugin detects when multiple series are being updated in quick succession (indicating "Replace all metadata") and automatically triggers bulk processing of all series in the library.

## 🎬 Multi-Season Show Support

The plugin fully supports shows with any number of seasons (Season 1, 2, 3, 4, 5, etc.):

### Season 1 Episodes
- **Standard validation** - Episode numbers in filenames must match metadata (strict safety check)
- **Normal path validation** - Uses standard file path checks
- **Standard metadata validation** - Requires complete metadata (season number, episode number, title)

### Season 2+ Episodes (All seasons >= 2)
- **Relaxed validation** - Episode number mismatch is allowed (filenames may have incorrect numbering, but Jellyfin metadata is authoritative)
- **Enhanced path derivation** - Automatically derives episode paths if metadata paths are stale after folder renames
- **Enhanced metadata derivation** - Can extract season numbers from folder paths if metadata is missing
- **Works for all seasons** - Season 2, 3, 4, 5, and beyond all use the same enhanced logic

**Why this matters:** Multi-season shows often have inconsistent filename numbering (e.g., absolute episode numbers instead of season-relative), but Jellyfin's metadata is authoritative. The plugin trusts Jellyfin's metadata for Season 2+ episodes while maintaining strict validation for Season 1 to prevent errors.

## Examples

### Series Folders
- `The Flash (2014) [tvdb-279121]`
- `The Office (2005) [tmdb-2316]`
- `Breaking Bad (2008) [imdb-tt0903747]`

### Season Folders
- `Season 01`
- `Season 02`
- `Season 1` (customizable format)

### Episode Files
- `S01E01 - Pilot.mp4`
- `S02E05 - The Darkness and the Light.mp4`
- `E09 - Episode Title.mp4` (for flat structures without season folders)

### Movie Folders
- `The Matrix (1999) [tmdb-603]`
- `Inception (2010) [tmdb-27205]`
- `The Dark Knight (2008) [imdb-tt0468569]`

## Installation

### Option 1: Install via Jellyfin Plugin Repository (Recommended)

1. Open Jellyfin web UI
2. Go to **Dashboard** > **Plugins** > **Repositories**
3. Click the **+** button
4. Enter this repository URL:
   ```
   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
   ```
5. Click **OK**
6. Go to **Dashboard** > **Plugins** > **Catalog**
7. Find **MetadataRenamer** in the **General** category
8. Click **Install**

### Option 2: Manual Installation

1. Download the plugin DLL:
   - [Jellyfin.Plugin.MetadataRenamer.dll](https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.MetadataRenamer.dll)
2. Create plugin folder:
   - **Windows**: `C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer\`
   - **Linux**: `/var/lib/jellyfin/plugins/MetadataRenamer/`
3. Copy the DLL to that folder
4. Restart Jellyfin
5. The plugin will appear in **Dashboard** > **Plugins**

## How It Works

### Overview

MetadataRenamer listens to Jellyfin's library update events and automatically renames:
- **Series folders** - When metadata is identified or changed
- **Season folders** - When season metadata is available
- **Episode files** - Using episode numbers and titles from metadata
- **Movie folders** - When metadata is identified or changed (movie files are not touched)

The plugin uses smart detection to only rename when it's safe to do so, and validates episode numbers to prevent incorrect renames.

### When Does It Rename?

#### Series Folders
The plugin will **only rename series folders** when ALL of these conditions are met:

1. ✅ **Item is a Series** (movies and other types are ignored)
2. ✅ **Series has a valid folder path** (folder exists on disk)
3. ✅ **Series has Name and ProductionYear** (required for naming)
4. ✅ **Series has at least one Provider ID** (Tvdb, Tmdb, Imdb, etc.)
5. ✅ **Provider IDs have changed** (if `OnlyRenameWhenProviderIdsChange` is enabled - **default: true**)

#### Episode Files
The plugin will **rename episode files** when:

1. ✅ **Episode renaming is enabled** in plugin settings
2. ✅ **Episode has valid metadata** (episode number and title from Jellyfin)
3. ✅ **Episode number validation passes**:
   - **Season 1:** Episode number in filename must match metadata (strict safety check)
   - **Season 2+:** Episode number mismatch is allowed (Jellyfin metadata is authoritative)
4. ✅ **Series is identified** (provider IDs change) - Episodes are processed automatically when a series is identified
5. ✅ **"Replace all metadata" is used** - All episodes in all series are processed during bulk refresh

**Note:** 
- **Normal scans:** Episodes are automatically processed when a series is identified (no additional scan needed)
- **Replace all metadata:** Automatically processes all episodes in all series in the library
- **Multi-season support:** Works correctly for shows with any number of seasons (Season 1, 2, 3, 4, 5, etc.)

#### Movie Folders
The plugin will **rename movie folders** when:

1. ✅ **Movie renaming is enabled** in plugin settings
2. ✅ **Movie has valid metadata** (name and year from Jellyfin)
3. ✅ **Movie has at least one Provider ID** (Tmdb, Imdb, etc.) if `RequireProviderIdMatch` is enabled
4. ✅ **Provider IDs have changed** (if `OnlyRenameWhenProviderIdsChange` is enabled) **OR** "Process During Library Scans" is enabled

**Note:** Movie folders are renamed, but the movie file itself is never touched. The plugin only renames the folder containing the movie file.

### Key Feature: Smart Detection

**Default Setting: `OnlyRenameWhenProviderIdsChange = true`**

This is the "smart detection" feature that prevents unnecessary renames:

- The plugin tracks a hash of provider IDs for each series
- It only renames when the hash **changes**
- This infers that "Identify" or "Refresh Metadata" just happened

### Real-World Scenarios

#### ✅ Scenario 1: New Library Scan (ProcessDuringLibraryScans Enabled)
1. You add a new library with series folders and movie folders
2. Jellyfin scans and automatically matches some series and movies
3. **Result:** 
   - Plugin detects provider ID changes → Renames series folders and movie folders to proper format
   - If "Process During Library Scans" is enabled → All series folders, season folders, episode files, and movie folders are updated to match metadata during the scan
   - Episode files are renamed using metadata (if episode renaming is enabled)
   - Movie folders are renamed with title, year, and provider ID (movie files are not touched)

#### ✅ Scenario 2: Manual Identify with Episode Renaming
1. You have a series "The Flash" that's not matched
2. You press **"Identify"** and select "The Flash (2014)"
3. **Result:** Provider IDs are added → Plugin renames series folder to `The Flash (2014) [tvdb-279121]`
4. **Perform a library scan** → Episode files are renamed: `S01E01 - Pilot.mp4`, `S01E02 - Fastest Man Alive.mp4`, etc.

#### ✅ Scenario 3: Movie Identification
1. You have a movie folder "The Matrix" that's not matched
2. You press **"Identify"** and select "The Matrix (1999)"
3. **Result:** Provider IDs are added → Plugin renames movie folder to `The Matrix (1999) [tmdb-603]`
4. **Note:** The movie file itself (e.g., `The Matrix.mkv`) is not renamed, only the folder

#### ✅ Scenario 4: Flat Structure (No Season Folders)
1. Series has episodes directly in the series folder (no season subfolders)
2. You identify the series and perform a library scan
3. **Result:** 
   - Series folder renamed: `The Flash (2014) [tvdb-279121]`
   - "Season 01" folder created automatically
   - Episodes moved to "Season 01" folder
   - Episodes renamed: `S01E01 - Pilot.mp4`, etc.

#### ✅ Scenario 5: Multi-Season Show (Season 1, 2, 3, etc.)
1. Series has multiple seasons (e.g., "Season 1", "Season 2", "Season 3")
2. You identify the series
3. **Result:**
   - Series folder renamed: `The Flash (2014) [tvdb-279121]`
   - Season folders left untouched (no changes)
   - **Season 1 episodes:** Renamed with strict validation (episode number must match filename)
   - **Season 2+ episodes:** Renamed with relaxed validation (trusts Jellyfin metadata even if filename numbering is incorrect)
   - All episodes renamed: `S01E01 - Pilot.mp4`, `S02E05 - The Darkness and the Light.mp4`, `S03E10 - Episode Title.mp4`, etc.
   - Works for any number of seasons (2, 3, 4, 5, and beyond)

#### ✅ Scenario 6: Replace All Metadata (Bulk Refresh)
1. You have multiple series in your library that need updating
2. You select a series (or library) and choose **"Replace all metadata"**
3. **Result:** 
   - Plugin detects bulk refresh (multiple series updates in quick succession)
   - All series in the library are processed automatically
   - All series folders, season folders, and episode files are updated to match current metadata
   - **Multi-season shows:** All seasons (1, 2, 3, etc.) are processed correctly
   - Perfect for bulk corrections after metadata provider changes or configuration updates

#### ❌ Scenario 7: Regular Library Scan (No Changes)
1. Series already has provider IDs: `The Flash (2014) [tvdb-279121]`
2. Regular library scan runs (no metadata changes, no identification)
3. **Result:** Provider IDs unchanged → Plugin skips processing (normal scans only process identified shows)

### What This Means

**WILL Rename:**
- ✅ **Series folders:** When you press **"Identify"** and select a match
- ✅ **Series folders:** When you press **"Identify"** and change to a different match
- ✅ **Series folders:** When you manually refresh metadata and it finds new provider IDs
- ✅ **Series folders:** During library scans if series get matched for the first time
- ✅ **Series folders:** When you use **"Replace all metadata"** - All series folders in the library are processed
- ✅ **Episode files:** Automatically when a series is identified (provider IDs change)
- ✅ **Episode files:** When you use **"Replace all metadata"** - All episodes in all series are processed
- ✅ **Episode files:** Works for all seasons (Season 1, 2, 3, 4, 5, etc.) with appropriate validation
- ✅ **Season folders:** When season metadata is available and season renaming is enabled
- ✅ **Season folders:** When you use **"Replace all metadata"** - All season folders are processed
- ✅ **Movie folders:** When you press **"Identify"** and select a match
- ✅ **Movie folders:** When you press **"Identify"** and change to a different match
- ✅ **Movie folders:** During library scans if movies get matched for the first time
- ✅ **Movie folders:** When you use **"Replace all metadata"** - All movie folders are processed

**WON'T Rename:**
- ❌ During normal library scans if provider IDs don't change (normal scans only process identified shows)
- ❌ On every metadata update (only when provider IDs actually change, or during "Replace all metadata")
- ❌ Items without provider IDs (unless `RequireProviderIdMatch` is disabled)
- ❌ **Season 1 episodes** if episode number in filename doesn't match metadata (strict safety check)
- ❌ **Season 2+ episodes** if target filename already exists with different content (prevents overwriting)
- ❌ Movie files themselves (only movie folders are renamed)

## Configuration

Access plugin settings: **Dashboard** > **Plugins** > **MetadataRenamer**

### Settings

| Setting | Default | Description |
|---------|---------|-------------|
| **Enabled** | `true` | Turn the plugin on/off |
| **Dry Run Mode** | `true` | Log only, don't actually rename (start here!) |
| **Rename Series Folders** | `true` | Enable/disable series folder renaming |
| **Rename Season Folders** | `true` | Enable/disable season folder renaming |
| **Rename Episode Files** | `true` | Enable/disable episode file renaming |
| **Rename Movie Folders** | `true` | Enable/disable movie folder renaming |
| **Require Provider ID Match** | `true` | Only rename items with provider IDs |
| **Only Rename When Provider IDs Change** | `true` | Smart detection - only rename when IDs change |
| **Process During Library Scans** | `true` | When enabled, library scans will automatically update all series folders, season folders, and episode files to match their metadata, even if provider IDs haven't changed. When disabled, only the "Identify" flow will trigger updates (when provider IDs change). |
| **Series Folder Format** | `{Name} ({Year}) [{Provider}-{Id}]` | Customize series folder naming format |
| **Season Folder Format** | `Season {Season:00}` | Customize season folder naming format |
| **Episode File Format** | `S{Season:00}E{Episode:00} - {Title}` | Customize episode file naming format |
| **Movie Folder Format** | `{Name} ({Year}) [{Provider}-{Id}]` | Customize movie folder naming format |
| **Preferred Series Providers** | `Tmdb, Tvdb, Imdb` | Order of provider preference for series (TMDB prioritized by default) |
| **Preferred Movie Providers** | `Tmdb, Imdb` | Order of provider preference for movies |
| **Per-Item Cooldown (seconds)** | `60` | Cooldown between rename attempts |

### Custom Formats

#### Series Folder Format
You can customize the series folder name format using these placeholders:
- `{Name}` - Series name
- `{Year}` - Production year
- `{Provider}` - Provider label (tvdb, tmdb, imdb)
- `{Id}` - Provider ID

Examples:
- `{Name} ({Year}) [{Provider}-{Id}]` → `The Flash (2014) [tvdb-279121]`
- `{Name} - {Year} - {Provider}-{Id}` → `The Flash - 2014 - tvdb-279121`
- `[{Provider}] {Name} ({Year})` → `[tvdb] The Flash (2014)`

#### Season Folder Format
Customize season folder names using:
- `{Season}` - Season number (e.g., `1`, `2`)
- `{Season:00}` - Season number with zero-padding (e.g., `01`, `02`)

Examples:
- `Season {Season:00}` → `Season 01`, `Season 02`
- `S{Season}` → `S1`, `S2`
- `Season {Season}` → `Season 1`, `Season 2`

#### Episode File Format
Customize episode file names using:
- `{Series}` - Series name
- `{Season}` - Season number (e.g., `1`, `2`)
- `{Season:00}` - Season number with zero-padding (e.g., `01`, `02`)
- `{Episode}` - Episode number (e.g., `1`, `5`)
- `{Episode:00}` - Episode number with zero-padding (e.g., `01`, `05`)
- `{Title}` - Episode title
- `{Year}` - Production year

Examples:
- `S{Season:00}E{Episode:00} - {Title}` → `S01E01 - Pilot.mp4`
- `{Series} - S{Season:00}E{Episode:00} - {Title}` → `The Flash - S01E01 - Pilot.mp4`
- `E{Episode:00} - {Title}` → `E01 - Pilot.mp4` (for flat structures without season folders)

#### Movie Folder Format
Customize movie folder names using:
- `{Name}` - Movie name
- `{Year}` - Production year
- `{Provider}` - Provider label (tmdb, imdb)
- `{Id}` - Provider ID

Examples:
- `{Name} ({Year}) [{Provider}-{Id}]` → `The Matrix (1999) [tmdb-603]`
- `{Name} - {Year} - {Provider}-{Id}` → `The Matrix - 1999 - tmdb-603`
- `[{Provider}] {Name} ({Year})` → `[tmdb] The Matrix (1999)`

## Safety Features

### 1. Dry Run Mode (Default: ON)
- Logs what it would rename without actually renaming
- Check Jellyfin logs to see what would happen
- Disable only after verifying the behavior

### 2. Cooldown Period (Default: 60 seconds)
- Prevents rapid renames of the same series
- Each series can only be renamed once per cooldown period

### 3. Collision Detection
- Skips rename if target folder already exists
- Prevents overwriting existing folders

### 4. Provider ID Change Detection
- Only renames when provider IDs actually change
- Prevents unnecessary renames during normal operations

### 5. Episode Number Validation (Season-Aware)
- **Season 1:** Strict validation - Episode number in filename must match metadata (prevents incorrect renames)
- **Season 2+:** Relaxed validation - Episode number mismatch is allowed (Jellyfin metadata is authoritative)
- **Why:** Multi-season shows often have inconsistent filename numbering, but Jellyfin's metadata is correct
- Only proceeds if episode numbers match (Season 1) or if it's a Season 2+ episode (trusts metadata)

### 6. Smart Season Folder Detection
- Detects existing season folders and leaves them untouched
- Only creates "Season 01" folder for flat structures (episodes in series root)
- Automatically organizes flat structures into season folders

### 7. Movie Folder Renaming
- Only renames the movie folder, never touches the movie file itself
- Uses the same provider ID change detection logic as series
- Respects "Process During Library Scans" setting
- Works independently from series logic (no interference)

### 8. Year Correction for Known Issues
- Automatically corrects incorrect years from Jellyfin metadata for specific provider IDs
- Example: Dororo (2019) with TMDB ID 83100 - Jellyfin sometimes provides 1969 (old version), plugin corrects to 2019
- Logs show year before and after correction for transparency
- Helps prevent incorrect folder names when Jellyfin metadata has year mismatches

### 9. Smart Provider ID Detection
- Detects which provider ID the user selected during "Identify" action
- Compares current provider IDs with previous state to identify newly added/changed IDs
- Uses the user-selected provider ID for renaming instead of just the preferred list
- Falls back to preferred provider list if no user selection can be detected

### 10. Provider ID Mismatch Detection
- Detects when folder name has a different provider ID than metadata
- Forces re-rename even if folder name otherwise matches desired name
- Helps catch cases where Jellyfin initially provided wrong metadata
- Ensures folder names always match the current metadata provider IDs

### 11. Nested Season Folder Fix
- Detects and fixes nested season folder structures (e.g., "Series - Season 1\Season 01")
- Moves episodes from nested folders to parent season folder
- Renames parent season folder to standard format (e.g., "Season 01")
- Prevents "Season Unknown" issues in Jellyfin

### 12. Automatic File Unblocking (Windows)
- Automatically unblocks plugin files on Windows when downloaded from the internet
- Prevents "Application Control" blocking issues
- Uses PowerShell's `Unblock-File` cmdlet or removes `Zone.Identifier` alternate data stream
- Runs automatically during plugin initialization

## Testing

### Step 1: Test with Dry Run (Default)

1. Plugin is installed with **DryRun = true** (default)
2. Go to a series in Jellyfin
3. Press **"Identify"** and select a match
4. Check Jellyfin logs for:
   ```
   [MR] DRY RUN rename: Old Name -> The Flash (2014) [tvdb-279121]
   ```
5. Verify the rename looks correct

### Step 2: Enable Actual Renaming

1. Go to **Dashboard** > **Plugins** > **MetadataRenamer**
2. Uncheck **"Dry Run Mode"**
3. Click **Save**
4. Identify another series
5. The folder should now actually rename

### Step 3: Verify Series Renaming

1. Check the series folder on disk
2. It should be renamed to: `Series Name (Year) [provider-id]`
3. Check Jellyfin logs for:
   ```
   [MR] Renaming: Old Name -> New Name
   [MR] RENAMED OK: Old Name -> New Name
   ```

### Step 4: Test Episode Renaming

1. **Identify a series** - Episodes are automatically processed when a series is identified (no additional scan needed)
2. **OR use "Replace all metadata"** - This will process all episodes in all series in the library
3. Check episode files on disk - they should be renamed with episode numbers and titles
4. **For multi-season shows:** Verify that Season 1 and Season 2+ episodes are both processed correctly
5. Check Jellyfin logs for:
   ```
   [MR] Episode File Rename Details
   [MR] Current File: episode1.mp4
   [MR] Desired File: S01E01 - Pilot.mp4
   [MR] [DEBUG] [SEASON2+-EP-NUMBER-MISMATCH] (for Season 2+ episodes with filename mismatches)
   ```

## Logs

Check Jellyfin logs to see plugin activity:

**Windows:**
```
C:\ProgramData\Jellyfin\Server\logs\
```

**Linux:**
```
/var/log/jellyfin/
```

Look for log entries prefixed with `[MR]`:
- `[MR] Desired folder: ...` - Shows what folder name would be used
- `[MR] DRY RUN rename: ...` - Dry run mode (no actual rename)
- `[MR] Renaming: ...` - Actual rename happening
- `[MR] RENAMED OK: ...` - Rename successful
- `[MR] Episode File Rename Details` - Episode renaming information
- `[MR] Episode is already in a season folder` - Episode processing in season folders
- `[MR] [DEBUG] [PROCESS-ALL-EPISODES]` - Episode processing started
- `[MR] [DEBUG] [SEASON2+-EP-NUMBER-MISMATCH]` - Season 2+ episode with filename mismatch (proceeding with metadata)
- `[MR] [DEBUG] [SEASON2+-PATH-FIX]` - Season 2+ episode path derived from folder structure
- `[MR] [DEBUG] [BULK-PROCESSING-DETECTION]` - Bulk processing detection (Replace all metadata)
- `[MR] Skip: ...` - Why a rename was skipped
- `[MR] === Year Detection from Metadata (BEFORE Correction) ===` - Year detection before correction
- `[MR] === Year Correction Applied ===` - Year correction details (before/after)
- `[MR] FINAL Year (AFTER correction)` - Final year used for folder name
- `[MR] === Provider IDs Details ===` - All provider IDs for the item
- `[MR] ⚠️ YEAR CORRECTION` - Warning when year correction is applied
- `[MR] ⚠️ WARNING: Multiple provider IDs detected` - Multiple provider IDs found
- `[MR] ✓ Unblocked: ...` - File unblocking success (Windows)

## Troubleshooting

### Plugin Not Renaming

1. **Check Dry Run Mode**
   - If enabled, it only logs, doesn't actually rename
   - Disable it to enable actual renaming

2. **Check Provider IDs**
   - Series must have at least one provider ID (Tvdb, Tmdb, Imdb)
   - Use "Identify" to match the series if it doesn't have IDs

3. **Check Provider ID Change Detection**
   - If `OnlyRenameWhenProviderIdsChange` is enabled, it only renames when IDs change
   - Try changing the identity to trigger a rename

4. **Check Logs**
   - Look for `[MR]` entries to see why renames are skipped
   - Common reasons: "ProviderIds unchanged", "no ProviderIds", "missing name/year"

### Folder Not Renaming After Identify

1. **Wait for cooldown** - 60 seconds between attempts per series
2. **Check if provider IDs actually changed** - Plugin only renames when IDs change
3. **Check logs** - Look for skip reasons
4. **Verify series has name and year** - Both are required

### Episode Files Not Renaming

1. **Identify the series first** - Episodes are processed automatically when a series is identified (provider IDs change)
2. **Use "Replace all metadata" for bulk processing** - This will process all episodes in all series in the library
3. **Check episode renaming is enabled** - Go to plugin settings and verify "Rename Episode Files" is enabled
4. **Check episode metadata** - Episodes must have episode number and title in Jellyfin metadata
5. **Check episode number validation**:
   - **Season 1:** If filename episode number doesn't match metadata, rename is skipped (strict safety check)
   - **Season 2+:** Episode number mismatch is allowed (Jellyfin metadata is authoritative)
6. **Check logs** - Look for `[MR] Episode File Rename Details` entries to see what's happening
7. **Multi-season shows:** The plugin works for all seasons (1, 2, 3, 4, 5, etc.) - check logs for `[MR] [DEBUG] [SEASON2+-EP-NUMBER-MISMATCH]` for Season 2+ episodes

### Folder Names Not Updating During Library Scans

1. **Normal scans only process identified shows** - Use "Identify" on individual series to trigger renaming
2. **Use "Replace all metadata" for bulk updates** - This will process all series in the library automatically
3. **Check "Only Rename When Provider IDs Change" setting** - If enabled, items will only be processed if provider IDs changed (normal behavior)
4. **Check "Process During Library Scans" setting** - If enabled, library scans will update all items to match metadata
5. **Check logs** - Look for `[MR]` entries to see what's happening. Look for `[MR] [DEBUG] [BULK-PROCESSING-DETECTION]` when using "Replace all metadata"

### Wrong Metadata Match Selected (e.g., Old vs New Series)

**Issue:** When using "Identify", you selected the wrong match (e.g., old "Dororo and Hyakkimaru" instead of new "Dororo (2019)").

**Solution:**
1. **Re-identify with the correct match:**
   - Go to the series/movie in Jellyfin
   - Click the **three dots** (⋮) menu
   - Select **"Identify"**
   - **Search for the correct entry** (e.g., "Dororo 2019" or "Dororo TV")
   - **Select the correct match** (check the year and description)
   - Click **"OK"** or **"Save"**

2. **Verify provider IDs in logs:**
   - After re-identifying, check Jellyfin logs
   - Look for `[MR] === Provider IDs Details ===` entries
   - Verify the provider IDs match the correct entry:
     - **Dororo (2019)**: Should have `Tmdb=83100` (or similar)
     - **Old Dororo**: Will have different IDs

3. **Check the folder name:**
   - After re-identifying, the folder should rename to match the correct metadata
   - Example: `Dororo (2019) [tmdb-83100]` (correct) vs `Dororo and Hyakkimaru [old-id]` (wrong)

**Note:** The plugin uses whatever provider IDs Jellyfin assigns after you select a match. If you select the wrong match in Jellyfin's "Identify" screen, the plugin will use those incorrect IDs. Always verify you're selecting the correct entry (check year, description, and poster) before confirming.

**Important:** Jellyfin may assign multiple provider IDs (e.g., both TVDB and TMDB) even when you select a single match. The plugin uses smart detection to identify which provider ID you selected during "Identify" by comparing current and previous provider IDs. If no user selection can be detected, it falls back to the "Preferred Series Providers" setting. By default, TMDB is prioritized over TVDB. If you're getting the wrong ID, check your plugin settings and adjust the provider preference order. Check the logs for `[MR] ⚠️ WARNING: Multiple provider IDs detected` to see if this is happening.

**Year Correction:** The plugin automatically corrects known year mismatches. For example, if Jellyfin provides year 1969 for Dororo (2019) with TMDB ID 83100, the plugin will correct it to 2019. Check logs for `[MR] ⚠️ YEAR CORRECTION` entries to see when corrections are applied. Check the logs for `[MR] ⚠️ WARNING: Multiple provider IDs detected` to see if this is happening.

### Movie Folders Not Renaming

1. **Check "Rename Movie Folders" setting** - Go to plugin settings and verify "Rename Movie Folders" is enabled
2. **Check movie metadata** - Movies must have name and year in Jellyfin metadata
3. **Check provider IDs** - Movies must have at least one provider ID (Tmdb, Imdb) if "Require Provider ID Match" is enabled
4. **Perform a library scan** - If "Process During Library Scans" is enabled, perform a full library scan
5. **Use "Identify"** - Press "Identify" on the movie and select the correct match to trigger renaming
6. **Check logs** - Look for `[MR] Processing Movie` entries to see what's happening

**Note:** The plugin only renames movie folders, not the movie files themselves. The movie file (e.g., `The Matrix.mkv`) will remain unchanged.

### Want to Rename Without Provider ID Change?

If you want the plugin to rename even when provider IDs don't change:

1. Go to plugin settings
2. Uncheck **"Only Rename When Provider IDs Change"**
3. **Warning:** This will rename on every ItemUpdated event, which may cause frequent renames during scans

## Requirements

- **Jellyfin Server**: 10.10.x (plugin built for 10.10.0.0)
- **.NET**: 9.0
- **Platform**: Windows, Linux, macOS

## Repository

**GitHub Repository:**
https://github.com/loguefx/Jellyfin_MetaData_Renamer

**Plugin Repository URL (for Jellyfin):**
```
https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
```

## Development

### Building

```bash
dotnet restore
dotnet build -c Release
```

### Project Structure

```
MetadataRenamer/
├── Jellyfin.Plugin.Template/
│   ├── Configuration/
│   │   ├── PluginConfiguration.cs    # Plugin settings
│   │   └── configPage.html          # Web UI
│   ├── Services/
│   │   ├── RenameCoordinator.cs     # Main logic
│   │   ├── PathRenameService.cs     # File system operations
│   │   ├── ProviderIdHelper.cs      # Provider ID utilities
│   │   └── SafeName.cs              # Name formatting
│   └── Plugin.cs                    # Plugin entry point
└── repository/
    ├── manifest.json                # Plugin repository manifest
    └── Jellyfin.Plugin.MetadataRenamer.dll
```

## License

See [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Support

For issues, questions, or feature requests, please open an issue on GitHub:
https://github.com/loguefx/Jellyfin_MetaData_Renamer/issues
