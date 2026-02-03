# Force Reinstall Plugin (Fix NotSupported Status)

## Current Problem
Plugin shows "NotSupported" status because the old .NET 9.0 build is still installed.

## Solution: Complete Clean Reinstall

### Step 1: Stop Jellyfin
```powershell
Stop-Service JellyfinServer
```

### Step 2: Remove Plugin Folder (REQUIRED)
```powershell
Remove-Item "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer" -Recurse -Force
```

### Step 3: Clear Cache
```powershell
Remove-Item "C:\ProgramData\Jellyfin\Server\cache\*" -Recurse -Force
```

### Step 4: Start Jellyfin
```powershell
Start-Service JellyfinServer
```

### Step 5: Remove and Re-add Repository
1. Dashboard > Plugins > Repositories
2. **Remove** the repository
3. Wait 30 seconds
4. Click **+** to add:
   ```
   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
   ```
5. Wait 60 seconds

### Step 6: Install from Catalog
1. Dashboard > Plugins > Catalog
2. Find **MetadataRenamer**
3. Click **Install**
4. Wait for installation

### Step 7: Restart Jellyfin
```powershell
Restart-Service JellyfinServer
```

### Step 8: Verify
- Plugin should show **"Active"** (not "NotSupported")
- Configuration page should load
- Settings should be visible

## Why This Works
- Removes old .NET 9.0 build
- Clears cached manifest
- Downloads fresh .NET 8.0 build
- Plugin loads successfully
