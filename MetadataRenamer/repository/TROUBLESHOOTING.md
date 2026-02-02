# Troubleshooting: Plugin Not Showing in Jellyfin

## Common Issues and Solutions

### 1. **Check Your Jellyfin Version**

The manifest uses `TargetAbi: "10.9.0.0"`. This must match your Jellyfin server version:

- **Jellyfin 10.9.x** → `TargetAbi: "10.9.0.0"` ✅ (Current)
- **Jellyfin 10.8.x** → `TargetAbi: "10.8.0.0"`
- **Jellyfin 10.10.x** → `TargetAbi: "10.10.0.0"`

**To check your Jellyfin version:**
- Go to Dashboard > General > About
- Look for the version number

**To update TargetAbi:**
1. Edit `manifest.json`
2. Change `"TargetAbi": "10.9.0.0"` to match your version
3. Commit and push to GitHub

### 2. **Verify Manifest is Accessible**

Test the manifest URL in your browser:
```
https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
```

**Expected result:** You should see the JSON content, not a 404 error.

### 3. **Verify DLL is Accessible**

Test the DLL URL in your browser:
```
https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.Template.dll
```

**Expected result:** The file should download, not show a 404 error.

### 4. **Check JSON Validity**

The manifest must be valid JSON. Common issues:
- Extra commas
- Missing quotes
- Invalid characters

**Validate your JSON:**
- Use https://jsonlint.com/
- Or use PowerShell: `Get-Content manifest.json | ConvertFrom-Json` (should not error)

### 5. **Clear Jellyfin Cache**

Jellyfin may cache repository data:

1. **Restart Jellyfin server**
2. **Clear browser cache** (Ctrl+Shift+Delete)
3. **Try adding repository again**

### 6. **Check Repository URL Format**

When adding the repository in Jellyfin:
- ✅ **Correct:** `https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json`
- ❌ **Wrong:** `https://github.com/loguefx/Jellyfin_MetaData_Renamer/blob/main/MetadataRenamer/repository/manifest.json` (blob URL won't work)

**Must use:** `raw.githubusercontent.com` not `github.com`

### 7. **Check Jellyfin Logs**

If the plugin still doesn't appear, check Jellyfin logs for errors:

**Windows:**
```
C:\ProgramData\Jellyfin\Server\logs\
```

**Linux:**
```
/var/log/jellyfin/
```

Look for errors related to:
- Plugin repository
- Manifest parsing
- Network errors

### 8. **Verify Manifest Structure**

Your manifest should have this exact structure:

```json
{
  "RepositoryName": "MetadataRenamer Repository",
  "RepositoryDescription": "...",
  "Plugins": [
    {
      "Name": "MetadataRenamer",
      "Guid": "eb5d7894-8eef-4b36-aa6f-5d124e828ce1",
      "Version": "1.0.0.0",
      "TargetAbi": "10.9.0.0",
      "Overview": "...",
      "Description": "...",
      "Category": "Libraries",
      "Owner": "loguefx",
      "SourceUrl": "...",
      "Checksum": "...",
      "Timestamp": "..."
    }
  ]
}
```

### 9. **Network/Firewall Issues**

If Jellyfin server can't access GitHub:
- Check firewall settings
- Verify internet connectivity from Jellyfin server
- Try accessing the manifest URL from the Jellyfin server machine

### 10. **Try Manual Installation First**

To verify the plugin works, try manual installation:

1. Download the DLL from GitHub
2. Copy to: `C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer\` (Windows)
3. Restart Jellyfin
4. Check if plugin appears in Dashboard > Plugins

If manual installation works but repository doesn't, the issue is with the repository setup, not the plugin itself.

## Step-by-Step Debugging

1. ✅ Verify manifest.json is accessible via browser
2. ✅ Verify DLL is accessible via browser  
3. ✅ Check Jellyfin version matches TargetAbi
4. ✅ Validate JSON format
5. ✅ Clear cache and restart Jellyfin
6. ✅ Check Jellyfin logs for errors
7. ✅ Try manual plugin installation to verify plugin works

## Still Not Working?

If none of the above works:
1. Check Jellyfin community forums for similar issues
2. Verify your Jellyfin version supports custom repositories
3. Try a different repository URL format
4. Contact Jellyfin support with your logs
