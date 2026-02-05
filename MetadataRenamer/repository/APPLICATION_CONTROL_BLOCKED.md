# Windows Application Control Blocking Plugin DLL

## Error Message

```
[ERR] Failed to load assembly "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0\Jellyfin.Plugin.MetadataRenamer.dll". Disabling plugin
System.IO.FileLoadException: Could not load file or assembly... An Application Control policy has blocked this file. (0x800711C7)
```

## What This Means

Windows Application Control (Windows Defender Application Control, AppLocker, or similar) is blocking the plugin DLL from loading. This is a Windows security feature that prevents unsigned or untrusted executables from running.

## Solutions

### Solution 1: Unblock the DLL File (Quickest Fix)

**PowerShell (Run as Administrator):**
```powershell
$dllPath = "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0\Jellyfin.Plugin.MetadataRenamer.dll"
Unblock-File -Path $dllPath
```

**Or manually:**
1. Navigate to: `C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0\`
2. Right-click `Jellyfin.Plugin.MetadataRenamer.dll`
3. Click **Properties**
4. Check the **Unblock** checkbox at the bottom (if present)
5. Click **OK**
6. Restart Jellyfin

### Solution 2: Unblock All Files in Plugin Folder

**PowerShell (Run as Administrator):**
```powershell
$pluginFolder = "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0"
Get-ChildItem -Path $pluginFolder -Recurse | Unblock-File
```

### Solution 3: Add Exception in Windows Defender Application Control

If you're using Windows Defender Application Control (WDAC):

1. Open **Local Group Policy Editor** (`gpedit.msc`)
2. Navigate to: **Computer Configuration > Administrative Templates > Windows Components > Windows Defender Application Control**
3. Configure policy exceptions as needed

**Or use PowerShell:**
```powershell
# Check current WDAC policies
Get-CimInstance -Namespace "root\Microsoft\Windows\CI" -ClassName "CC_Application" | Select-Object Name, Status
```

### Solution 4: Disable Application Control for Plugin Path (Not Recommended)

**Only if other solutions don't work:**

1. Open **Local Group Policy Editor** (`gpedit.msc`)
2. Navigate to: **Computer Configuration > Windows Settings > Security Settings > Application Control Policies**
3. Configure exceptions for: `C:\ProgramData\Jellyfin\Server\plugins\*\*.dll`

**Warning:** This reduces security. Only use if necessary.

### Solution 5: Sign the DLL (Best Long-term Solution)

If you have a code signing certificate:

1. Sign the DLL with your certificate
2. Windows will trust signed DLLs automatically

**PowerShell:**
```powershell
Set-AuthenticodeSignature -FilePath "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0\Jellyfin.Plugin.MetadataRenamer.dll" -Certificate (Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert)
```

## Verification

After applying a solution:

1. **Restart Jellyfin** (completely stop and start the service)
2. **Check Jellyfin logs** for:
   ```
   [INF] Loaded plugin: "MetadataRenamer" "1.0.0.0"
   ```
3. **Check Dashboard > Plugins** - MetadataRenamer should appear in the installed plugins list

## Why This Happens

- **Downloaded files** from the internet are often marked as "blocked" by Windows
- **Unsigned DLLs** may be blocked by Application Control policies
- **Enterprise environments** often have stricter Application Control policies

## Prevention

After unblocking, the DLL should remain unblocked unless:
- The file is re-downloaded
- Windows updates reset policies
- Group Policy changes

## Related Issues

If the plugin still doesn't load after unblocking:
- Check Jellyfin logs for other errors
- Verify the DLL is the correct version (.NET 8.0)
- Check file permissions
- See `PLUGIN_NOT_LOADING.md` for more troubleshooting
