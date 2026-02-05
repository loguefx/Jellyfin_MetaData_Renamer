# Manual Steps to Unblock Plugin DLL

## The Problem

Windows Application Control is blocking the plugin DLL from loading. You'll see this error in Jellyfin logs:

```
[ERR] Failed to load assembly... An Application Control policy has blocked this file. (0x800711C7)
```

## Solution: Unblock the DLL File

### Step 1: Find the Plugin DLL

The DLL should be located at:
```
C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0\Jellyfin.Plugin.MetadataRenamer.dll
```

**If the folder doesn't exist:**
1. The plugin may not be installed yet
2. Install it from Dashboard > Plugins > Catalog
3. Then follow the steps below

### Step 2: Unblock the DLL (Method 1 - PowerShell)

**Run PowerShell as Administrator:**

```powershell
# Navigate to the plugin folder
cd "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0"

# Unblock the DLL
Unblock-File -Path "Jellyfin.Plugin.MetadataRenamer.dll"

# Verify it worked
Get-Item "Jellyfin.Plugin.MetadataRenamer.dll" | Select-Object FullName, Attributes
```

### Step 3: Unblock the DLL (Method 2 - File Properties)

1. **Open File Explorer**
2. **Navigate to:** `C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0\`
3. **Right-click** `Jellyfin.Plugin.MetadataRenamer.dll`
4. **Click Properties**
5. **At the bottom**, if you see a checkbox that says **"Unblock"**, check it
6. **Click OK**

**Note:** If you don't see an "Unblock" checkbox, the file may already be unblocked, or Windows may be using a different blocking mechanism.

### Step 4: Unblock All Files in Plugin Folder (Recommended)

**PowerShell as Administrator:**

```powershell
$pluginFolder = "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0"
Get-ChildItem -Path $pluginFolder -Recurse | Unblock-File
```

### Step 5: Restart Jellyfin

1. **Stop Jellyfin service** completely
2. **Wait 10 seconds**
3. **Start Jellyfin service**
4. **Check logs** for: `[INF] Loaded plugin: "MetadataRenamer" "1.0.0.0"`

## Verification

After unblocking and restarting, check Jellyfin logs. You should see:

✅ **Success:**
```
[INF] Loaded plugin: "MetadataRenamer" "1.0.0.0"
```

❌ **Still blocked:**
```
[ERR] Failed to load assembly... An Application Control policy has blocked this file. (0x800711C7)
```

## If Unblock-File Doesn't Work

If `Unblock-File` doesn't work, you may need to:

1. **Check Windows Defender Application Control (WDAC) policies**
2. **Add an exception** in Group Policy
3. **Sign the DLL** with a code signing certificate

See `APPLICATION_CONTROL_BLOCKED.md` for more advanced solutions.

## After Unblocking

Once the plugin loads successfully:
- The plugin will appear in **Dashboard > Plugins**
- You'll see `[MR]` log entries when it processes items
- Series folders will be renamed automatically when you use "Identify"
