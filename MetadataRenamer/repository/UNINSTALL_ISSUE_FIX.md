# Plugin Uninstall Issue - Known Jellyfin Limitation

## Problem

After uninstalling the plugin and restarting Jellyfin, the plugin folder may still exist, causing:
- Plugin shows as "deleted" in Plugins list
- Catalog still shows "Uninstall" button (doesn't work)
- Folder `MetadataRenamer_1.0.0.0` still exists in plugins directory

## Root Cause

This is a **Jellyfin core limitation**, not a plugin bug:

1. **.NET Assembly Locking**: When a plugin is loaded, the DLL is locked by the .NET runtime
2. **Cannot Unload**: .NET assemblies cannot be unloaded without unloading the entire AppDomain (requires process restart)
3. **deleteOnStartup Failure**: Jellyfin marks the folder for deletion on next startup, but if the DLL is still locked (by antivirus, file indexing, or .NET), the deletion fails silently
4. **Folder Persists**: The folder remains, so Jellyfin's catalog thinks the plugin is still installed

## Solution: Manual Cleanup Script

Use this PowerShell script to completely remove the plugin:

```powershell
# Run as Administrator
$pluginFolder = "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0"
$serviceName = "JellyfinServer"

# Stop Jellyfin service
Write-Host "Stopping Jellyfin service..." -ForegroundColor Cyan
Stop-Service -Name $serviceName -Force
Start-Sleep -Seconds 5

# Kill any remaining Jellyfin processes
Get-Process -Name "jellyfin" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# Delete plugin folder
if (Test-Path $pluginFolder) {
    Write-Host "Deleting plugin folder..." -ForegroundColor Cyan
    Remove-Item $pluginFolder -Recurse -Force
    Write-Host "✅ Plugin folder deleted" -ForegroundColor Green
} else {
    Write-Host "⚠️  Plugin folder not found (already deleted)" -ForegroundColor Yellow
}

# Start Jellyfin service
Write-Host "Starting Jellyfin service..." -ForegroundColor Cyan
Start-Service -Name $serviceName
Write-Host "✅ Done!" -ForegroundColor Green
```

## Alternative: Use Provided Script

Use the provided `uninstall-plugin.ps1` script:

```powershell
# Run PowerShell as Administrator
cd "path\to\repository"
.\uninstall-plugin.ps1
```

## Why This Happens

The logs show:
```
[WRN] Unable to delete "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0", so marking as deleteOnStartup.
```

Then after restart:
```
[WRN] Unable to delete "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0"
System.UnauthorizedAccessException: Access to the path 'Jellyfin.Plugin.MetadataRenamer.dll' is denied.
```

The DLL is still locked, preventing folder deletion. This is expected .NET behavior - assemblies remain loaded until the process fully terminates.

## Prevention

Unfortunately, this cannot be prevented from within the plugin. It's a fundamental .NET limitation. The plugin does everything it can:
- ✅ Properly disposes all resources
- ✅ Unsubscribes from events
- ✅ Clears internal state
- ✅ Requests garbage collection

But the DLL lock persists until Jellyfin fully restarts.

## Workaround

The manual cleanup script above is the most reliable solution. Jellyfin's `deleteOnStartup` mechanism works in most cases, but can fail if:
- Antivirus is scanning the DLL
- Windows file indexing is active
- File system delays
- .NET runtime hasn't fully released the assembly
