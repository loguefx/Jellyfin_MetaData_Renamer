# Re-Add Repository Checklist

## ✅ Completed
- [x] Plugin folder removed from `C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer`
- [x] Repository removed from Jellyfin UI

## 📋 Next Steps

### Step 1: Re-add Repository
1. Open Jellyfin web UI
2. Go to **Dashboard > Plugins > Repositories**
3. Click the **+** button
4. Enter this URL:
   ```
   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
   ```
5. Click **OK**
6. **Wait 60 seconds** for Jellyfin to fetch the manifest

### Step 2: Install Plugin
1. Go to **Dashboard > Plugins > Catalog**
2. Look for **MetadataRenamer** in the **General** category
3. Click **Install**
4. Wait for installation to complete

### Step 3: Restart Jellyfin
1. Stop Jellyfin service:
   ```powershell
   Stop-Service JellyfinServer
   ```
2. Wait 10 seconds
3. Start Jellyfin service:
   ```powershell
   Start-Service JellyfinServer
   ```

### Step 4: Verify
1. Go to **Dashboard > Plugins**
2. Find **MetadataRenamer**
3. Should show:
   - ✅ **Status: Active** (not "NotSupported")
   - ✅ **Configuration page loads** when clicked
   - ✅ **All settings visible**

## Troubleshooting

### Plugin doesn't appear in Catalog
- Wait another 60 seconds (Jellyfin may need time to process)
- Check Jellyfin logs for errors
- Verify repository URL is correct
- Try removing and re-adding repository again

### Plugin shows "NotSupported"
- The old .NET 9.0 build may still be installed
- Run the force-reinstall script:
  ```powershell
  .\MetadataRenamer\repository\force-reinstall.ps1
  ```

### Configuration page doesn't load
- Plugin may not have loaded correctly
- Check Jellyfin logs for errors
- Verify plugin shows "Active" status
- Try restarting Jellyfin again

## Expected Result
✅ Plugin appears in Catalog
✅ Installation succeeds
✅ Plugin shows "Active" status
✅ Configuration page works
✅ Plugin functions correctly
