param(
    [string]$ApiKey,
    [switch]$Push,

    # Primary push target. The v3 service index is the documented endpoint, but it is
    # unreachable from some corporate networks (TLS interception drops api.nuget.org while
    # www.nuget.org still resolves), so -Push falls back to the v2 endpoint below rather
    # than failing the release.
    [string]$Source = 'https://api.nuget.org/v3/index.json',
    [string]$FallbackSource = 'https://www.nuget.org/api/v2/package',

    # Additional feed to publish to, e.g. a folder-based local feed. Pushed before
    # nuget.org so a network failure upstream still leaves the package usable locally.
    [string]$LocalFeed,

    # Bundle the build host's Copilot CLI runtime into the nupkg (~80 MB compressed). Off by
    # default: the shipped tool is small and fetches the CLI lazily on first run
    # (Orchestra.Copilot.CopilotCliBootstrap), exactly like the CI release workflow, which
    # exports CopilotSkipCliDownload=true. Without this switch a cached runtime bundle under
    # src/Orchestra.Copilot/obj would otherwise flow into the package through the SDK's
    # transitive copy targets and triple its size.
    [switch]$BundleHostCopilotCli
)

$ErrorActionPreference = 'Stop'

if (-not $BundleHostCopilotCli) {
    $env:CopilotSkipCliDownload = 'true'
}

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

$portalProject = Join-Path $repoRoot 'src/Orchestra.Portal/Orchestra.Portal.csproj'
$toolProject = Join-Path $repoRoot 'src/Orchestra.Cli/Orchestra.Cli.csproj'

# Pack is a build+publish step, not a verification step. Tests live in CI (or a
# dedicated test.ps1 if you want a local pre-flight). Running them here is
# expensive and turns every `pack.ps1` into a 3-minute wait, even when nothing
# under test has changed.
Invoke-Step -Description 'Restore solution' -Command "dotnet restore `"$solutionPath`""
Invoke-Step -Description 'Build portal assets and solution' -Command "dotnet build `"$solutionPath`" --configuration Release --no-restore"
Invoke-Step -Description 'Verify portal publish output' -Command "dotnet publish `"$portalProject`" --configuration Release --no-build -o `"$(Join-Path $artifactsRoot 'portal-publish')`""
Invoke-Step -Description 'Pack Orchestra tool' -Command "dotnet pack `"$toolProject`" --configuration Release --no-build -o `"$packageOutput`""

# Guard the "ship small" contract: a runtime bundle sneaking in is only visible as a size jump
# on nuget.org after the (immutable) push, so fail here instead.
if (-not $BundleHostCopilotCli) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($package in Get-ChildItem -Path $packageOutput -Filter '*.nupkg' | Where-Object { $_.Name -notlike '*.snupkg' }) {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $bundled = @($zip.Entries | Where-Object { $_.FullName -match 'copilot_runtime|copilot-runtime|libcopilot_runtime' })
        }
        finally {
            $zip.Dispose()
        }

        if ($bundled.Count -gt 0) {
            throw "$($package.Name) contains the Copilot CLI runtime ($($bundled.Count) files). Pass -BundleHostCopilotCli to ship it on purpose, otherwise clear src/Orchestra.Copilot/obj and repack."
        }

        Write-Host ("  {0}: {1:N1} MB, no bundled Copilot runtime" -f $package.Name, ($package.Length / 1MB)) -ForegroundColor DarkGray
    }
}

if ($Push) {
    $packages = Get-ChildItem -Path $packageOutput -Filter '*.nupkg' | Where-Object { $_.Name -notlike '*.snupkg' }
    if (-not $packages) {
        throw 'No packages were produced to push.'
    }

    # Local feed first: it needs no credentials and no network, so the package is available
    # locally even when the upstream push fails.
    if ($LocalFeed) {
        foreach ($package in $packages) {
            Invoke-Step -Description "Push $($package.Name) to $LocalFeed" -Command "dotnet nuget push `"$($package.FullName)`" --source `"$LocalFeed`" --skip-duplicate"
        }
    }

    $resolvedApiKey = if ($ApiKey) { $ApiKey } else { $env:NUGET_API_KEY }
    if ([string]::IsNullOrWhiteSpace($resolvedApiKey)) {
        throw 'NuGet API key not provided. Use -ApiKey or set NUGET_API_KEY.'
    }

    foreach ($package in $packages) {
        Write-Host "> Push $($package.Name) to $Source" -ForegroundColor Cyan
        & pwsh -NoLogo -NoProfile -Command "dotnet nuget push `"$($package.FullName)`" --api-key `"$resolvedApiKey`" --source $Source --skip-duplicate" | Out-Host

        if ($LASTEXITCODE -ne 0) {
            if ([string]::IsNullOrWhiteSpace($FallbackSource)) {
                throw "Push of $($package.Name) failed with exit code $LASTEXITCODE."
            }

            Write-Host "  $Source failed; retrying against $FallbackSource" -ForegroundColor Yellow
            Invoke-Step -Description "Push $($package.Name) to $FallbackSource" -Command "dotnet nuget push `"$($package.FullName)`" --api-key `"$resolvedApiKey`" --source $FallbackSource --skip-duplicate"
        }
    }
}
