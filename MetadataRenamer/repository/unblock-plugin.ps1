# Unblock Plugin DLL Script
# This script unblocks the MetadataRenamer plugin DLL to allow it to load in Jellyfin

param(
    [string]$PluginPath = "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0"
)

Write-Host "=== Unblock Plugin DLL Script ===" -ForegroundColor Cyan
Write-Host ""

# Check if running as administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "WARNING: Not running as Administrator. Some operations may fail." -ForegroundColor Yellow
    Write-Host "Right-click PowerShell and select 'Run as Administrator' for best results." -ForegroundColor Yellow
    Write-Host ""
}

# Check if plugin folder exists
if (-not (Test-Path $PluginPath)) {
    Write-Host "ERROR: Plugin folder not found: $PluginPath" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please check:" -ForegroundColor Yellow
    Write-Host "1. Plugin is installed in Jellyfin" -ForegroundColor Yellow
    Write-Host "2. Path is correct: $PluginPath" -ForegroundColor Yellow
    exit 1
}

Write-Host "Plugin folder found: $PluginPath" -ForegroundColor Green
Write-Host ""

# Find DLL file
$dllPath = Join-Path $PluginPath "Jellyfin.Plugin.MetadataRenamer.dll"
if (-not (Test-Path $dllPath)) {
    Write-Host "ERROR: DLL file not found: $dllPath" -ForegroundColor Red
    exit 1
}

Write-Host "DLL file found: $dllPath" -ForegroundColor Green
Write-Host ""

# Check if file is blocked
try {
    $file = Get-Item $dllPath -Force
    $isBlocked = $file.Attributes -band [System.IO.FileAttributes]::ReparsePoint
    
    # Try to unblock
    Write-Host "Attempting to unblock DLL file..." -ForegroundColor Cyan
    Unblock-File -Path $dllPath -ErrorAction Stop
    Write-Host "✓ DLL file unblocked successfully!" -ForegroundColor Green
} catch {
    Write-Host "WARNING: Could not unblock file automatically: $_" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Manual steps:" -ForegroundColor Yellow
    Write-Host "1. Navigate to: $PluginPath" -ForegroundColor Yellow
    Write-Host "2. Right-click Jellyfin.Plugin.MetadataRenamer.dll" -ForegroundColor Yellow
    Write-Host "3. Click Properties" -ForegroundColor Yellow
    Write-Host "4. Check 'Unblock' checkbox (if present)" -ForegroundColor Yellow
    Write-Host "5. Click OK" -ForegroundColor Yellow
}

# Unblock all files in plugin folder (just in case)
Write-Host ""
Write-Host "Unblocking all files in plugin folder..." -ForegroundColor Cyan
try {
    Get-ChildItem -Path $PluginPath -Recurse -File | ForEach-Object {
        try {
            Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
        } catch {
            # Ignore errors for individual files
        }
    }
    Write-Host "✓ All files unblocked!" -ForegroundColor Green
} catch {
    Write-Host "WARNING: Could not unblock all files: $_" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== Next Steps ===" -ForegroundColor Cyan
Write-Host "1. Restart Jellyfin service completely" -ForegroundColor Yellow
Write-Host "2. Check Jellyfin logs for: 'Loaded plugin: MetadataRenamer'" -ForegroundColor Yellow
Write-Host "3. Verify plugin appears in Dashboard > Plugins" -ForegroundColor Yellow
Write-Host ""
Write-Host "If plugin still doesn't load, check:" -ForegroundColor Yellow
Write-Host "- Jellyfin logs for other errors" -ForegroundColor Yellow
Write-Host "- Windows Event Viewer for Application Control policy errors" -ForegroundColor Yellow
Write-Host "- See APPLICATION_CONTROL_BLOCKED.md for more solutions" -ForegroundColor Yellow
Write-Host ""
