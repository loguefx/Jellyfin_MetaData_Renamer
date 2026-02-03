# Plugin Not Loading After Installation

## Symptoms
- Plugin shows in Catalog with "Install" button
- After clicking Install and restarting, plugin still shows in Catalog
- Plugin does NOT appear in Dashboard > Plugins (installed plugins list)

## Common Causes

### 1. Plugin Failed to Download
Jellyfin may have failed to download the DLL from GitHub.

**Check:**
- Go to Jellyfin logs: `C:\ProgramData\Jellyfin\Server\logs\`
- Look for errors about downloading the plugin
- Search for "MetadataRenamer" or "eb5d7894-8eef-4b36-aa6f-5d124e828ce1"

### 2. Plugin DLL Not in Correct Location

**Windows Plugin Path:**
```
C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer\Jellyfin.Plugin.MetadataRenamer.dll
```

**Check if it exists:**
1. Open File Explorer
2. Navigate to `C:\ProgramData\Jellyfin\Server\plugins\`
3. Look for `MetadataRenamer` folder
4. Check if `Jellyfin.Plugin.MetadataRenamer.dll` is inside

### 3. Plugin Failed to Load (Compatibility Issue)

**Check Jellyfin Logs:**
1. Open: `C:\ProgramData\Jellyfin\Server\logs\log_YYYYMMDD.log`
2. Search for:
   - "MetadataRenamer"
   - "eb5d7894-8eef-4b36-aa6f-5d124e828ce1"
   - "plugin" + "error"
   - "Failed to load"
   - "NotSupported"

**Common errors:**
- `NotSupported` - TargetAbi mismatch
- `FileNotFoundException` - Missing DLL or dependencies
- `TypeLoadException` - Compatibility issue
- `MissingMethodException` - Version mismatch

### 4. Plugin Folder Structure Wrong

Jellyfin expects:
```
plugins/
└── MetadataRenamer/
    └── Jellyfin.Plugin.MetadataRenamer.dll
```

**NOT:**
```
plugins/
└── MetadataRenamer/
    └── MetadataRenamer/
        └── Jellyfin.Plugin.MetadataRenamer.dll  ❌ Wrong!
```

### 5. Jellyfin Cache Issue

Sometimes Jellyfin caches plugin state incorrectly.

**Try:**
1. Stop Jellyfin service completely
2. Wait 30 seconds
3. Start Jellyfin service
4. Check plugins again

## Diagnostic Steps

### Step 1: Check Plugin Folder

**PowerShell:**
```powershell
$pluginPath = "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer"
if (Test-Path $pluginPath) {
    Write-Host "✅ Plugin folder exists"
    Get-ChildItem $pluginPath -Recurse | Format-Table Name, Length, LastWriteTime
} else {
    Write-Host "❌ Plugin folder does NOT exist"
}
```

### Step 2: Check Jellyfin Logs

**PowerShell:**
```powershell
$logPath = "C:\ProgramData\Jellyfin\Server\logs"
$latestLog = Get-ChildItem $logPath -Filter "log_*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Latest log: $($latestLog.FullName)"
Select-String -Path $latestLog.FullName -Pattern "MetadataRenamer|eb5d7894" -Context 2,2
```

### Step 3: Manual Installation Test

If automatic installation fails, try manual:

1. **Download DLL:**
   ```
   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.MetadataRenamer.dll
   ```

2. **Create folder:**
   ```
   C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer\
   ```

3. **Copy DLL to folder**

4. **Restart Jellyfin**

5. **Check Dashboard > Plugins**

If manual install works, the issue is with the automatic installation/download.

### Step 4: Verify DLL Compatibility

**Check TargetAbi:**
- Your Jellyfin: **10.10.7**
- Manifest TargetAbi: **10.10.0.0** ✅ (Should work)

**If mismatch:**
- Update manifest.json `targetAbi` to match your Jellyfin version
- Rebuild plugin if needed

### Step 5: Check Plugin GUID

The plugin GUID must match:
- **Plugin.cs:** `eb5d7894-8eef-4b36-aa6f-5d124e828ce1`
- **manifest.json:** `"guid": "eb5d7894-8eef-4b36-aa6f-5d124e828ce1"`

If they don't match, Jellyfin won't recognize it as the same plugin.

## Quick Fixes

### Fix 1: Remove and Reinstall

1. **Remove from Catalog:**
   - If plugin shows "Installed" but doesn't appear, try uninstalling first
   - Dashboard > Plugins > Catalog > MetadataRenamer > Uninstall

2. **Delete plugin folder:**
   ```
   C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer\
   ```

3. **Restart Jellyfin**

4. **Install again from Catalog**

### Fix 2: Manual Install

1. Download DLL manually
2. Create plugin folder
3. Copy DLL
4. Restart Jellyfin
5. Plugin should appear in Dashboard > Plugins

### Fix 3: Check Permissions

Make sure Jellyfin service has permission to:
- Write to `C:\ProgramData\Jellyfin\Server\plugins\`
- Read the DLL file

**Check:**
```powershell
$pluginPath = "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer"
$acl = Get-Acl $pluginPath
$acl.Access | Format-Table IdentityReference, FileSystemRights, AccessControlType
```

## Still Not Working?

1. **Check Jellyfin version compatibility**
2. **Verify DLL is not corrupted** (re-download)
3. **Check for other plugin conflicts**
4. **Try installing a known-working plugin** to verify Jellyfin plugin system works
5. **Check Windows Event Viewer** for Jellyfin service errors

## Expected Behavior After Installation

✅ **Plugin should appear in:**
- Dashboard > Plugins (installed plugins list)
- NOT in Catalog (unless you uninstall it)

✅ **Plugin should show:**
- Name: "MetadataRenamer"
- Version: "1.0.0.0"
- Status: Enabled/Disabled toggle
- Settings button

❌ **If it still shows in Catalog:**
- Installation didn't complete
- Plugin failed to load
- Check logs for errors
