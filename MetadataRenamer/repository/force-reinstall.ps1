# Force Reinstall MetadataRenamer Plugin
# This script completely removes the old plugin and prepares for fresh installation

Write-Host "=== MetadataRenamer Force Reinstall ===" -ForegroundColor Cyan
Write-Host ""

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "⚠️  This script requires Administrator privileges!" -ForegroundColor Red
    Write-Host "Right-click PowerShell and select 'Run as Administrator'" -ForegroundColor Yellow
    exit 1
}

$pluginFolder = "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer"
$cacheFolder = "C:\ProgramData\Jellyfin\Server\cache"
$serviceName = "JellyfinServer"

Write-Host "Step 1: Stopping Jellyfin service..." -ForegroundColor Cyan
try {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq 'Running') {
        Stop-Service -Name $serviceName -Force
        Write-Host "✅ Jellyfin service stopped" -ForegroundColor Green
        Start-Sleep -Seconds 3
    } else {
        Write-Host "⚠️  Jellyfin service not running" -ForegroundColor Yellow
    }
} catch {
    Write-Host "⚠️  Could not stop service: $_" -ForegroundColor Yellow
}

Write-Host "`nStep 2: Removing plugin folder..." -ForegroundColor Cyan
if (Test-Path $pluginFolder) {
    try {
        Remove-Item $pluginFolder -Recurse -Force
        Write-Host "✅ Plugin folder removed: $pluginFolder" -ForegroundColor Green
    } catch {
        Write-Host "❌ Failed to remove plugin folder: $_" -ForegroundColor Red
        Write-Host "You may need to close any programs using the DLL" -ForegroundColor Yellow
    }
} else {
    Write-Host "⚠️  Plugin folder not found (already removed)" -ForegroundColor Yellow
}

Write-Host "`nStep 3: Clearing cache..." -ForegroundColor Cyan
if (Test-Path $cacheFolder) {
    try {
        Remove-Item "$cacheFolder\*" -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "✅ Cache cleared" -ForegroundColor Green
    } catch {
        Write-Host "⚠️  Some cache files may be locked: $_" -ForegroundColor Yellow
    }
} else {
    Write-Host "⚠️  Cache folder not found" -ForegroundColor Yellow
}

Write-Host "`nStep 4: Starting Jellyfin service..." -ForegroundColor Cyan
try {
    Start-Service -Name $serviceName
    Write-Host "✅ Jellyfin service started" -ForegroundColor Green
} catch {
    Write-Host "⚠️  Could not start service: $_" -ForegroundColor Yellow
}

Write-Host "`n=== Next Steps (Manual) ===" -ForegroundColor Cyan
Write-Host "1. Open Jellyfin web UI" -ForegroundColor White
Write-Host "2. Go to Dashboard > Plugins > Repositories" -ForegroundColor White
Write-Host "3. Remove the repository (if present)" -ForegroundColor White
Write-Host "4. Wait 30 seconds" -ForegroundColor White
Write-Host "5. Click + to add repository:" -ForegroundColor White
Write-Host "   https://raw.githubusercontent.com/loguefx/Jellyfin_MetaData_Renamer/main/MetadataRenamer/repository/manifest.json" -ForegroundColor Cyan
Write-Host "6. Wait 60 seconds" -ForegroundColor White
Write-Host "7. Go to Dashboard > Plugins > Catalog" -ForegroundColor White
Write-Host "8. Find MetadataRenamer and click Install" -ForegroundColor White
Write-Host "9. Restart Jellyfin service" -ForegroundColor White
Write-Host "10. Verify plugin shows 'Active' status" -ForegroundColor White

Write-Host "`n✅ Plugin folder removed. Ready for fresh installation!" -ForegroundColor Green
