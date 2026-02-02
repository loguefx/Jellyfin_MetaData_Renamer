# MetadataRenamer Plugin Repository

This directory contains the plugin repository manifest for the MetadataRenamer plugin.

## Quick Setup for Local Testing

### Option 1: Using a Local Web Server

1. **Build the plugin:**
   ```bash
   cd ..
   dotnet build -c Release
   ```

2. **Generate the manifest:**
   - **Windows (PowerShell):**
     ```powershell
     .\generate-manifest.ps1
     ```
   - **Linux/Mac (Bash):**
     ```bash
     chmod +x generate-manifest.sh
     ./generate-manifest.sh
     ```

3. **Start a local web server:**
   - **Python 3:**
     ```bash
     python -m http.server 8000
     ```
   - **Node.js (http-server):**
     ```bash
     npx http-server -p 8000
     ```

4. **Copy the DLL to the repository folder:**
   ```bash
   copy ..\Jellyfin.Plugin.Template\bin\Release\net9.0\Jellyfin.Plugin.Template.dll .
   ```

5. **Update manifest.json SourceUrl:**
   - Change `SourceUrl` to: `http://YOUR_IP_ADDRESS:8000/Jellyfin.Plugin.Template.dll`
   - Example: `http://192.168.0.180:8000/Jellyfin.Plugin.Template.dll`

6. **Add repository to Jellyfin:**
   - Go to Dashboard > Plugins > Repositories
   - Click the `+` button
   - Enter: `http://YOUR_IP_ADDRESS:8000/manifest.json`
   - Click OK

## Option 2: Using GitHub (Recommended for Production)

1. **Create a GitHub repository** (or use existing)

2. **Upload files:**
   - Upload `Jellyfin.Plugin.Template.dll` to the `repository/` folder
   - Upload `manifest.json` to the `repository/` folder

3. **Get raw file URLs:**
   - Manifest: `https://raw.githubusercontent.com/YOUR_USERNAME/YOUR_REPO/main/repository/manifest.json`
   - DLL: `https://raw.githubusercontent.com/YOUR_USERNAME/YOUR_REPO/main/repository/Jellyfin.Plugin.Template.dll`

4. **Update manifest.json:**
   - Run the generate script with your GitHub URL:
     ```powershell
     .\generate-manifest.ps1 -SourceUrl "https://raw.githubusercontent.com/YOUR_USERNAME/YOUR_REPO/main/repository/Jellyfin.Plugin.Template.dll"
     ```

5. **Commit and push to GitHub**

6. **Add repository to Jellyfin:**
   - Go to Dashboard > Plugins > Repositories
   - Click the `+` button
   - Enter your manifest.json URL
   - Click OK

## Option 3: Using GitHub Releases (Best Practice)

1. **Create a GitHub release:**
   - Tag: `v1.0.0.0`
   - Upload `Jellyfin.Plugin.Template.dll` as a release asset

2. **Get the release asset URL:**
   - Example: `https://github.com/YOUR_USERNAME/YOUR_REPO/releases/download/v1.0.0.0/Jellyfin.Plugin.Template.dll`

3. **Update manifest.json SourceUrl** to point to the release asset

4. **Host manifest.json** on GitHub (in repository root or docs folder)

5. **Add repository to Jellyfin** using the manifest.json URL

## Manifest Structure

The `manifest.json` file contains:
- **RepositoryName**: Display name for the repository
- **RepositoryDescription**: Description of the repository
- **Plugins**: Array of plugin entries with:
  - **Name**: Plugin name
  - **Guid**: Unique plugin identifier
  - **Version**: Plugin version
  - **TargetAbi**: Jellyfin API version (must match your Jellyfin server version)
  - **Overview**: Short description
  - **Description**: Detailed description
  - **Category**: Plugin category
  - **Owner**: Plugin owner/author
  - **SourceUrl**: URL to download the plugin DLL
  - **Checksum**: SHA256 hash of the DLL (auto-generated)
  - **Timestamp**: Last update timestamp (auto-generated)

## Updating the Plugin

When you release a new version:

1. Build the new version
2. Run the generate script to update checksum and timestamp
3. Update the version in `manifest.json`
4. Upload the new DLL to your hosting location
5. Jellyfin will automatically detect the update and prompt users to install it

## Troubleshooting

- **Plugin not showing up**: Check that the manifest.json URL is accessible and valid JSON
- **Installation fails**: Verify the DLL URL is correct and the checksum matches
- **Version mismatch**: Ensure `TargetAbi` matches your Jellyfin server version (10.9.0.0 for Jellyfin 10.9.x)
