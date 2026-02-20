<#
.SYNOPSIS
    Quick setup and run script for Orchestra Portal with dev tunnel.

.DESCRIPTION
    This script handles everything needed to expose the Orchestra Portal to the internet:
    1. Checks/installs devtunnel CLI
    2. Handles login
    3. Starts the server
    4. Creates and hosts the tunnel

.PARAMETER Port
    Local port for the server. Default: 5100

.PARAMETER Anonymous
    Allow anonymous access (required for Power Automate webhooks). Default: true

.EXAMPLE
    .\start-with-tunnel.ps1
    # Quick start with anonymous access on port 5100
#>

param(
    [int]$Port = 5100,
    [switch]$Private
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$projectPath = Join-Path $projectDir 'Orchestra.Playground.Copilot.Portal.csproj'

Write-Host ""
Write-Host "  Orchestra Portal + Dev Tunnel" -ForegroundColor Cyan
Write-Host "  =============================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Check devtunnel CLI
$devtunnel = Get-Command 'devtunnel' -ErrorAction SilentlyContinue
if (-not $devtunnel) {
    Write-Host "[1/4] Installing devtunnel CLI..." -ForegroundColor Yellow
    
    # Try winget first
    $winget = Get-Command 'winget' -ErrorAction SilentlyContinue
    if ($winget) {
        winget install Microsoft.devtunnel --accept-package-agreements --accept-source-agreements
    } else {
        # Manual download
        $downloadUrl = "https://aka.ms/TunnelsCliDownload/win-x64"
        $devtunnelPath = Join-Path $env:LOCALAPPDATA "devtunnel\devtunnel.exe"
        $devtunnelDir = Split-Path $devtunnelPath
        
        if (-not (Test-Path $devtunnelDir)) {
            New-Item -ItemType Directory -Path $devtunnelDir | Out-Null
        }
        
        Write-Host "Downloading devtunnel CLI..." -ForegroundColor Gray
        Invoke-WebRequest -Uri $downloadUrl -OutFile $devtunnelPath
        
        # Add to PATH for this session
        $env:PATH += ";$devtunnelDir"
    }
    
    # Refresh command
    $devtunnel = Get-Command 'devtunnel' -ErrorAction SilentlyContinue
    if (-not $devtunnel) {
        Write-Host "ERROR: Failed to install devtunnel. Please install manually:" -ForegroundColor Red
        Write-Host "  winget install Microsoft.devtunnel" -ForegroundColor Cyan
        exit 1
    }
    Write-Host "[1/4] devtunnel CLI installed" -ForegroundColor Green
} else {
    Write-Host "[1/4] devtunnel CLI found" -ForegroundColor Green
}

# Step 2: Check login
Write-Host "[2/4] Checking devtunnel login..." -ForegroundColor Yellow
$loginCheck = devtunnel user show 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "       Please login to continue:" -ForegroundColor Yellow
    devtunnel user login
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Login failed" -ForegroundColor Red
        exit 1
    }
}
Write-Host "[2/4] Logged in to devtunnel" -ForegroundColor Green

# Step 3: Build and start server
Write-Host "[3/4] Building and starting server..." -ForegroundColor Yellow

# Build first to catch errors early
$buildResult = dotnet build $projectPath --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed" -ForegroundColor Red
    Write-Host $buildResult
    exit 1
}

# Start server in background
$serverJob = Start-Job -ScriptBlock {
    param($projectPath, $port)
    Set-Location (Split-Path -Parent $projectPath)
    dotnet run --project $projectPath --no-build --urls "http://localhost:$port" 2>&1
} -ArgumentList $projectPath, $Port

# Wait for server to start
$maxWait = 15
$waited = 0
while ($waited -lt $maxWait) {
    Start-Sleep -Seconds 1
    $waited++
    
    # Check if server crashed
    if ($serverJob.State -eq 'Failed' -or $serverJob.State -eq 'Completed') {
        Write-Host "ERROR: Server failed to start" -ForegroundColor Red
        Receive-Job $serverJob
        exit 1
    }
    
    # Try to connect
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$Port/api/status" -TimeoutSec 1 -ErrorAction SilentlyContinue
        if ($response.StatusCode -eq 200) {
            break
        }
    } catch {
        # Server not ready yet
    }
}

if ($waited -ge $maxWait) {
    Write-Host "WARNING: Server may still be starting..." -ForegroundColor Yellow
}

Write-Host "[3/4] Server started on http://localhost:$Port" -ForegroundColor Green

# Step 4: Start tunnel
Write-Host "[4/4] Starting dev tunnel..." -ForegroundColor Yellow
Write-Host ""

$accessArg = if ($Private) { '' } else { '--allow-anonymous' }

try {
    Write-Host "============================================" -ForegroundColor Cyan
    Write-Host "  TUNNEL ACTIVE - Use the URL below" -ForegroundColor Cyan
    Write-Host "============================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "For Power Automate webhooks, use:" -ForegroundColor Yellow
    Write-Host "  <tunnel-url>/api/webhook/<orchestration-id>" -ForegroundColor White
    Write-Host ""
    Write-Host "Press Ctrl+C to stop the tunnel and server." -ForegroundColor Gray
    Write-Host ""
    
    if ($accessArg) {
        devtunnel host -p $Port $accessArg
    } else {
        devtunnel host -p $Port
    }
} finally {
    Write-Host ""
    Write-Host "Stopping server..." -ForegroundColor Yellow
    Stop-Job $serverJob -ErrorAction SilentlyContinue
    Remove-Job $serverJob -ErrorAction SilentlyContinue
    Write-Host "Server stopped. Goodbye!" -ForegroundColor Green
}
