# Simple local web server for testing the plugin repository
# This will serve the repository folder on port 8000

$ErrorActionPreference = "Stop"

$Port = 8000
$ScriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepositoryPath = $ScriptPath

Write-Host "Starting local web server for MetadataRenamer repository..." -ForegroundColor Green
Write-Host "Serving from: $RepositoryPath" -ForegroundColor Cyan
Write-Host "Port: $Port" -ForegroundColor Cyan
Write-Host ""

# Check if Python is available
$python = Get-Command python -ErrorAction SilentlyContinue
if ($python) {
    Write-Host "Starting Python HTTP server..." -ForegroundColor Yellow
    Write-Host "Access manifest at: http://localhost:$Port/manifest.json" -ForegroundColor Green
    Write-Host "Press Ctrl+C to stop the server" -ForegroundColor Yellow
    Write-Host ""
    Set-Location $RepositoryPath
    python -m http.server $Port
} else {
    # Try Node.js http-server
    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) {
        Write-Host "Python not found, trying Node.js..." -ForegroundColor Yellow
        Write-Host "Installing http-server (if needed)..." -ForegroundColor Yellow
        npx --yes http-server $RepositoryPath -p $Port -c-1
    } else {
        Write-Host "Error: Neither Python nor Node.js found." -ForegroundColor Red
        Write-Host "Please install Python 3 or Node.js to run a local web server." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Alternative: Use any web server (IIS, Apache, nginx) to serve the repository folder" -ForegroundColor Cyan
        exit 1
    }
}
