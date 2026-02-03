# PowerShell script to generate manifest.json with checksum and timestamp
# Usage: .\generate-manifest.ps1 [path-to-dll]
# This script supports multiple Jellyfin versions by creating multiple version entries

param(
    [string]$DllPath = "..\Jellyfin.Plugin.Template\bin\Release\net8.0\Jellyfin.Plugin.MetadataRenamer.dll",
    [string]$SourceUrl = "https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.MetadataRenamer.zip",
    [string[]]$TargetAbis = @("10.10.0.0", "10.11.0.0")
)

$ErrorActionPreference = "Stop"

Write-Host "Generating manifest.json..." -ForegroundColor Green

# Check if DLL exists
if (-not (Test-Path $DllPath)) {
    Write-Host "Error: DLL not found at $DllPath" -ForegroundColor Red
    Write-Host "Please build the plugin first: dotnet build -c Release" -ForegroundColor Yellow
    exit 1
}

# Create ZIP package from DLL (Jellyfin requires ZIP, not raw DLL)
Write-Host "Creating ZIP package..." -ForegroundColor Cyan
$zipPath = Join-Path $PSScriptRoot "Jellyfin.Plugin.MetadataRenamer.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path $DllPath -DestinationPath $zipPath -Force
Write-Host "ZIP created: $zipPath" -ForegroundColor Green

# Calculate MD5 checksum of ZIP file (Jellyfin uses MD5, not SHA256)
Write-Host "Calculating ZIP checksum (MD5)..." -ForegroundColor Cyan
$hash = Get-FileHash -Path $zipPath -Algorithm MD5
$checksum = $hash.Hash.ToUpper()  # Jellyfin expects uppercase MD5
Write-Host "Checksum (MD5): $checksum" -ForegroundColor Cyan

# Get current timestamp in UTC (properly formatted with Z suffix)
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")

# Get version from DLL if possible
$version = "1.0.0.0"
try {
    $dllInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($DllPath)
    if ($dllInfo.FileVersion) {
        $version = $dllInfo.FileVersion
    }
} catch {
    Write-Host "Warning: Could not read version from DLL, using default: $version" -ForegroundColor Yellow
}

# Create versions array for all TargetAbis
$versions = @()
foreach ($targetAbi in $TargetAbis) {
    $versionEntry = @{
        version = $version
        targetAbi = $targetAbi
        sourceUrl = $SourceUrl
        checksum = $checksum
        timestamp = $timestamp
        changelog = "Automatic series folder renaming on metadata identification"
    }
    $versions += $versionEntry
    Write-Host "Added version entry for TargetAbi: $targetAbi" -ForegroundColor Green
}

# Create manifest structure
$manifest = @(
    @{
        guid = "eb5d7894-8eef-4b36-aa6f-5d124e828ce1"
        name = "MetadataRenamer"
        description = "MetadataRenamer automatically renames series folders when you identify a series in Jellyfin. It uses the format: {SeriesName} ({Year}) [{provider}-{id}], for example: 'The Flash (2014) [tvdb-279121]'. The plugin only renames when provider IDs change (inferring that Identify/refresh just happened), includes safety features like dry-run mode, collision checks, and per-item cooldown periods."
        overview = "Automatically renames series folders to {SeriesName} ({Year}) [{provider}-{id}] format when metadata is identified"
        owner = "loguefx"
        category = "General"
        versions = $versions
    }
)

# Save manifest as properly formatted JSON (UTF-8 without BOM)
# Force array format - PowerShell's ConvertTo-Json may convert single-item arrays to objects
$manifestPath = Join-Path $PSScriptRoot "manifest.json"

# Convert to JSON - ensure it's always an array format
$jsonContent = $manifest | ConvertTo-Json -Depth 10

# PowerShell 5.1 may output object instead of array for single items - force array format
if (-not $jsonContent.TrimStart().StartsWith('[')) {
    # Wrap in array brackets and indent
    $lines = $jsonContent -split "`n"
    $indented = $lines | ForEach-Object { "  $_" }
    $jsonContent = "[`n" + ($indented -join "`n") + "`n]"
}

# Use UTF8 encoding without BOM (Jellyfin requires this)
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($manifestPath, $jsonContent, $utf8NoBom)

Write-Host "`nManifest updated successfully!" -ForegroundColor Green
Write-Host "Checksum: $checksum" -ForegroundColor Cyan
Write-Host "Timestamp: $timestamp" -ForegroundColor Cyan
Write-Host "Version: $version" -ForegroundColor Cyan
Write-Host "TargetAbis: $($TargetAbis -join ', ')" -ForegroundColor Cyan
Write-Host "`nManifest saved to: $manifestPath" -ForegroundColor Green
Write-Host "`nNext steps:" -ForegroundColor Yellow
Write-Host "1. Commit and push manifest.json to GitHub" -ForegroundColor White
Write-Host "2. Verify manifest is accessible at:" -ForegroundColor White
Write-Host "   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json" -ForegroundColor Cyan
Write-Host "3. Add repository URL to Jellyfin: Dashboard > Plugins > Repositories" -ForegroundColor White
