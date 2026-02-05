# Clear Jellyfin Plugin Cache Script
# This script clears Jellyfin's plugin cache to force a fresh manifest download

$jellyfinDataPath = "C:\ProgramData\Jellyfin\Server"
$cachePath = Join-Path $jellyfinDataPath "cache"
$pluginCachePath = Join-Path $cachePath "plugins"

Write-Host "=== Clear Jellyfin Plugin Cache ===" -ForegroundColor Cyan
Write-Host ""

# Check if Jellyfin service is running
$jellyfinService = Get-Service -Name "JellyfinServer" -ErrorAction SilentlyContinue
if ($jellyfinService -and $jellyfinService.Status -eq "Running") {
    Write-Host "WARNING: Jellyfin service is running!" -ForegroundColor Yellow
    Write-Host "The service must be stopped to clear the cache safely." -ForegroundColor Yellow
    Write-Host ""
    $response = Read-Host "Do you want to stop Jellyfin service? (Y/N)"
    if ($response -eq "Y" -or $response -eq "y") {
        Write-Host "Stopping Jellyfin service..." -ForegroundColor Cyan
        Stop-Service -Name "JellyfinServer" -Force
        Write-Host "✓ Jellyfin service stopped" -ForegroundColor Green
        $shouldRestart = $true
    } else {
        Write-Host "Skipping cache clear. Please stop Jellyfin manually and run this script again." -ForegroundColor Yellow
        exit 1
    }
} else {
    Write-Host "Jellyfin service is not running (or not found)" -ForegroundColor Green
    $shouldRestart = $false
}

Write-Host ""

# Clear plugin cache
if (Test-Path $pluginCachePath) {
    Write-Host "Found plugin cache: $pluginCachePath" -ForegroundColor Cyan
    Write-Host "Deleting plugin cache..." -ForegroundColor Cyan
    Remove-Item -Path $pluginCachePath -Recurse -Force -ErrorAction Stop
    Write-Host "✓ Plugin cache cleared!" -ForegroundColor Green
} else {
    Write-Host "Plugin cache not found: $pluginCachePath" -ForegroundColor Yellow
    Write-Host "This is normal if cache hasn't been created yet." -ForegroundColor Yellow
}

Write-Host ""

# Clear general cache (optional, more aggressive)
if (Test-Path $cachePath) {
    Write-Host "Found general cache: $cachePath" -ForegroundColor Cyan
    $response = Read-Host "Do you want to clear ALL cache (not just plugins)? This is more aggressive. (Y/N)"
    if ($response -eq "Y" -or $response -eq "y") {
        Write-Host "Clearing all cache..." -ForegroundColor Cyan
        Get-ChildItem -Path $cachePath -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "✓ All cache cleared!" -ForegroundColor Green
    } else {
        Write-Host "Skipping general cache clear." -ForegroundColor Yellow
    }
}

Write-Host ""

# Restart Jellyfin if we stopped it
if ($shouldRestart) {
    Write-Host "Starting Jellyfin service..." -ForegroundColor Cyan
    Start-Service -Name "JellyfinServer"
    Write-Host "✓ Jellyfin service started" -ForegroundColor Green
}

Write-Host ""
Write-Host "=== Next Steps ===" -ForegroundColor Cyan
Write-Host "1. Wait 30 seconds for Jellyfin to fully start" -ForegroundColor Yellow
Write-Host "2. Go to Dashboard > Plugins > Repositories" -ForegroundColor Yellow
Write-Host "3. Remove the repository (if present)" -ForegroundColor Yellow
Write-Host "4. Add it again: https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json" -ForegroundColor Yellow
Write-Host "5. Check the plugin catalog - timestamp should be updated" -ForegroundColor Yellow
Write-Host ""
Write-Host "The manifest timestamp on GitHub is now: 2026-02-05T02:10:38.847Z" -ForegroundColor Green
Write-Host ""
