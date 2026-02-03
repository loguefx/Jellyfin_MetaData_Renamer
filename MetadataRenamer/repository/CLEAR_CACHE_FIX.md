# Fix: Jellyfin Using Cached Manifest

## The Problem

Jellyfin is caching the old manifest with SHA256 checksum. Even though GitHub has the correct MD5 checksum, Jellyfin is still using the cached version.

## Solution: Clear Jellyfin's Cache

### Step 1: Remove Repository

1. Go to **Dashboard > Plugins > Repositories**
2. Find your repository entry
3. Click **Delete/Remove** (trash icon or X)
4. Confirm removal

### Step 2: Clear Jellyfin Cache (Important!)

Jellyfin caches plugin manifests. You need to clear this cache:

**Option A: Clear via Jellyfin (if available)**
- Some Jellyfin versions have a "Clear Cache" option in Dashboard

**Option B: Clear cache files manually**
1. **Stop Jellyfin service**
2. Delete cache files:
   ```
   C:\ProgramData\Jellyfin\Server\cache\*
   ```
   (Or just the plugin-related cache files if you can identify them)
3. **Start Jellyfin service**

**Option C: Restart and wait**
- Sometimes Jellyfin clears cache on restart
- Stop Jellyfin
- Wait 2-3 minutes
- Start Jellyfin

### Step 3: Re-add Repository

1. Go to **Dashboard > Plugins > Repositories**
2. Click **+** button
3. Enter:
   ```
   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
   ```
4. Click **OK**
5. **Wait 60 seconds** for Jellyfin to fetch the fresh manifest

### Step 4: Install from Catalog

1. Go to **Dashboard > Plugins > Catalog**
2. Find **MetadataRenamer**
3. Click **Install**
4. Should install successfully now

### Step 5: Restart Jellyfin

1. **Stop** Jellyfin service
2. Wait 10 seconds
3. **Start** Jellyfin service
4. Check plugin status - should show "Active"

## Why This Happens

Jellyfin caches plugin manifests to reduce network requests. When you update the manifest on GitHub, Jellyfin doesn't automatically know to refresh. You need to:
- Remove the repository (tells Jellyfin to forget the cached manifest)
- Clear cache (ensures old data is gone)
- Re-add repository (forces fresh download)

## Verification

After re-adding the repository, you can verify Jellyfin has the correct manifest by checking the logs. Look for:
- No checksum mismatch errors
- Successful installation
- Plugin shows "Active" status
