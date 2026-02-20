<#
.SYNOPSIS
    Runs the Orchestra Portal server with a dev tunnel for external access.

.DESCRIPTION
    This script starts the Orchestra Portal server and creates a dev tunnel to expose
    it to the internet. This is useful for:
    - Testing webhooks from Power Automate, Azure Logic Apps, etc.
    - Sharing the portal temporarily with others
    - Testing on mobile devices

.PARAMETER Port
    The local port to run the server on. Default: 5100

.PARAMETER TunnelAccess
    Who can access the tunnel: 'anonymous' (public), 'org', or 'private'. 
    Use 'anonymous' for Power Automate webhooks. Default: anonymous

.PARAMETER Persistent
    Create a persistent tunnel with a stable URL that survives restarts.

.PARAMETER TunnelName
    Name for a persistent tunnel. Only used with -Persistent.

.EXAMPLE
    .\run-portal-tunnel.ps1
    # Runs with default settings (port 5100, anonymous access)

.EXAMPLE
    .\run-portal-tunnel.ps1 -Port 5200 -TunnelAccess private
    # Runs on port 5200 with private access (requires login to access)

.EXAMPLE
    .\run-portal-tunnel.ps1 -Persistent -TunnelName "orchestra-portal"
    # Creates a persistent tunnel with a stable URL

.NOTES
    Prerequisites:
    1. Install the dev tunnel CLI:
       winget install Microsoft.devtunnel
    
    2. Login to dev tunnels (one-time):
       devtunnel user login
#>

param(
    [int]$Port = 5100,
    [ValidateSet('anonymous', 'org', 'private')]
    [string]$TunnelAccess = 'anonymous',
    [switch]$Persistent,
    [string]$TunnelName = 'orchestra-portal'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir = Join-Path $scriptDir '..\playground\Hosting\Orchestra.Playground.Copilot.Portal'
$projectPath = Join-Path $projectDir 'Orchestra.Playground.Copilot.Portal.csproj'

# Check if devtunnel is installed
$devtunnelPath = Get-Command 'devtunnel' -ErrorAction SilentlyContinue
if (-not $devtunnelPath) {
    Write-Host "ERROR: devtunnel CLI is not installed." -ForegroundColor Red
    Write-Host ""
    Write-Host "Install it using one of these methods:" -ForegroundColor Yellow
    Write-Host "  winget install Microsoft.devtunnel" -ForegroundColor Cyan
    Write-Host "  # OR download from: https://aka.ms/TunnelsCliDownload/win-x64" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "After installing, login with:" -ForegroundColor Yellow
    Write-Host "  devtunnel user login" -ForegroundColor Cyan
    exit 1
}

# Check if user is logged in
$loginStatus = devtunnel user show 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Not logged in to dev tunnels." -ForegroundColor Red
    Write-Host ""
    Write-Host "Please login first:" -ForegroundColor Yellow
    Write-Host "  devtunnel user login" -ForegroundColor Cyan
    exit 1
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Orchestra Portal with Dev Tunnel" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Local Port:     $Port" -ForegroundColor White
Write-Host "Tunnel Access:  $TunnelAccess" -ForegroundColor White
if ($Persistent) {
    Write-Host "Tunnel Type:    Persistent ($TunnelName)" -ForegroundColor White
} else {
    Write-Host "Tunnel Type:    Temporary" -ForegroundColor White
}
Write-Host ""

# Build access argument
$accessArg = switch ($TunnelAccess) {
    'anonymous' { '--allow-anonymous' }
    'org' { '--organization' }
    'private' { '' }
}

# Create or use persistent tunnel
$tunnelId = $null
if ($Persistent) {
    # Check if tunnel already exists
    $existingTunnels = devtunnel list --output json 2>$null | ConvertFrom-Json -ErrorAction SilentlyContinue
    $existing = $existingTunnels | Where-Object { $_.tunnelId -eq $TunnelName }
    
    if ($existing) {
        Write-Host "Using existing persistent tunnel: $TunnelName" -ForegroundColor Green
        $tunnelId = $TunnelName
    } else {
        Write-Host "Creating persistent tunnel: $TunnelName" -ForegroundColor Yellow
        $createOutput = devtunnel create $TunnelName $accessArg 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Failed to create tunnel: $createOutput" -ForegroundColor Red
            exit 1
        }
        $tunnelId = $TunnelName
        
        # Add port to the tunnel
        devtunnel port create $tunnelId -p $Port --protocol https 2>&1 | Out-Null
    }
}

# Start the portal server in background
Write-Host "Starting Orchestra Portal server..." -ForegroundColor Yellow
$serverJob = Start-Job -ScriptBlock {
    param($projectPath, $port)
    Set-Location (Split-Path -Parent $projectPath)
    dotnet run --project $projectPath --urls "http://localhost:$port"
} -ArgumentList $projectPath, $Port

# Wait a moment for the server to start
Write-Host "Waiting for server to start..." -ForegroundColor Gray
Start-Sleep -Seconds 3

# Check if server started
if ($serverJob.State -eq 'Failed') {
    Write-Host "ERROR: Server failed to start" -ForegroundColor Red
    Receive-Job $serverJob
    exit 1
}

# Start the tunnel
Write-Host ""
Write-Host "Starting dev tunnel..." -ForegroundColor Yellow
Write-Host ""

try {
    if ($Persistent) {
        # Host the existing persistent tunnel
        devtunnel host $tunnelId -p $Port $accessArg
    } else {
        # Create and host a temporary tunnel
        if ($accessArg) {
            devtunnel host -p $Port $accessArg
        } else {
            devtunnel host -p $Port
        }
    }
} finally {
    # Cleanup: Stop the server when tunnel is closed
    Write-Host ""
    Write-Host "Stopping server..." -ForegroundColor Yellow
    Stop-Job $serverJob -ErrorAction SilentlyContinue
    Remove-Job $serverJob -ErrorAction SilentlyContinue
    Write-Host "Done." -ForegroundColor Green
}
