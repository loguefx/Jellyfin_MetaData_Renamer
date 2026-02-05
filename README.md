# MetadataRenamer - Jellyfin Plugin

Automatically renames series folders, season folders, episode files, and movie folders when metadata is identified in Jellyfin, using metadata from providers like TVDB, TMDB, and IMDB.

## ⚠️ Important: Library Scan Required for Episode Renaming

**For episode file renaming to take effect, you must perform a library scan after identifying or updating metadata.** The plugin processes episodes when Jellyfin fires `ItemUpdated` events, which occur during library scans. Simply identifying a series will rename the series folder, but episode files will be renamed during the next library scan.

## 🔄 Automatic Processing During Library Scans

**New Feature:** The plugin can now automatically update folder names and episode files during library scans (full or regular) to match metadata. This is controlled by the **"Process During Library Scans"** configuration option:

- **Enabled (default):** Library scans will automatically update all series folders, season folders, and episode files to match their metadata, even if provider IDs haven't changed
- **Disabled:** Library scans won't automatically update items; only the "Identify" flow will trigger updates (when provider IDs change)

**Note:** The "Identify" flow always works regardless of this setting - when you press "Identify" and select metadata, the plugin will process the item.

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
3. ✅ **Episode number matches filename** (safety check prevents incorrect renames)
4. ✅ **Library scan has occurred** (episodes are processed during library scans via `ItemUpdated` events) **OR** "Process During Library Scans" is enabled

**Note:** Episode renaming requires a library scan to trigger. After identifying a series, perform a library scan to rename episode files. If "Process During Library Scans" is enabled, full library scans will automatically update all episode files to match metadata.

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

#### ✅ Scenario 5: Existing Season Folders
1. Series already has "Season 1", "Season 2" folders
2. You identify the series and perform a library scan
3. **Result:**
   - Series folder renamed: `The Flash (2014) [tvdb-279121]`
   - Season folders left untouched (no changes)
   - Episodes renamed using their actual season numbers: `S01E01 - Pilot.mp4`, `S02E05 - The Darkness and the Light.mp4`, etc.

#### ❌ Scenario 6: Regular Library Scan (No Changes)
1. Series already has provider IDs: `The Flash (2014) [tvdb-279121]`
2. Regular library scan runs (no metadata changes)
3. **Result:** Provider IDs unchanged → Plugin skips series rename (no rename)

### What This Means

**WILL Rename:**
- ✅ **Series folders:** When you press **"Identify"** and select a match
- ✅ **Series folders:** When you press **"Identify"** and change to a different match
- ✅ **Series folders:** When you manually refresh metadata and it finds new provider IDs
- ✅ **Series folders:** During library scans if series get matched for the first time
- ✅ **Series folders:** During full library scans if "Process During Library Scans" is enabled (updates all folders to match metadata)
- ✅ **Episode files:** During library scans when episodes have valid metadata (episode number and title)
- ✅ **Episode files:** During full library scans if "Process During Library Scans" is enabled (updates all episode files to match metadata)
- ✅ **Season folders:** When season metadata is available and season renaming is enabled
- ✅ **Season folders:** During full library scans if "Process During Library Scans" is enabled
- ✅ **Movie folders:** When you press **"Identify"** and select a match
- ✅ **Movie folders:** When you press **"Identify"** and change to a different match
- ✅ **Movie folders:** During library scans if movies get matched for the first time
- ✅ **Movie folders:** During full library scans if "Process During Library Scans" is enabled (updates all movie folders to match metadata)

**WON'T Rename:**
- ❌ During normal library scans (if provider IDs don't change and "Process During Library Scans" is disabled)
- ❌ On every metadata update (only when provider IDs actually change, unless "Process During Library Scans" is enabled)
- ❌ Items without provider IDs (unless `RequireProviderIdMatch` is disabled)
- ❌ Episodes if episode number in filename doesn't match metadata (safety check)
- ❌ Episodes without performing a library scan (episodes are processed during scans)
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
| **Process During Library Scans** | `true` | When enabled, library scans (full or regular) will automatically update folder names and episode files to match metadata. When disabled, only the "Identify" flow triggers updates. |
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

### 5. Episode Number Validation
- Validates episode number in filename matches metadata episode number
- Prevents incorrect renames (e.g., won't rename "episode 1" to "episode 5" if metadata is wrong)
- Only proceeds if episode numbers match or filename has no episode number

### 6. Smart Season Folder Detection
- Detects existing season folders and leaves them untouched
- Only creates "Season 01" folder for flat structures (episodes in series root)
- Automatically organizes flat structures into season folders

### 7. Movie Folder Renaming
- Only renames the movie folder, never touches the movie file itself
- Uses the same provider ID change detection logic as series
- Respects "Process During Library Scans" setting
- Works independently from series logic (no interference)

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

1. **Important:** Perform a library scan after identifying the series
2. Go to **Dashboard** > **Libraries** > Select your library > **Scan Library**
3. Wait for the scan to complete
4. Check episode files on disk - they should be renamed with episode numbers and titles
5. Check Jellyfin logs for:
   ```
   [MR] Episode File Rename Details
   [MR] Current File: episode1.mp4
   [MR] Desired File: S01E01 - Pilot.mp4
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
- `[MR] Skip: ...` - Why a rename was skipped

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

1. **Perform a library scan** - Episode renaming requires a library scan to trigger
2. **Check "Process During Library Scans" setting** - If disabled, only the "Identify" flow will trigger updates. Enable it to process episodes during library scans.
3. **Check episode renaming is enabled** - Go to plugin settings and verify "Rename Episode Files" is enabled
4. **Check episode metadata** - Episodes must have episode number and title in Jellyfin metadata
5. **Check episode number validation** - If filename episode number doesn't match metadata, rename is skipped (safety feature)
6. **Check logs** - Look for `[MR] Episode File Rename Details` entries to see what's happening

### Folder Names Not Updating During Library Scans

1. **Check "Process During Library Scans" setting** - This must be enabled for automatic updates during library scans
2. **Check "Only Rename When Provider IDs Change" setting** - If enabled, items will only be processed if provider IDs changed OR "Process During Library Scans" is enabled
3. **Perform a full library scan** - Go to Dashboard > Libraries > Select your library > Scan Library (Full)
4. **Check logs** - Look for `[MR] ProcessDuringLibraryScans` entries to see if the setting is being applied

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

**Important:** Jellyfin may assign multiple provider IDs (e.g., both TVDB and TMDB) even when you select a single match. The plugin uses the "Preferred Series Providers" setting to determine which ID to use. By default, TMDB is prioritized over TVDB. If you're getting the wrong ID, check your plugin settings and adjust the provider preference order. Check the logs for `[MR] ⚠️ WARNING: Multiple provider IDs detected` to see if this is happening.

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
