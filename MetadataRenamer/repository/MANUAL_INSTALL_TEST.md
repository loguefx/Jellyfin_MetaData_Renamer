# Manual Installation Test

If the plugin doesn't appear in the catalog, try manual installation to verify the plugin itself works:

## Steps:

1. **Download the DLL:**
   - URL: https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.MetadataRenamer.dll
   - Save it to your Downloads folder

2. **Create plugin folder:**
   ```
   C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer\
   ```

3. **Copy DLL to plugin folder:**
   - Copy `Jellyfin.Plugin.MetadataRenamer.dll` to the folder above

4. **Restart Jellyfin:**
   - Go to Dashboard > General
   - Click "Restart" button
   - Or restart the Jellyfin service

5. **Check if plugin appears:**
   - Go to Dashboard > Plugins
   - Look for "MetadataRenamer" in the list

## What This Tells Us:

- **If manual install works:** The plugin is fine, the issue is with the repository setup
- **If manual install fails:** There's an issue with the plugin itself (compatibility, missing dependencies, etc.)

## If Manual Install Works:

The repository should work too. Try:
1. Remove repository
2. Restart Jellyfin
3. Add repository again
4. Wait 5 minutes
5. Check catalog again

Sometimes Jellyfin needs time to refresh the catalog after adding a repository.
