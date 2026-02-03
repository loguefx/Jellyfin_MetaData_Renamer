# MetadataRenamer - Jellyfin Plugin

Automatically renames series folders when metadata is identified in Jellyfin, using the format: `{SeriesName} ({Year}) [{provider}-{id}]`

## Examples

- `The Flash (2014) [tvdb-279121]`
- `The Office (2005) [tmdb-2316]`
- `Breaking Bad (2008) [imdb-tt0903747]`

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

MetadataRenamer listens to Jellyfin's library update events and automatically renames series folders when metadata is identified or changed. It uses smart detection to only rename when it's safe to do so.

### When Does It Rename?

The plugin will **only rename** when ALL of these conditions are met:

1. ✅ **Item is a Series** (movies and other types are ignored)
2. ✅ **Series has a valid folder path** (folder exists on disk)
3. ✅ **Series has Name and ProductionYear** (required for naming)
4. ✅ **Series has at least one Provider ID** (Tvdb, Tmdb, Imdb, etc.)
5. ✅ **Provider IDs have changed** (if `OnlyRenameWhenProviderIdsChange` is enabled - **default: true**)

### Key Feature: Smart Detection

**Default Setting: `OnlyRenameWhenProviderIdsChange = true`**

This is the "smart detection" feature that prevents unnecessary renames:

- The plugin tracks a hash of provider IDs for each series
- It only renames when the hash **changes**
- This infers that "Identify" or "Refresh Metadata" just happened

### Real-World Scenarios

#### ✅ Scenario 1: New Library Scan
1. You add a new library with series folders
2. Jellyfin scans and automatically matches some series
3. **Result:** Plugin detects provider ID changes → Renames folders to proper format

#### ✅ Scenario 2: Manual Identify
1. You have a series "The Flash" that's not matched
2. You press **"Identify"** and select "The Flash (2014)"
3. **Result:** Provider IDs are added → Plugin renames to `The Flash (2014) [tvdb-279121]`

#### ✅ Scenario 3: Change Identity
1. Series is currently "The Flash (2014) [tvdb-279121]"
2. You press **"Identify"** and change it to "The Flash (2023)"
3. **Result:** Provider IDs change → Plugin renames to `The Flash (2023) [tvdb-XXXXX]`

#### ❌ Scenario 4: Regular Library Scan (No Changes)
1. Series already has provider IDs: `The Flash (2014) [tvdb-279121]`
2. Regular library scan runs (no metadata changes)
3. **Result:** Provider IDs unchanged → Plugin skips (no rename)

### What This Means

**WILL Rename:**
- ✅ When you press **"Identify"** and select a match
- ✅ When you press **"Identify"** and change to a different match
- ✅ When you manually refresh metadata and it finds new provider IDs
- ✅ During library scans if series get matched for the first time

**WON'T Rename:**
- ❌ During normal library scans (if provider IDs don't change)
- ❌ On every metadata update (only when provider IDs actually change)
- ❌ Movies or other non-series items
- ❌ Series without provider IDs (unless `RequireProviderIdMatch` is disabled)

## Configuration

Access plugin settings: **Dashboard** > **Plugins** > **MetadataRenamer**

### Settings

| Setting | Default | Description |
|---------|---------|-------------|
| **Enabled** | `true` | Turn the plugin on/off |
| **Dry Run Mode** | `true` | Log only, don't actually rename (start here!) |
| **Rename Series Folders** | `true` | Enable/disable series renaming |
| **Require Provider ID Match** | `true` | Only rename series with provider IDs |
| **Only Rename When Provider IDs Change** | `true` | Smart detection - only rename when IDs change |
| **Series Folder Format** | `{Name} ({Year}) [{Provider}-{Id}]` | Customize naming format |
| **Preferred Series Providers** | `Tvdb, Tmdb, Imdb` | Order of provider preference |
| **Per-Item Cooldown (seconds)** | `60` | Cooldown between rename attempts |

### Custom Format

You can customize the folder name format using these placeholders:
- `{Name}` - Series name
- `{Year}` - Production year
- `{Provider}` - Provider label (tvdb, tmdb, imdb)
- `{Id}` - Provider ID

Examples:
- `{Name} ({Year}) [{Provider}-{Id}]` → `The Flash (2014) [tvdb-279121]`
- `{Name} - {Year} - {Provider}-{Id}` → `The Flash - 2014 - tvdb-279121`
- `[{Provider}] {Name} ({Year})` → `[tvdb] The Flash (2014)`

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

### Step 3: Verify

1. Check the series folder on disk
2. It should be renamed to: `Series Name (Year) [provider-id]`
3. Check Jellyfin logs for:
   ```
   [MR] Renaming: Old Name -> New Name
   [MR] RENAMED OK: Old Name -> New Name
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
