# Quick Plugin Uninstall Script
# Removes the plugin folder so you can reinstall from Catalog

Write-Host "=== MetadataRenamer Uninstall ===" -ForegroundColor Cyan
Write-Host ""

$pluginFolder = "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer"

if (Test-Path $pluginFolder) {
    Write-Host "Found plugin folder: $pluginFolder" -ForegroundColor Yellow
    Write-Host "⚠️  Make sure Jellyfin service is stopped!" -ForegroundColor Red
    Write-Host ""
    $response = Read-Host "Delete plugin folder? (y/n)"
    
    if ($response -eq "y") {
        try {
            Remove-Item $pluginFolder -Recurse -Force
            Write-Host "✅ Plugin folder removed successfully!" -ForegroundColor Green
            Write-Host ""
            Write-Host "Next steps:" -ForegroundColor Yellow
            Write-Host "1. Start Jellyfin service" -ForegroundColor White
            Write-Host "2. Go to Dashboard > Plugins > Catalog" -ForegroundColor White
            Write-Host "3. MetadataRenamer should now show 'Install' button" -ForegroundColor White
            Write-Host "4. Click Install to get the new .NET 8.0 version" -ForegroundColor White
        } catch {
            Write-Host "❌ Failed to remove: $_" -ForegroundColor Red
            Write-Host "Make sure Jellyfin service is stopped and no programs are using the DLL" -ForegroundColor Yellow
        }
    } else {
        Write-Host "Cancelled" -ForegroundColor Yellow
    }
} else {
    Write-Host "Plugin folder not found (already removed)" -ForegroundColor Green
    Write-Host "You can now reinstall from Catalog" -ForegroundColor Cyan
}
