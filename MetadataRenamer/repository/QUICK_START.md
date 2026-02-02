# Quick Start Guide - Local Testing

## Fastest Way to Test Locally

### Step 1: Build the Plugin
```powershell
cd ..
dotnet build -c Release
```

### Step 2: Copy DLL to Repository Folder
```powershell
Copy-Item "..\Jellyfin.Plugin.Template\bin\Release\net9.0\Jellyfin.Plugin.Template.dll" -Destination "."
```

### Step 3: Update manifest.json SourceUrl
Edit `manifest.json` and change the `SourceUrl` to your local IP:
```json
"SourceUrl": "http://192.168.0.180:8000/Jellyfin.Plugin.Template.dll"
```
(Replace `192.168.0.180` with your computer's IP address)

### Step 4: Start Local Web Server
```powershell
.\start-local-server.ps1
```

Or manually:
```powershell
python -m http.server 8000
```

### Step 5: Add Repository to Jellyfin
1. Open Jellyfin web UI
2. Go to **Dashboard** > **Plugins** > **Repositories**
3. Click the **+** button
4. Enter: `http://YOUR_IP:8000/manifest.json`
   - Example: `http://192.168.0.180:8000/manifest.json`
5. Click **OK**

### Step 6: Install Plugin
1. Go to **Dashboard** > **Plugins**
2. Find **MetadataRenamer** in the catalog
3. Click **Install**

## Finding Your IP Address

**Windows:**
```powershell
ipconfig | findstr IPv4
```

**Linux/Mac:**
```bash
ip addr show | grep inet
# or
ifconfig | grep inet
```

## Troubleshooting

- **Can't access from Jellyfin server**: Make sure Windows Firewall allows port 8000
- **Plugin not showing**: Check that manifest.json is accessible in browser
- **Installation fails**: Verify DLL URL is correct and accessible
