# Automatic Installation Fix

## What Was Fixed

1. **Multi-Version Support**: Manifest now supports both Jellyfin 10.10.x and 10.11.x
2. **Updated Checksum**: Manifest checksum matches the current DLL
3. **Future-Proof**: Easy to add new Jellyfin versions

## Testing Automatic Installation

### Step 1: Remove Any Manual Installation

If you manually installed the plugin, remove it first:

```powershell
# Stop Jellyfin service first!
Remove-Item "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer" -Recurse -Force
```

### Step 2: Remove and Re-add Repository

1. Go to **Dashboard > Plugins > Repositories**
2. Remove the existing repository entry
3. Click **+** to add it again:
   ```
   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
   ```
4. Wait 30 seconds for Jellyfin to refresh

### Step 3: Install from Catalog

1. Go to **Dashboard > Plugins > Catalog**
2. Find **MetadataRenamer** in the **General** category
3. Click **Install**
4. Wait for installation to complete (check for success message)

### Step 4: Restart Jellyfin

1. **Completely stop** the Jellyfin service
2. Wait 10 seconds
3. **Start** the Jellyfin service again

### Step 5: Verify Installation

1. Go to **Dashboard > Plugins**
2. Look for **MetadataRenamer** in the installed plugins list
3. It should **NOT** appear in Catalog anymore

## If Automatic Installation Still Fails

### Check 1: Jellyfin Logs

Look for errors in:
- `C:\ProgramData\Jellyfin\Server\logs\log_YYYYMMDD.log`

Search for:
- "MetadataRenamer"
- "plugin install"
- "Failed to download"
- "Access denied"

### Check 2: Permissions

Jellyfin service needs write access to:
```
C:\ProgramData\Jellyfin\Server\plugins\
```

**Fix permissions:**
```powershell
$pluginsPath = "C:\ProgramData\Jellyfin\Server\plugins"
$acl = Get-Acl $pluginsPath

# Find your Jellyfin service account (usually NT SERVICE\JellyfinServer)
# Grant FullControl
$permission = "NT SERVICE\JellyfinServer","FullControl","ContainerInherit,ObjectInherit","None","Allow"
$accessRule = New-Object System.Security.AccessControl.FileSystemAccessRule $permission
$acl.SetAccessRule($accessRule)
Set-Acl $pluginsPath $acl
```

### Check 3: Network Access

Jellyfin needs to download from GitHub. Test:
```
https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.MetadataRenamer.dll
```

If this doesn't work from the Jellyfin server, there may be a firewall/proxy issue.

### Check 4: Manifest Format

Verify the manifest is valid JSON:
```powershell
$manifest = Get-Content "MetadataRenamer\repository\manifest.json" -Raw | ConvertFrom-Json
$manifest | ConvertTo-Json -Depth 10
```

Should show valid JSON structure.

## Expected Behavior

✅ **After successful installation:**
- Plugin appears in **Dashboard > Plugins** (installed list)
- Plugin does **NOT** appear in Catalog
- Plugin can be enabled/disabled
- Plugin settings are accessible

❌ **If installation fails:**
- Plugin still shows in Catalog with "Install" button
- No plugin folder created
- Check logs for specific error

## Troubleshooting

### Error: "Failed to download plugin"
- Check network connectivity
- Verify GitHub URL is accessible
- Check firewall settings

### Error: "Access denied"
- Fix permissions (see Check 2 above)
- Ensure Jellyfin service has write access

### Error: "TargetAbi mismatch"
- Your Jellyfin version doesn't match supported versions
- Add your version to `generate-manifest.ps1` and regenerate

### Plugin installs but doesn't appear
- Restart Jellyfin completely (stop and start service)
- Check logs for loading errors
- Verify DLL is in correct location

## Success Indicators

When automatic installation works:
1. ✅ Click "Install" in Catalog
2. ✅ See "Installation successful" or similar message
3. ✅ Plugin folder is created: `C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer\`
4. ✅ DLL is present in the folder
5. ✅ After restart, plugin appears in Dashboard > Plugins
