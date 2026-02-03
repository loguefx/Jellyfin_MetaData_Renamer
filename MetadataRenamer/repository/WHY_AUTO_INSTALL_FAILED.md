# Why Automatic Installation Failed

## What Happened

When you clicked "Install" in the Catalog, Jellyfin should have:
1. Downloaded the DLL from GitHub
2. Created the plugin folder
3. Copied the DLL to the folder
4. Loaded the plugin on restart

**But the plugin folder didn't exist**, which means step 1-3 failed.

## Common Reasons

### 1. Network/Firewall Issue
Jellyfin couldn't download the DLL from GitHub.

**Check:**
- Can Jellyfin access the internet?
- Is there a firewall blocking GitHub?
- Try accessing the URL manually: https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.MetadataRenamer.dll

### 2. Permission Issue
Jellyfin service doesn't have write permission to the plugins folder.

**Check:**
- What user is the Jellyfin service running as?
- Does that user have write access to `C:\ProgramData\Jellyfin\Server\plugins\`?

**Fix:**
```powershell
# Grant Jellyfin service account write access
$pluginsPath = "C:\ProgramData\Jellyfin\Server\plugins"
$acl = Get-Acl $pluginsPath
$permission = "NT SERVICE\JellyfinServer","FullControl","ContainerInherit,ObjectInherit","None","Allow"
$accessRule = New-Object System.Security.AccessControl.FileSystemAccessRule $permission
$acl.SetAccessRule($accessRule)
Set-Acl $pluginsPath $acl
```

### 3. Jellyfin Logs Show Error
Check Jellyfin logs for installation errors.

**Location:**
- `C:\ProgramData\Jellyfin\Server\logs\`
- Or check Jellyfin Dashboard > Logs

**Look for:**
- "Failed to download plugin"
- "Access denied"
- "Network error"
- "Plugin installation failed"

### 4. GitHub Rate Limiting
GitHub may have rate-limited Jellyfin's requests.

**Solution:**
- Wait a few minutes and try again
- Or use manual installation (which we just did)

## Solution: Manual Installation ✅

We've already installed the plugin manually. Now:

1. **Restart Jellyfin** (completely stop and start the service)
2. **Check Dashboard > Plugins**
3. **Plugin should appear in the installed plugins list**

## Preventing Future Issues

### Option 1: Fix Permissions
Make sure Jellyfin service can write to plugins folder (see above).

### Option 2: Use Manual Installation
If automatic installation keeps failing, you can:
- Use the `install-plugin.ps1` script
- Or manually download and copy the DLL

### Option 3: Check Jellyfin Logs
Always check logs when installation fails to see the exact error.

## After Manual Installation

Once the plugin is manually installed and appears in Dashboard > Plugins:

- ✅ The plugin will work normally
- ✅ You can enable/disable it in settings
- ✅ Updates from the catalog should work (if permissions are fixed)
- ⚠️  The catalog may still show "Install" until you uninstall and reinstall via catalog

## Next Steps

1. **Restart Jellyfin now**
2. **Check if plugin appears in Dashboard > Plugins**
3. **If it appears:** Great! The plugin is working
4. **If it doesn't appear:** Check Jellyfin logs for loading errors
