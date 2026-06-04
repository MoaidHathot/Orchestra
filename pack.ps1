param(
    [string]$ApiKey,
    [switch]$Push
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$packageOutput = Join-Path $artifactsRoot 'packages'
$solutionPath = Join-Path $repoRoot 'OrchestrationEngine.slnx'

function Invoke-Step {
    param(
        [string]$Description,
        [string]$Command,
        [string]$WorkingDirectory = $repoRoot
    )

    Write-Host "> $Description" -ForegroundColor Cyan
    & pwsh -NoLogo -NoProfile -Command $Command | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if (Test-Path $artifactsRoot) {
    Remove-Item $artifactsRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $packageOutput | Out-Null

$portalProject = Join-Path $repoRoot 'playground/Hosting/Orchestra.Playground.Copilot.Portal/Orchestra.Playground.Copilot.Portal.csproj'
$toolProject = Join-Path $repoRoot 'src/Orchestra.Tool/Orchestra.Tool.csproj'

# Pack is a build+publish step, not a verification step. Tests live in CI (or a
# dedicated test.ps1 if you want a local pre-flight). Running them here is
# expensive and turns every `pack.ps1` into a 3-minute wait, even when nothing
# under test has changed.
Invoke-Step -Description 'Restore solution' -Command "dotnet restore `"$solutionPath`""
Invoke-Step -Description 'Build portal assets and solution' -Command "dotnet build `"$solutionPath`" --configuration Release --no-restore"
Invoke-Step -Description 'Verify portal publish output' -Command "dotnet publish `"$portalProject`" --configuration Release --no-build -o `"$(Join-Path $artifactsRoot 'portal-publish')`""
Invoke-Step -Description 'Pack Orchestra tool' -Command "dotnet pack `"$toolProject`" --configuration Release --no-build -o `"$packageOutput`""

if ($Push) {
    $resolvedApiKey = if ($ApiKey) { $ApiKey } else { $env:NUGET_API_KEY }
    if ([string]::IsNullOrWhiteSpace($resolvedApiKey)) {
        throw 'NuGet API key not provided. Use -ApiKey or set NUGET_API_KEY.'
    }

    $packages = Get-ChildItem -Path $packageOutput -Filter '*.nupkg' | Where-Object { $_.Name -notlike '*.snupkg' }
    if (-not $packages) {
        throw 'No packages were produced to push.'
    }

    foreach ($package in $packages) {
        Invoke-Step -Description "Push $($package.Name)" -Command "dotnet nuget push `"$($package.FullName)`" --api-key `"$resolvedApiKey`" --source https://api.nuget.org/v3/index.json --skip-duplicate"
    }
}
