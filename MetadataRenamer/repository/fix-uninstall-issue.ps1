# Fix Plugin Uninstall Issue
# This script handles the case where Jellyfin can't uninstall a plugin that failed to load
# Uses the same robust approach as uninstall-plugin.ps1

param(
    [switch]$Force,
    [string]$JellyfinPath = ""
)

Write-Host "=== Fix MetadataRenamer Uninstall Issue ===" -ForegroundColor Cyan
Write-Host "This script handles plugins that failed to load (NotSupported status)" -ForegroundColor Yellow
Write-Host ""

# Check if running as Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "⚠️  This script requires Administrator privileges!" -ForegroundColor Red
    Write-Host "Right-click PowerShell and select 'Run as Administrator'" -ForegroundColor Yellow
    exit 1
}

$pluginGuid = "eb5d7894-8eef-4b36-aa6f-5d124e828ce1"
$serviceName = "JellyfinServer"

# Function to find Jellyfin installation path
function Find-JellyfinPath {
    $possiblePaths = @(
        "C:\ProgramData\Jellyfin\Server",
        "$env:LOCALAPPDATA\jellyfin",
        "$env:APPDATA\jellyfin",
        "C:\Program Files\Jellyfin\Server"
    )
    
    if ($JellyfinPath -and (Test-Path $JellyfinPath)) {
        return $JellyfinPath
    }
    
    foreach ($path in $possiblePaths) {
        if (Test-Path $path) {
            return $path
        }
    }
    
    return $null
}

# Function to stop Jellyfin service and kill processes
function Stop-JellyfinCompletely {
    Write-Host "Step 1: Stopping Jellyfin service and processes..." -ForegroundColor Cyan
    
    # Stop Windows service
    try {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') {
            Write-Host "  Stopping Jellyfin service..." -ForegroundColor Yellow
            Stop-Service -Name $serviceName -Force -ErrorAction Stop
            Write-Host "  ✅ Service stopped" -ForegroundColor Green
            Start-Sleep -Seconds 3
        } else {
            Write-Host "  ℹ️  Jellyfin service not running" -ForegroundColor Cyan
        }
    } catch {
        Write-Host "  ⚠️  Could not stop service: $_" -ForegroundColor Yellow
    }
    
    # Kill any remaining Jellyfin processes
    $processes = Get-Process -Name "jellyfin" -ErrorAction SilentlyContinue
    if ($processes) {
        Write-Host "  Found $($processes.Count) Jellyfin process(es), terminating..." -ForegroundColor Yellow
        foreach ($proc in $processes) {
            try {
                Stop-Process -Id $proc.Id -Force -ErrorAction Stop
                Write-Host "  ✅ Terminated process: $($proc.ProcessName) (PID: $($proc.Id))" -ForegroundColor Green
            } catch {
                Write-Host "  ⚠️  Could not terminate process $($proc.Id): $_" -ForegroundColor Yellow
            }
        }
        Start-Sleep -Seconds 2
    }
    
    # Also check for processes that might have DLLs loaded
    $allProcesses = Get-Process | Where-Object { $_.Path -like "*jellyfin*" -or $_.ProcessName -like "*jellyfin*" }
    if ($allProcesses) {
        Write-Host "  Found additional Jellyfin-related processes, terminating..." -ForegroundColor Yellow
        foreach ($proc in $allProcesses) {
            try {
                Stop-Process -Id $proc.Id -Force -ErrorAction Stop
                Write-Host "  ✅ Terminated: $($proc.ProcessName) (PID: $($proc.Id))" -ForegroundColor Green
            } catch {
                Write-Host "  ⚠️  Could not terminate process $($proc.Id): $_" -ForegroundColor Yellow
            }
        }
        Start-Sleep -Seconds 2
    }
}

