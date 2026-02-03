# Plugin Uninstall Works When Active

## The Issue

The plugin uninstall is failing because **the .NET 9.0 build is still installed**, which fails to load. When a plugin fails to load, Jellyfin can't properly uninstall it through the UI.

## Root Cause

1. **Plugin is .NET 9.0** (incompatible with Jellyfin 10.10.7)
2. **Jellyfin can't load it** → marks as "NotSupported" or "Disabled"
3. **Jellyfin doesn't track failed plugins** for uninstallation
4. **Uninstall button doesn't work** for plugins that failed to load

## The Solution

**The plugin code is already correct:**
- ✅ Implements `IDisposable` properly
- ✅ Unsubscribes from events in `Dispose()`
- ✅ No file handles or locks
- ✅ Proper cleanup

**The repository is correct:**
- ✅ Manifest is array format
- ✅ ZIP package contains .NET 8.0 DLL
- ✅ MD5 checksum is correct
- ✅ All metadata matches

**What's needed:**
1. **Install the .NET 8.0 build** from the repository
2. **Plugin will load successfully** → shows "Active" status
3. **Uninstall will work automatically** through Jellyfin's UI

## How Uninstall Works

### When Plugin is Active (Loaded Successfully):
✅ **Uninstall button works**
✅ **Jellyfin removes plugin folder automatically**
✅ **Catalog shows "Install" after uninstall**
✅ **Can reinstall without manual steps**

### When Plugin is NotSupported (Failed to Load):
❌ **Uninstall button may not work**
❌ **Plugin folder persists**
❌ **Manual removal needed**

## Current Status

- ✅ Plugin code: Correct (proper cleanup)
- ✅ Repository: Correct (.NET 8.0 build ready)
- ❌ Installed version: Still .NET 9.0 (needs update)

## Next Steps

1. **Remove the old .NET 9.0 build:**
   - Stop Jellyfin service
   - Delete: `C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0`
   - Start Jellyfin service

2. **Install .NET 8.0 build:**
   - Dashboard > Plugins > Catalog
   - Install MetadataRenamer
   - Restart Jellyfin

3. **Verify:**
   - Plugin shows "Active" (not "NotSupported")
   - Configuration page loads

4. **Test Uninstall:**
   - Dashboard > Plugins
   - Click three dots on MetadataRenamer
   - Click "Uninstall"
   - Should work automatically!

## Why This Works

Once the plugin loads successfully:
- Jellyfin properly tracks it
- Jellyfin can call `Dispose()` on uninstall
- Plugin cleans up resources
- Jellyfin removes the folder
- Everything works like other plugins

The plugin code doesn't need changes - it's already correct. The only issue is getting the right version installed.
