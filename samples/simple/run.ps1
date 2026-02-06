# Simple Sample - Story Generator
# A basic 3-step pipeline that generates a sci-fi story premise, outline, and opening paragraph.

param(
    [string]$ConsolePath = "$PSScriptRoot\..\..\src\OrchestrationEngine.Console"
)

$ErrorActionPreference = "Stop"

# Build the console app if needed
Write-Host "Building OrchestrationEngine.Console..." -ForegroundColor Cyan
dotnet build $ConsolePath -c Release -v quiet

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed"
    exit 1
}

# Run the orchestration
$exe = Join-Path $ConsolePath "bin\Release\net10.0\OrchestrationEngine.Console.exe"
$orchestration = Join-Path $PSScriptRoot "orchestration.json"
$mcp = Join-Path $PSScriptRoot "mcp.json"

Write-Host "Running Story Generator orchestration..." -ForegroundColor Green
& $exe -o $orchestration -m $mcp
