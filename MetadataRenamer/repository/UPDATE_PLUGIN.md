# How to Update the Plugin

## Current Issue: "NotSupported" Status

The plugin shows "NotSupported" because it was built for .NET 9.0, but Jellyfin 10.10.7 requires .NET 8.0.

## Solution: Update to .NET 8.0 Build

### Option 1: Reinstall from Catalog (Recommended)

1. **Uninstall current plugin:**
   - Go to Dashboard > Plugins
   - Find MetadataRenamer
   - Click Uninstall (or delete from plugins folder)

2. **Wait 1-2 minutes** for GitHub to update

3. **Reinstall:**
   - Go to Dashboard > Plugins > Catalog
   - Find MetadataRenamer
   - Click Install
   - Restart Jellyfin

4. **Verify:**
   - Status should show "Active" (not "NotSupported")
   - Configuration page should load

### Option 2: Manual Update

1. **Stop Jellyfin service**

2. **Download new DLL:**
   - URL: https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.MetadataRenamer.zip
   - Extract the DLL from the ZIP

3. **Replace DLL:**
   - Location: `C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer\`
   - Replace `Jellyfin.Plugin.MetadataRenamer.dll` with the new one

4. **Start Jellyfin service**

5. **Verify:**
   - Status should show "Active"
   - Configuration page should load

## What Changed

- **TargetFramework**: Changed from `net9.0` to `net8.0`
- **Embedded Resource**: Added LogicalName for correct config page loading
- **Result**: Plugin now compatible with Jellyfin 10.10.7

## After Update

Once the plugin shows "Active":
1. Click on the plugin to open configuration
2. You should see all settings (Dry Run, Format, etc.)
3. Test with "Identify" on a series
4. Check logs for `[MR]` entries
