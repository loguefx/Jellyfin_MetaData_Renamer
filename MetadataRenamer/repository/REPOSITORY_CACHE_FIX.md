# Fix Repository Caching Issues

## The Problem
Jellyfin caches plugin manifests aggressively. When you update the manifest on GitHub, Jellyfin may continue using the cached version.

## Root Cause
The manifest was saved as an **object** `{...}` instead of an **array** `[{...}]`. Jellyfin expects an array format.

## Solution Applied

### 1. Fixed Manifest Format
- Changed from object to array format
- Regenerated using `generate-manifest.ps1`
- Pushed to GitHub

### 2. Force Jellyfin to Refresh

**Option A: Remove and Re-add Repository**
1. Dashboard > Plugins > Repositories
2. Remove the repository
3. Wait 30 seconds
4. Re-add: `https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json`
5. Wait 60 seconds

**Option B: Clear Jellyfin Cache**
1. Stop Jellyfin service
2. Delete: `C:\ProgramData\Jellyfin\Server\cache\*`
3. Start Jellyfin service
4. Re-add repository

**Option C: Use Versioned URL (Future)**
If caching persists, we can add a version query parameter:
```
https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json?v=2
```

## Verify Manifest is Correct

Test the URL in browser:
```
https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
```

**Should start with:** `[` (array)
**Should NOT start with:** `{` (object)

## After Fix
- Manifest is array format ✅
- Fresh timestamp on each generation ✅
- Proper cache headers from GitHub ✅
- Jellyfin should pick up changes after cache clear ✅
