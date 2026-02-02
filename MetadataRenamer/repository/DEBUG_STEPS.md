# Debugging Steps - Plugin Not Appearing

## Step 1: Check Jellyfin Logs

**Windows Location:**
```
C:\ProgramData\Jellyfin\Server\logs\
```

Look for the most recent log file and search for:
- "plugin"
- "repository"
- "manifest"
- "MetadataRenamer"
- Any errors or warnings

**What to look for:**
- Errors parsing the manifest
- Network errors accessing GitHub
- TargetAbi mismatch warnings
- Plugin validation errors

## Step 2: Verify Manifest is Valid

Test the manifest URL directly:
```
https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
```

**Should see:** Clean JSON starting with `{`
**Should NOT see:** HTML page, 404 error, or BOM characters

## Step 3: Verify DLL is Accessible

Test the DLL URL:
```
https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.Template.dll
```

**Should see:** File download starts
**Should NOT see:** 404 error or HTML page

## Step 4: Check TargetAbi Match

Your Jellyfin: **10.10.7**
Manifest TargetAbi: **10.10.0.0** ✅ (Should match)

If they don't match, Jellyfin will hide the plugin.

## Step 5: Try Manual Installation

To verify the plugin itself works:

1. Download the DLL from GitHub
2. Create folder: `C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer\`
3. Copy DLL to that folder
4. Restart Jellyfin
5. Check Dashboard > Plugins

If manual install works, the issue is with the repository setup, not the plugin.

## Step 6: Clear All Caches

1. **Restart Jellyfin server** (not just the web UI)
2. **Clear browser cache** (Ctrl+Shift+Delete)
3. **Wait 5 minutes** after adding repository
4. **Check again**

## Step 7: Check Repository Name

In Jellyfin > Plugins > Repositories, the repository should show as:
- Name: "Jellyfin_MetaData_Renamer" or "MetadataRenamer Repository"
- URL: The manifest.json URL

If it shows an error icon, there's a problem accessing the manifest.

## Step 8: Verify JSON Structure

The manifest must have exact structure. Common issues:
- Missing required fields
- Invalid JSON syntax
- Wrong data types (e.g., Version as number instead of string)

## Step 9: Check Network Access

From the Jellyfin server machine, test if it can access GitHub:
```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json" -UseBasicParsing
```

If this fails, there's a network/firewall issue.

## Step 10: Alternative - Use GitHub Releases

If repository still doesn't work, try GitHub Releases:

1. Create a release on GitHub
2. Upload the DLL as a release asset
3. Update manifest SourceUrl to point to release asset
4. This is more reliable than raw file URLs

## Still Not Working?

Share the Jellyfin log errors and we can debug further!
