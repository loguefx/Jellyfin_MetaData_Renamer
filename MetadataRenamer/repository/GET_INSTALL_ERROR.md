# How to Get the Installation Error Message

## Step 1: Check Jellyfin Server Logs

1. Navigate to: `C:\ProgramData\Jellyfin\Server\logs\`
2. Open the most recent log file (e.g., `log_20260210.log`)
3. Search for one of these terms:
   - `MetadataRenamer`
   - `eb5d7894-8eef-4b36-aa6f-5d124e828ce1` (plugin GUID)
   - `plugin` + `error`
   - `Failed to install`
   - `Installation failed`

## Step 2: Look for Error Messages

Common error patterns to search for:
- `System.Exception`
- `NotSupportedException`
- `FileNotFoundException`
- `UnauthorizedAccessException`
- `IOException`
- `TypeLoadException`

## Step 3: Copy the Full Error Message

Copy the entire error message including:
- The exception type
- The error message
- The stack trace (if available)
- Any context around the error (lines before and after)

## Common Installation Errors and Fixes

### Error: "NotSupported" or "TargetAbi mismatch"
- **Cause:** Jellyfin version doesn't match plugin TargetAbi
- **Fix:** Check your Jellyfin version and ensure the manifest includes the correct TargetAbi

### Error: "FileNotFoundException" or "Could not find file"
- **Cause:** ZIP file not accessible or corrupted
- **Fix:** Verify the ZIP file is accessible at the GitHub URL

### Error: "UnauthorizedAccessException" or "Permission denied"
- **Cause:** Jellyfin doesn't have permission to write to plugin directory
- **Fix:** Check Jellyfin service permissions on `C:\ProgramData\Jellyfin\Server\plugins\`

### Error: "IOException" or "File in use"
- **Cause:** Plugin DLL is locked or in use
- **Fix:** Restart Jellyfin service completely

### Error: "TypeLoadException" or "MissingMethodException"
- **Cause:** Plugin dependencies missing or version mismatch
- **Fix:** Ensure Jellyfin version matches plugin TargetAbi requirements

## Quick Diagnostic Commands

Run these in PowerShell to check common issues:

```powershell
# Check if ZIP file exists and is accessible
Test-Path "D:\Jellyfin Projects\Jellyfin_Metadata_tool\MetadataRenamer\repository\Jellyfin.Plugin.MetadataRenamer.zip"

# Check if manifest is accessible
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json" -Method Head

# Check if ZIP is accessible
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.MetadataRenamer.zip" -Method Head

# Check Jellyfin plugin directory
Test-Path "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer"
```

## What to Provide

When reporting the installation error, please provide:
1. The exact error message from Jellyfin logs
2. Your Jellyfin version (Dashboard > System > About)
3. The output of the diagnostic commands above
