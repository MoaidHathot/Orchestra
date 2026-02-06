# Stock Analyzer - Data Pipeline Sample
# This sample demonstrates:
# - MCP tool usage: Uses the fetch MCP server to gather live web data
# - handleOutput: Displays the final report to the user
# - 4-step pipeline: Gather Market Data -> Analyze Movement -> Forecast Outlook -> Generate Report

$ErrorActionPreference = "Stop"

# Get the script directory
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
$consoleProject = Join-Path $repoRoot "src\OrchestrationEngine.Console\OrchestrationEngine.Console.csproj"

# Build the console app
Write-Host "Building OrchestrationEngine.Console..." -ForegroundColor Cyan
dotnet build $consoleProject -c Release --nologo -v q

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Run the orchestration
Write-Host "`nRunning Stock Analyzer pipeline..." -ForegroundColor Green
Write-Host "This pipeline will fetch live market data for MSFT and generate an analysis report.`n" -ForegroundColor Gray

$orchestrationFile = Join-Path $scriptDir "orchestration.json"
$mcpFile = Join-Path $scriptDir "mcp.json"

dotnet run --project $consoleProject -c Release --no-build -- -o $orchestrationFile -m $mcpFile
