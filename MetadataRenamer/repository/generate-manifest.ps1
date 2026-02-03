# PowerShell script to generate manifest.json with checksum and timestamp
# Usage: .\generate-manifest.ps1 [path-to-dll]

param(
    [string]$DllPath = "..\Jellyfin.Plugin.Template\bin\Release\net9.0\Jellyfin.Plugin.MetadataRenamer.dll",
    [string]$SourceUrl = "https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/Jellyfin.Plugin.MetadataRenamer.dll"
)

$ErrorActionPreference = "Stop"

Write-Host "Generating manifest.json..." -ForegroundColor Green

# Check if DLL exists
if (-not (Test-Path $DllPath)) {
    Write-Host "Error: DLL not found at $DllPath" -ForegroundColor Red
    Write-Host "Please build the plugin first: dotnet build -c Release" -ForegroundColor Yellow
    exit 1
}

# Calculate SHA256 checksum
Write-Host "Calculating checksum..." -ForegroundColor Cyan
$hash = Get-FileHash -Path $DllPath -Algorithm SHA256
$checksum = $hash.Hash.ToLower()

# Get current timestamp
$timestamp = Get-Date -Format "yyyy-MM-ddTHH:mm:ss.fffZ"

# Read manifest template
$manifestPath = Join-Path $PSScriptRoot "manifest.json"
if (-not (Test-Path $manifestPath)) {
    Write-Host "Error: manifest.json not found at $manifestPath" -ForegroundColor Red
    exit 1
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

# Jellyfin expects manifest to be an array of plugins directly
if ($manifest -is [Array]) {
    # Update plugin entry (first item in array)
    $manifest[0].Checksum = $checksum
    $manifest[0].Timestamp = $timestamp
    $manifest[0].SourceUrl = $SourceUrl
    
    # Get version from DLL if possible, or use default
    try {
        $dllInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($DllPath)
        if ($dllInfo.FileVersion) {
            $manifest[0].Version = $dllInfo.FileVersion
        }
    } catch {
        Write-Host "Warning: Could not read version from DLL, using manifest version" -ForegroundColor Yellow
    }
} else {
    Write-Host "Error: Manifest must be an array of plugins" -ForegroundColor Red
    exit 1
}

# Save updated manifest with compact formatting (Jellyfin prefers compact JSON)
$jsonContent = $manifest | ConvertTo-Json -Depth 10 -Compress
# Reformat with proper indentation (2 spaces) for readability while keeping it valid
$jsonContent = $jsonContent -replace '":', '": ' -replace ',', ', ' -replace '\{', '{' -replace '\}', '}'
# Use a proper JSON formatter
$jsonContent = ($manifest | ConvertTo-Json -Depth 10) -replace '  ', '  ' | Out-String
Set-Content $manifestPath -Value $jsonContent.Trim() -Encoding UTF8 -NoNewline

Write-Host "`nManifest updated successfully!" -ForegroundColor Green
Write-Host "Checksum: $checksum" -ForegroundColor Cyan
Write-Host "Timestamp: $timestamp" -ForegroundColor Cyan
Write-Host "`nNext steps:" -ForegroundColor Yellow
Write-Host "1. Update SourceUrl in manifest.json to point to your hosted DLL" -ForegroundColor White
Write-Host "2. Host manifest.json and DLL on a web server (GitHub, web server, etc.)" -ForegroundColor White
Write-Host "3. Add repository URL to Jellyfin: Dashboard > Plugins > Repositories" -ForegroundColor White
