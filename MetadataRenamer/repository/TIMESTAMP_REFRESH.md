# Fix: Timestamp Not Updating in Jellyfin UI

## The Problem

After updating `manifest.json` with a new timestamp and pushing to GitHub, Jellyfin still shows the old timestamp in the plugin detail page.

## Root Cause

**Jellyfin aggressively caches plugin manifests.** When you update the manifest on GitHub, Jellyfin doesn't automatically know to refresh. The manifest is cached locally and only refreshed when:
1. The repository is removed and re-added
2. Jellyfin's cache is cleared
3. Jellyfin is restarted (sometimes)

## Solution: Force Jellyfin to Refresh the Manifest

### Method 1: Remove and Re-add Repository (Recommended)

1. Go to **Dashboard > Plugins > Repositories**
2. Find your repository entry (should show the manifest URL)
3. Click **Delete/Remove** (trash icon or X button)
4. **Wait 30 seconds**
5. Click the **+** button to add a new repository
6. Enter the repository URL:
   ```
   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
   ```
7. Click **OK**
8. **Wait 60 seconds** for Jellyfin to fetch the fresh manifest
9. Go to **Dashboard > Plugins > Catalog**
10. Find **MetadataRenamer** and check the revision timestamp - it should now show the updated time

### Method 2: Clear Jellyfin Cache + Re-add Repository

If Method 1 doesn't work:

1. **Stop Jellyfin service**
   ```powershell
   Stop-Service JellyfinServer
   ```

2. **Clear Jellyfin cache**
   ```powershell
   Remove-Item "C:\ProgramData\Jellyfin\Server\cache\*" -Recurse -Force
   ```
   (Linux: `rm -rf /var/cache/jellyfin/*`)

3. **Start Jellyfin service**
   ```powershell
   Start-Service JellyfinServer
   ```

4. **Remove and re-add repository** (follow Method 1 steps 1-10)

### Method 3: Use Cache-Busting URL (Future Enhancement)

If caching persists, you can add a query parameter to force refresh:

1. Remove the repository
2. Re-add with a versioned URL:
   ```
   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json?v=2
   ```
   (Change `v=2` to `v=3`, `v=4`, etc. each time you update)

## Verify the Fix

After refreshing:

1. Go to **Dashboard > Plugins > Catalog**
2. Find **MetadataRenamer**
3. Click on it to view details
4. Check the **Revision History** section
5. The timestamp should match the current time (or close to when you regenerated the manifest)

## Why This Happens

Jellyfin caches plugin manifests to:
- Reduce network requests
- Improve performance
- Work offline

However, this means manual intervention is required when manifests are updated. This is expected behavior and not a bug.

## Prevention

To avoid this issue in the future:
- Only update timestamps when you actually release a new version
- Consider incrementing the version number when making significant changes (this forces Jellyfin to detect updates)
- Document that users need to refresh repositories after updates

## Technical Details

- **GitHub URL**: Always has the latest timestamp ✅
- **Jellyfin Cache**: May have old timestamp until refreshed ⚠️
- **Solution**: Manual refresh required (by design)
