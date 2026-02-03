# Fix Plugin Uninstall Issue
# This script handles the case where Jellyfin can't uninstall a plugin that failed to load

Write-Host "=== Fix MetadataRenamer Uninstall Issue ===" -ForegroundColor Cyan
Write-Host ""

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsPrincipal]::Administrator)
if (-not $isAdmin) {
    Write-Host "⚠️  This script requires Administrator privileges!" -ForegroundColor Red
    Write-Host "Right-click PowerShell and select 'Run as Administrator'" -ForegroundColor Yellow
    exit 1
}

$serviceName = "JellyfinServer"
$pluginFolder = "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer_1.0.0.0"
$pluginFolderAlt = "C:\ProgramData\Jellyfin\Server\plugins\MetadataRenamer"
$configPath = "C:\ProgramData\Jellyfin\Server\config\plugins"
$pluginGuid = "eb5d7894-8eef-4b36-aa6f-5d124e828ce1"

Write-Host "Step 1: Stopping Jellyfin service..." -ForegroundColor Cyan
try {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq 'Running') {
        Stop-Service -Name $serviceName -Force
        Write-Host "✅ Jellyfin service stopped" -ForegroundColor Green
        Start-Sleep -Seconds 5
    } else {
        Write-Host "⚠️  Jellyfin service not running" -ForegroundColor Yellow
    }
} catch {
    Write-Host "⚠️  Could not stop service: $_" -ForegroundColor Yellow
    Write-Host "Please stop Jellyfin manually and press any key to continue..." -ForegroundColor Yellow
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
}

Write-Host "`nStep 2: Finding and removing plugin folders..." -ForegroundColor Cyan

# Try both possible folder names
$foldersToRemove = @()
if (Test-Path $pluginFolder) {
    $foldersToRemove += $pluginFolder
}
if (Test-Path $pluginFolderAlt) {
    $foldersToRemove += $pluginFolderAlt
}

if ($foldersToRemove.Count -eq 0) {
    Write-Host "⚠️  No plugin folders found" -ForegroundColor Yellow
} else {
    foreach ($folder in $foldersToRemove) {
        Write-Host "Found plugin folder: $folder" -ForegroundColor Yellow
        
        # Try to unlock DLL files first
        Write-Host "Attempting to unlock DLL files..." -ForegroundColor Cyan
        $dllFiles = Get-ChildItem -Path $folder -Filter "*.dll" -Recurse -ErrorAction SilentlyContinue
        foreach ($dll in $dllFiles) {
            try {
                # Try to take ownership and remove read-only
                takeown /F $dll.FullName /A 2>$null | Out-Null
                icacls $dll.FullName /grant Administrators:F 2>$null | Out-Null
                $dll.IsReadOnly = $false
                Write-Host "  Unlocked: $($dll.Name)" -ForegroundColor Green
            } catch {
                Write-Host "  Could not unlock: $($dll.Name)" -ForegroundColor Yellow
            }
        }
        
        # Remove the folder
        try {
            Remove-Item $folder -Recurse -Force -ErrorAction Stop
            Write-Host "✅ Removed: $folder" -ForegroundColor Green
        } catch {
            Write-Host "❌ Failed to remove: $folder" -ForegroundColor Red
            Write-Host "   Error: $_" -ForegroundColor Red
            Write-Host "`nTrying alternative method..." -ForegroundColor Yellow
            
            # Alternative: Use cmd to remove
            try {
                $folderPath = $folder.Replace('\', '\\')
                cmd /c "rmdir /s /q `"$folder`"" 2>$null
                if (-not (Test-Path $folder)) {
                    Write-Host "✅ Removed using alternative method" -ForegroundColor Green
                } else {
                    Write-Host "❌ Still exists - manual removal required" -ForegroundColor Red
                    Write-Host "   Please manually delete: $folder" -ForegroundColor Yellow
                }
            } catch {
                Write-Host "❌ Alternative method also failed" -ForegroundColor Red
            }
        }
    }
}

Write-Host "`nStep 3: Removing plugin configuration files..." -ForegroundColor Cyan
$configFiles = @(
    Join-Path $configPath "$pluginGuid.xml",
    Join-Path $configPath "MetadataRenamer.xml"
)

foreach ($configFile in $configFiles) {
    if (Test-Path $configFile) {
        try {
            Remove-Item $configFile -Force
            Write-Host "✅ Removed config: $configFile" -ForegroundColor Green
        } catch {
            Write-Host "⚠️  Could not remove: $configFile" -ForegroundColor Yellow
        }
    }
}

Write-Host "`nStep 4: Checking for locked files..." -ForegroundColor Cyan
$remainingFolders = @()
if (Test-Path $pluginFolder) { $remainingFolders += $pluginFolder }
if (Test-Path $pluginFolderAlt) { $remainingFolders += $pluginFolderAlt }

if ($remainingFolders.Count -gt 0) {
    Write-Host "⚠️  Some folders still exist. Checking for locked files..." -ForegroundColor Yellow
    foreach ($folder in $remainingFolders) {
        $lockedFiles = Get-ChildItem -Path $folder -Recurse -ErrorAction SilentlyContinue | Where-Object {
            try {
                $file = [System.IO.File]::Open($_.FullName, 'Open', 'ReadWrite', 'None')
                $file.Close()
                $false
            } catch {
                $true
            }
        }
        
        if ($lockedFiles) {
            Write-Host "Found locked files in: $folder" -ForegroundColor Red
            Write-Host "These files may be in use by another process." -ForegroundColor Yellow
            Write-Host "Try:" -ForegroundColor Yellow
            Write-Host "1. Close all Jellyfin-related processes" -ForegroundColor White
            Write-Host "2. Restart your computer" -ForegroundColor White
            Write-Host "3. Run this script again" -ForegroundColor White
        }
    }
} else {
    Write-Host "✅ All plugin folders removed successfully!" -ForegroundColor Green
}

Write-Host "`nStep 5: Starting Jellyfin service..." -ForegroundColor Cyan
try {
    Start-Service -Name $serviceName
    Write-Host "✅ Jellyfin service started" -ForegroundColor Green
} catch {
    Write-Host "⚠️  Could not start service: $_" -ForegroundColor Yellow
    Write-Host "Please start Jellyfin manually" -ForegroundColor Yellow
}

Write-Host "`n=== Uninstall Complete ===" -ForegroundColor Green
Write-Host "`nNext steps:" -ForegroundColor Cyan
Write-Host "1. Open Jellyfin web UI" -ForegroundColor White
Write-Host "2. Go to Dashboard > Plugins" -ForegroundColor White
Write-Host "3. Verify MetadataRenamer is no longer listed" -ForegroundColor White
Write-Host "4. If it still appears, refresh the page" -ForegroundColor White
Write-Host "5. Reinstall from Catalog to get the .NET 8.0 version" -ForegroundColor White
