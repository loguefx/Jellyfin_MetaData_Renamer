# Force Jellyfin to Update Plugin Manifest

If Jellyfin is not picking up the updated plugin manifest timestamp, follow these steps:

## Method 1: Clear Jellyfin Plugin Cache (Recommended)

1. **Stop Jellyfin Server**

2. **Delete the plugin cache directory:**
   - **Windows**: `C:\ProgramData\Jellyfin\Server\cache\plugins\`
   - **Linux**: `/var/cache/jellyfin/plugins/` or `/var/lib/jellyfin/cache/plugins/`
   - **Docker**: Inside the container at `/cache/plugins/`

3. **Delete cached plugin manifests:**
   - Look for any files related to your repository URL in the cache directory
   - Delete any `.json` files that might be cached manifests

4. **Start Jellyfin Server**

5. **Remove and Re-add Repository:**
   - Go to **Dashboard** > **Plugins** > **Repositories**
   - Remove the repository
   - Wait 10 seconds
   - Add it back: `https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json`

6. **Check Plugin Catalog:**
   - Go to **Dashboard** > **Plugins** > **Catalog**
   - The timestamp should now show the latest value

## Method 2: Manual Cache Clear via PowerShell (Windows)

Run this PowerShell command as Administrator:

```powershell
# Stop Jellyfin service
Stop-Service Jellyfin -ErrorAction SilentlyContinue

# Clear plugin cache
$cachePath = "C:\ProgramData\Jellyfin\Server\cache\plugins"
if (Test-Path $cachePath) {
    Remove-Item "$cachePath\*" -Recurse -Force
    Write-Host "Cache cleared: $cachePath"
}

# Start Jellyfin service
Start-Service Jellyfin
```

## Method 3: Verify Manifest on GitHub

Before clearing cache, verify the manifest is updated on GitHub:

1. Open: https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
2. Check the `timestamp` field - it should match the latest build time
3. If it doesn't match, wait a few minutes for GitHub to update, then try again

## Method 4: Use Cache-Busting URL (Temporary)

If the above methods don't work, try adding a query parameter to force a fresh download:

1. Remove the repository
2. Add it back with a cache-busting parameter:
   ```
   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json?v=2
   ```
   (Change `v=2` to `v=3`, `v=4`, etc. each time you need to force refresh)

## Troubleshooting

- **If timestamp still doesn't update:** The issue might be browser cache. Try:
  - Hard refresh the Jellyfin web interface (Ctrl+F5)
  - Clear browser cache
  - Use incognito/private browsing mode

- **If plugin doesn't appear:** Check Jellyfin logs for errors:
  - **Windows**: `C:\ProgramData\Jellyfin\Server\log\`
  - Look for errors related to plugin repository loading

- **Verify DLL was updated:** The checksum in the manifest should match the DLL. If you see a different checksum, the DLL wasn't updated properly.

## Current Manifest Info

- **Latest Timestamp**: Check the manifest.json file for the current timestamp
- **Checksum**: `091171ADF8BB6B4CD3CFD170E00844F6` (as of last build)
- **Version**: `1.0.0.0`
