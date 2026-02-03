# Complete Reinstall Steps (Automatic Installation)

## Current Situation
- Plugin shows "NotSupported" (old .NET 9.0 build installed)
- Configuration page doesn't load
- Catalog shows "Installed" because plugin folder still exists
- Jellyfin is using cached manifest with old checksum

## Solution: Complete Clean Reinstall

### Step 1: Remove Plugin Folder
**This is required** - Jellyfin checks folder existence to determine if plugin is installed.

1. **Stop Jellyfin service**
2. **Delete plugin folder:**
   ```powershell
   Remove-Item "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer" -Recurse -Force
   ```
3. **Start Jellyfin service**

### Step 2: Clear Jellyfin Cache
Jellyfin caches plugin manifests. Clear it to get fresh manifest.

1. **Stop Jellyfin service**
2. **Clear cache:**
   ```powershell
   Remove-Item "C:\ProgramData\Jellyfin\Server\cache\*" -Recurse -Force
   ```
3. **Start Jellyfin service**

### Step 3: Remove and Re-add Repository
This forces Jellyfin to fetch the fresh manifest from GitHub.

1. Go to **Dashboard > Plugins > Repositories**
2. **Remove** the existing repository entry
3. **Wait 30 seconds**
4. Click **+** to add repository:
   ```
   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
   ```
5. Click **OK**
6. **Wait 60 seconds** for Jellyfin to fetch the manifest

### Step 4: Install from Catalog
1. Go to **Dashboard > Plugins > Catalog**
2. Find **MetadataRenamer**
3. Should now show **"Install"** button (not "Installed")
4. Click **Install**
5. Wait for installation to complete

### Step 5: Restart Jellyfin
1. **Stop** Jellyfin service
2. Wait 10 seconds
3. **Start** Jellyfin service

### Step 6: Verify
1. Go to **Dashboard > Plugins**
2. MetadataRenamer should show:
   - ✅ **Status: Active** (not "NotSupported")
   - ✅ **Configuration page loads** when you click on it
   - ✅ **All settings visible** (Dry Run, Format, etc.)

## What This Does

1. **Removes old plugin** - Deletes the .NET 9.0 build
2. **Clears cache** - Forces Jellyfin to forget old manifest
3. **Fresh manifest** - Downloads new manifest with MD5 checksum
4. **New installation** - Installs .NET 8.0 build that works with Jellyfin 10.10.7

## After Successful Installation

The plugin will:
- Show "Active" status
- Display configuration page
- Work correctly with Jellyfin 10.10.7
- Rename folders when you use "Identify"
