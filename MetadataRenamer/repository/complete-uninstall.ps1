# Complete Plugin Uninstall Script
# This script removes all traces of the MetadataRenamer plugin

Write-Host "=== Complete MetadataRenamer Uninstall ===" -ForegroundColor Cyan
Write-Host ""

# Stop Jellyfin service first
Write-Host "⚠️  IMPORTANT: Stop Jellyfin service before running this script!" -ForegroundColor Yellow
Write-Host "Press any key after stopping Jellyfin..." -ForegroundColor Yellow
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

$jellyfinPath = "C:\ProgramData\Jellyfin\Server"
$pluginFolder = Join-Path $jellyfinPath "plugins\MetadataRenamer"
$pluginGuid = "eb5d7894-8eef-4b36-aa6f-5d124e828ce1"

Write-Host "`n=== Removing Plugin Files ===" -ForegroundColor Cyan

# Remove plugin folder
if (Test-Path $pluginFolder) {
    Write-Host "Removing plugin folder: $pluginFolder" -ForegroundColor Yellow
    try {
        Remove-Item $pluginFolder -Recurse -Force
        Write-Host "✅ Plugin folder removed" -ForegroundColor Green
    } catch {
        Write-Host "❌ Failed to remove plugin folder: $_" -ForegroundColor Red
        Write-Host "You may need to close any programs using the DLL" -ForegroundColor Yellow
    }
} else {
    Write-Host "Plugin folder not found (already removed)" -ForegroundColor Cyan
}

# Remove plugin config files
Write-Host "`n=== Removing Plugin Config Files ===" -ForegroundColor Cyan
$configPaths = @(
    Join-Path $jellyfinPath "config\plugins\$pluginGuid.xml",
    Join-Path $jellyfinPath "data\plugins\$pluginGuid.xml",
    Join-Path $jellyfinPath "config\plugins\MetadataRenamer.xml"
)

foreach ($configPath in $configPaths) {
    if (Test-Path $configPath) {
        Write-Host "Removing config: $configPath" -ForegroundColor Yellow
        try {
            Remove-Item $configPath -Force
            Write-Host "✅ Config file removed" -ForegroundColor Green
        } catch {
            Write-Host "⚠️  Could not remove: $_" -ForegroundColor Yellow
        }
    }
}

Write-Host "`n=== Uninstall Complete ===" -ForegroundColor Green
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "1. Start Jellyfin service" -ForegroundColor White
Write-Host "2. Go to Dashboard > Plugins > Catalog" -ForegroundColor White
Write-Host "3. Plugin should now show 'Install' button (not 'Installed')" -ForegroundColor White
Write-Host "4. Click Install to reinstall with new version" -ForegroundColor White
