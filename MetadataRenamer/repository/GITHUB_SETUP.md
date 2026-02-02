# GitHub Repository Setup Guide

Your GitHub repository: **https://github.com/loguefx/Jellyfin_MetaData_Renamer**

## Setup Steps

### 1. Push Your Code to GitHub

If you haven't already, push your local repository:

```powershell
cd "D:\Jellyfin Projects\Jellyfin_Metadata_tool"
git init
git add .
git commit -m "Initial commit - MetadataRenamer plugin"
git branch -M main
git remote add origin https://github.com/loguefx/Jellyfin_MetaData_Renamer.git
git push -u origin main
```

### 2. Build and Copy DLL

```powershell
cd MetadataRenamer
dotnet build -c Release
Copy-Item "Jellyfin.Plugin.Template\bin\Release\net9.0\Jellyfin.Plugin.Template.dll" -Destination "repository\"
```

### 3. Generate Updated Manifest

```powershell
cd repository
.\generate-manifest.ps1
```

This will update the checksum and timestamp automatically.

### 4. Commit and Push Repository Files

```powershell
cd ..
git add repository/
git commit -m "Add plugin repository manifest and DLL"
git push
```

### 5. Verify Files Are Accessible

Check these URLs in your browser (they should download the files):

- **Manifest**: https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
- **DLL**: https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.Template.dll

### 6. Add Repository to Jellyfin

1. Open Jellyfin web UI
2. Go to **Dashboard** > **Plugins** > **Repositories**
3. Click the **+** button
4. Enter the manifest URL:
   ```
   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json
   ```
5. Click **OK**

### 7. Install Plugin

1. Go to **Dashboard** > **Plugins**
2. Find **MetadataRenamer** in the catalog
3. Click **Install**

## Updating the Plugin

When you release a new version:

1. Build the new version:
   ```powershell
   dotnet build -c Release
   ```

2. Copy DLL to repository folder:
   ```powershell
   Copy-Item "Jellyfin.Plugin.Template\bin\Release\net9.0\Jellyfin.Plugin.Template.dll" -Destination "repository\"
   ```

3. Update version in `Directory.Build.props` (if needed)

4. Regenerate manifest:
   ```powershell
   cd repository
   .\generate-manifest.ps1
   ```

5. Commit and push:
   ```powershell
   cd ..
   git add .
   git commit -m "Release version X.X.X.X"
   git push
   ```

6. Jellyfin will automatically detect the update and prompt users to install it

## Using GitHub Releases (Recommended for Production)

For better version management, you can use GitHub Releases:

1. **Create a Release:**
   - Go to your GitHub repository
   - Click **Releases** > **Create a new release**
   - Tag: `v1.0.0.0`
   - Title: `MetadataRenamer v1.0.0.0`
   - Upload `Jellyfin.Plugin.Template.dll` as a release asset

2. **Update manifest.json SourceUrl:**
   ```json
   "SourceUrl": "https://github.com/loguefx/Jellyfin_MetaData_Renamer/releases/download/v1.0.0.0/Jellyfin.Plugin.Template.dll"
   ```

3. **Regenerate manifest:**
   ```powershell
   .\generate-manifest.ps1 -SourceUrl "https://github.com/loguefx/Jellyfin_MetaData_Renamer/releases/download/v1.0.0.0/Jellyfin.Plugin.Template.dll"
   ```

## Repository Structure

Your repository structure should look like:
```
Jellyfin_MetaData_Renamer/
├── MetadataRenamer/
│   ├── repository/
│   │   ├── manifest.json          ← Plugin repository manifest
│   │   ├── Jellyfin.Plugin.Template.dll  ← Plugin DLL (after build)
│   │   ├── generate-manifest.ps1
│   │   └── README.md
│   └── ... (source code)
└── README.md
```

## Troubleshooting

- **404 errors**: Make sure files are pushed to GitHub and paths are correct
- **Checksum mismatch**: Regenerate manifest after building
- **Plugin not showing**: Verify manifest.json is valid JSON and accessible
