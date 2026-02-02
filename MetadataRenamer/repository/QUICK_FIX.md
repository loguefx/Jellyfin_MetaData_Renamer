# Quick Fix Guide

## Issue: Plugin Not Showing in Jellyfin Repository

### Most Common Causes:

1. **BOM (Byte Order Mark) in manifest.json** ✅ **FIXED**
   - The manifest has been updated to remove BOM
   - Wait a few minutes for GitHub to update

2. **TargetAbi Version Mismatch**
   - Current: `10.9.0.0`
   - Check your Jellyfin version in Dashboard > General > About
   - If different, update `TargetAbi` in manifest.json

3. **Jellyfin Cache**
   - Restart Jellyfin server
   - Clear browser cache
   - Remove and re-add the repository

### Steps to Try:

1. **Wait 2-3 minutes** for GitHub to update the file

2. **Remove the repository in Jellyfin:**
   - Dashboard > Plugins > Repositories
   - Remove the existing entry

3. **Add it again:**
   - Click `+` button
   - Enter: `https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json`
   - Click OK

4. **Check Jellyfin Logs:**
   - Look for errors about plugin repository
   - Check if manifest is being fetched successfully

5. **Verify Your Jellyfin Version:**
   - If you're on Jellyfin 10.8.x or 10.10.x, the TargetAbi needs to match
   - Edit manifest.json and change `"TargetAbi": "10.9.0.0"` to your version

### Test the Manifest:

Open this URL in your browser:
```
https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
```

**Should see:** Clean JSON starting with `{` (no `?` or other characters)

**Should NOT see:** 404 error or HTML page

### If Still Not Working:

Try manual installation to verify the plugin works:
1. Download DLL from: https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.Template.dll
2. Copy to: `C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer\`
3. Restart Jellyfin
4. Check Dashboard > Plugins

If manual install works, the issue is with the repository setup, not the plugin.
