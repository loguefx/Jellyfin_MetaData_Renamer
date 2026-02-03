# Future Version Compatibility Guide

## How to Add Support for New Jellyfin Versions

When Jellyfin releases a new version (e.g., 10.12.x), follow these steps:

### Step 1: Update the Manifest

Edit `generate-manifest.ps1` and add the new TargetAbi to the `$TargetAbis` array:

```powershell
[string[]]$TargetAbis = @("10.10.0.0", "10.11.0.0", "10.12.0.0")
```

### Step 2: Rebuild and Regenerate Manifest

```powershell
# Build the plugin
cd MetadataRenamer\Jellyfin.Plugin.Template
dotnet build -c Release

# Generate new manifest with all versions
cd ..\repository
.\generate-manifest.ps1
```

### Step 3: Copy DLL and Push to GitHub

```powershell
# Copy DLL to repository
Copy-Item "..\Jellyfin.Plugin.Template\bin\Release\net9.0\Jellyfin.Plugin.MetadataRenamer.dll" -Destination "repository\Jellyfin.Plugin.MetadataRenamer.dll" -Force

# Commit and push
git add repository\Jellyfin.Plugin.MetadataRenamer.dll repository\manifest.json
git commit -m "Add support for Jellyfin 10.12.x"
git push
```

## How It Works

The manifest contains multiple version entries, one for each TargetAbi:

```json
"versions": [
  {
    "version": "1.0.0.0",
    "targetAbi": "10.10.0.0",
    "sourceUrl": "...",
    "checksum": "..."
  },
  {
    "version": "1.0.0.0",
    "targetAbi": "10.11.0.0",
    "sourceUrl": "...",
    "checksum": "..."
  }
]
```

Jellyfin automatically selects the version entry that matches its TargetAbi. This means:
- ✅ One DLL works for multiple Jellyfin versions
- ✅ Users on different Jellyfin versions can all install the plugin
- ✅ No need to rebuild for each version (unless API changes)

## When to Rebuild

You only need to rebuild the plugin if:
- Jellyfin releases a new major version with breaking API changes
- You need to use new Jellyfin features
- The plugin code needs updates

Otherwise, the same DLL can work across multiple minor versions (10.10.x, 10.11.x, etc.).

## Testing

After adding a new version:
1. Test on the new Jellyfin version
2. Verify the plugin loads correctly
3. Test core functionality
4. Check Jellyfin logs for any compatibility warnings

## Automatic Installation Fix

The automatic installation should work if:
- ✅ Manifest is properly formatted (UTF-8 without BOM)
- ✅ DLL is accessible from the sourceUrl
- ✅ Checksum matches the DLL
- ✅ Jellyfin service has write permissions to plugins folder
- ✅ Network access to GitHub is available

If automatic installation still fails:
1. Check Jellyfin logs for specific errors
2. Verify permissions on `C:\ProgramData\Jellyfin\Server\plugins\`
3. Test manual installation to verify the plugin works
4. Check if Jellyfin can access GitHub (firewall/proxy)