# Function to unlock and remove files
function Remove-FileWithRetry {
    param(
        [string]$Path,
        [int]$MaxRetries = 3
    )
    
    if (-not (Test-Path $Path)) {
        return $true
    }
    
    for ($i = 1; $i -le $MaxRetries; $i++) {
        try {
            # Try to take ownership and grant permissions
            $item = Get-Item $Path -Force -ErrorAction Stop
            
            # Remove read-only attribute
            if ($item.Attributes -band [System.IO.FileAttributes]::ReadOnly) {
                $item.Attributes = $item.Attributes -band (-bnot [System.IO.FileAttributes]::ReadOnly)
            }
            
            # Try to unlock DLL files
            if ($item.Extension -eq ".dll") {
                try {
                    takeown /F $item.FullName /A 2>$null | Out-Null
                    icacls $item.FullName /grant Administrators:F /T 2>$null | Out-Null
                } catch {
                    # Ignore errors from takeown/icacls
                }
            }
            
            # Remove the item
            Remove-Item $Path -Recurse -Force -ErrorAction Stop
            return $true
        } catch {
            if ($i -lt $MaxRetries) {
                Write-Host "    Attempt $i failed, retrying in 2 seconds..." -ForegroundColor Yellow
                Start-Sleep -Seconds 2
            } else {
                Write-Host "    ❌ Failed after $MaxRetries attempts: $_" -ForegroundColor Red
                return $false
            }
        }
    }
    
    return $false
}

# Find Jellyfin installation
$jellyfinPath = Find-JellyfinPath
if (-not $jellyfinPath) {
    Write-Host "❌ Could not find Jellyfin installation" -ForegroundColor Red
    Write-Host "Please specify the path manually:" -ForegroundColor Yellow
    Write-Host "  .\fix-uninstall-issue.ps1 -JellyfinPath 'C:\Path\To\Jellyfin\Server'" -ForegroundColor Cyan
    exit 1
}

Write-Host "✅ Found Jellyfin installation: $jellyfinPath" -ForegroundColor Green
Write-Host ""

# Stop Jellyfin completely
Stop-JellyfinCompletely
Write-Host ""

# Find all possible plugin folders
Write-Host "Step 2: Finding plugin folders..." -ForegroundColor Cyan
$pluginsPath = Join-Path $jellyfinPath "plugins"
$pluginFolders = @()

if (Test-Path $pluginsPath) {
    # Check for exact folder name
    $exactFolder = Join-Path $pluginsPath "MetadataRenamer"
    if (Test-Path $exactFolder) {
        $pluginFolders += $exactFolder
    }
    
    # Check for versioned folders (e.g., MetadataRenamer_1.0.0.0)
    $versionedFolders = Get-ChildItem -Path $pluginsPath -Directory -Filter "MetadataRenamer_*" -ErrorAction SilentlyContinue
    foreach ($folder in $versionedFolders) {
        $pluginFolders += $folder.FullName
    }
    
    # Also check for any folder containing our DLL
    $allFolders = Get-ChildItem -Path $pluginsPath -Directory -ErrorAction SilentlyContinue
    foreach ($folder in $allFolders) {
        $dllPath = Join-Path $folder.FullName "Jellyfin.Plugin.MetadataRenamer.dll"
        if (Test-Path $dllPath) {
            if ($pluginFolders -notcontains $folder.FullName) {
                $pluginFolders += $folder.FullName
            }
        }
    }
}

if ($pluginFolders.Count -eq 0) {
    Write-Host "  ℹ️  No plugin folders found (already removed)" -ForegroundColor Cyan
} else {
    Write-Host "  Found $($pluginFolders.Count) plugin folder(s):" -ForegroundColor Yellow
    foreach ($folder in $pluginFolders) {
        Write-Host "    - $folder" -ForegroundColor White
    }
}

Write-Host ""

