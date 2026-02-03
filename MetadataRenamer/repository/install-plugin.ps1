# MetadataRenamer Plugin Installation Script
# This script helps diagnose and manually install the plugin

Write-Host "=== MetadataRenamer Plugin Installation ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: Find Jellyfin installation
$possiblePaths = @(
    "C:\ProgramData\Jellyfin\Server",
    "$env:LOCALAPPDATA\jellyfin",
    "$env:APPDATA\jellyfin",
    "C:\Program Files\Jellyfin\Server"
)

$jellyfinPath = $null
foreach ($path in $possiblePaths) {
    if (Test-Path $path) {
        $jellyfinPath = $path
        Write-Host "✅ Found Jellyfin at: $path" -ForegroundColor Green
        break
    }
}

if (-not $jellyfinPath) {
    Write-Host "❌ Could not find Jellyfin installation" -ForegroundColor Red
    Write-Host "Please specify your Jellyfin installation path:" -ForegroundColor Yellow
    $jellyfinPath = Read-Host "Jellyfin Server Path"
}

# Step 2: Set plugin paths
$pluginsPath = Join-Path $jellyfinPath "plugins"
$pluginFolder = Join-Path $pluginsPath "MetadataRenamer"
$dllPath = Join-Path $pluginFolder "Jellyfin.Plugin.MetadataRenamer.dll"

Write-Host ""
Write-Host "=== Plugin Paths ===" -ForegroundColor Cyan
Write-Host "Plugins directory: $pluginsPath"
Write-Host "Plugin folder: $pluginFolder"
Write-Host "DLL path: $dllPath"
Write-Host ""

# Step 3: Check if plugin folder exists
if (Test-Path $pluginFolder) {
    Write-Host "⚠️  Plugin folder already exists" -ForegroundColor Yellow
    $existing = Get-ChildItem $pluginFolder -File
    if ($existing) {
        Write-Host "Existing files:" -ForegroundColor Yellow
        $existing | Format-Table Name, Length, LastWriteTime
        Write-Host ""
        $response = Read-Host "Delete existing files? (y/n)"
        if ($response -eq "y") {
            Remove-Item $pluginFolder -Recurse -Force
            Write-Host "✅ Deleted existing plugin folder" -ForegroundColor Green
        }
    }
}

# Step 4: Create plugin folder
if (-not (Test-Path $pluginFolder)) {
    Write-Host "Creating plugin folder..." -ForegroundColor Yellow
    try {
        New-Item -ItemType Directory -Path $pluginFolder -Force | Out-Null
        Write-Host "✅ Created plugin folder" -ForegroundColor Green
    } catch {
        Write-Host "❌ Failed to create plugin folder: $_" -ForegroundColor Red
        exit 1
    }
}

# Step 5: Download DLL
Write-Host ""
Write-Host "=== Downloading Plugin DLL ===" -ForegroundColor Cyan
$dllUrl = "https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.MetadataRenamer.dll"
Write-Host "URL: $dllUrl"

try {
    Write-Host "Downloading..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $dllUrl -OutFile $dllPath -UseBasicParsing
    Write-Host "✅ Downloaded DLL successfully" -ForegroundColor Green
    
    $fileInfo = Get-Item $dllPath
    Write-Host "File size: $($fileInfo.Length) bytes" -ForegroundColor Cyan
    Write-Host "File date: $($fileInfo.LastWriteTime)" -ForegroundColor Cyan
} catch {
    Write-Host "❌ Failed to download DLL: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "You can manually download from:" -ForegroundColor Yellow
    Write-Host $dllUrl
    Write-Host "And copy it to: $pluginFolder"
    exit 1
}

# Step 6: Verify installation
Write-Host ""
Write-Host "=== Verification ===" -ForegroundColor Cyan
if (Test-Path $dllPath) {
    Write-Host "✅ Plugin DLL is installed at:" -ForegroundColor Green
    Write-Host "   $dllPath" -ForegroundColor White
    Write-Host ""
    Write-Host "=== Next Steps ===" -ForegroundColor Cyan
    Write-Host "1. Restart Jellyfin server" -ForegroundColor Yellow
    Write-Host "2. Go to Dashboard > Plugins" -ForegroundColor Yellow
    Write-Host "3. Look for 'MetadataRenamer' in the installed plugins list" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "If the plugin doesn't appear:" -ForegroundColor Yellow
    Write-Host "- Check Jellyfin logs for errors" -ForegroundColor White
    Write-Host "- Verify Jellyfin version is 10.10.x" -ForegroundColor White
    Write-Host "- Check plugin folder permissions" -ForegroundColor White
} else {
    Write-Host "❌ DLL not found after download" -ForegroundColor Red
    exit 1
}