# Remove plugin folders
if ($pluginFolders.Count -gt 0) {
    Write-Host "Step 3: Removing plugin folders..." -ForegroundColor Cyan
    foreach ($folder in $pluginFolders) {
        Write-Host "  Removing: $folder" -ForegroundColor Yellow
        
        # Try to unlock DLL files first
        $dllFiles = Get-ChildItem -Path $folder -Filter "*.dll" -Recurse -ErrorAction SilentlyContinue
        foreach ($dll in $dllFiles) {
            try {
                takeown /F $dll.FullName /A 2>$null | Out-Null
                icacls $dll.FullName /grant Administrators:F 2>$null | Out-Null
                Write-Host "    Unlocked: $($dll.Name)" -ForegroundColor Green
            } catch {
                Write-Host "    ⚠️  Could not unlock: $($dll.Name)" -ForegroundColor Yellow
            }
        }
        
        if (Remove-FileWithRetry -Path $folder) {
            Write-Host "  ✅ Removed successfully" -ForegroundColor Green
        } else {
            Write-Host "  ❌ Failed to remove (may be locked by another process)" -ForegroundColor Red
            Write-Host "  Trying alternative method..." -ForegroundColor Yellow
            
            # Alternative: Use cmd to remove
            try {
                cmd /c "rmdir /s /q `"$folder`"" 2>$null
                Start-Sleep -Seconds 1
                if (-not (Test-Path $folder)) {
                    Write-Host "  ✅ Removed using alternative method" -ForegroundColor Green
                } else {
                    Write-Host "  ❌ Still exists - manual removal required" -ForegroundColor Red
                }
            } catch {
                Write-Host "  ❌ Alternative method also failed" -ForegroundColor Red
            }
        }
    }
    Write-Host ""
}

# Remove config files
Write-Host "Step 4: Removing plugin configuration files..." -ForegroundColor Cyan
$configPaths = @(
    Join-Path $jellyfinPath "config\plugins\$pluginGuid.xml",
    Join-Path $jellyfinPath "config\plugins\MetadataRenamer.xml",
    Join-Path $jellyfinPath "data\plugins\$pluginGuid.xml",
    Join-Path $jellyfinPath "data\plugins\MetadataRenamer.xml"
)

$configRemoved = 0
foreach ($configPath in $configPaths) {
    if (Test-Path $configPath) {
        Write-Host "  Removing: $configPath" -ForegroundColor Yellow
        if (Remove-FileWithRetry -Path $configPath) {
            Write-Host "  ✅ Removed" -ForegroundColor Green
            $configRemoved++
        } else {
            Write-Host "  ⚠️  Could not remove" -ForegroundColor Yellow
        }
    }
}

if ($configRemoved -eq 0) {
    Write-Host "  ℹ️  No config files found" -ForegroundColor Cyan
}
Write-Host ""

# Verify removal
Write-Host "Step 5: Verifying removal..." -ForegroundColor Cyan
$stillExists = @()
foreach ($folder in $pluginFolders) {
    if (Test-Path $folder) {
        $stillExists += $folder
    }
}

if ($stillExists.Count -gt 0) {
    Write-Host "  ⚠️  Warning: Some folders still exist:" -ForegroundColor Yellow
    foreach ($folder in $stillExists) {
        Write-Host "    - $folder" -ForegroundColor Red
        
        # Check for locked files
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
            Write-Host "      Found locked files - may be in use by another process" -ForegroundColor Red
        }
    }
    Write-Host ""
    Write-Host "  Try:" -ForegroundColor Yellow
    Write-Host "    1. Close all Jellyfin-related processes" -ForegroundColor White
    Write-Host "    2. Restart your computer" -ForegroundColor White
    Write-Host "    3. Run this script again" -ForegroundColor White
    Write-Host ""
} else {
    Write-Host "  ✅ All plugin folders removed successfully!" -ForegroundColor Green
    Write-Host ""
}

# Start Jellyfin service
Write-Host "Step 6: Starting Jellyfin service..." -ForegroundColor Cyan
try {
    Start-Service -Name $serviceName -ErrorAction Stop
    Write-Host "  ✅ Jellyfin service started" -ForegroundColor Green
} catch {
    Write-Host "  ⚠️  Could not start service: $_" -ForegroundColor Yellow
    Write-Host "  Please start Jellyfin manually" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== Uninstall Complete ===" -ForegroundColor Green
Write-Host "Plugin folders found: $($pluginFolders.Count)" -ForegroundColor White
Write-Host "Plugin folders removed: $($pluginFolders.Count - $stillExists.Count)" -ForegroundColor White
Write-Host "Config files removed: $configRemoved" -ForegroundColor White
Write-Host ""

if ($stillExists.Count -eq 0) {
    Write-Host "✅ Fix completed successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "1. Open Jellyfin web UI" -ForegroundColor White
    Write-Host "2. Go to Dashboard > Plugins" -ForegroundColor White
    Write-Host "3. Verify MetadataRenamer is no longer listed" -ForegroundColor White
    Write-Host "4. If it still appears, refresh the page" -ForegroundColor White
    Write-Host "5. Reinstall from Catalog to get the .NET 8.0 version" -ForegroundColor White
} else {
    Write-Host "⚠️  Fix completed with warnings" -ForegroundColor Yellow
    Write-Host "Some files could not be removed. See above for details." -ForegroundColor Yellow
    exit 1
}
