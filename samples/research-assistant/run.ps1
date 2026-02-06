# Codebase Research Assistant Sample
# This sample demonstrates:
# - handleInput: Allows user to describe their research question
# - handleOutput: Displays the final research summary
# - Placeholders: Uses {{workingDirectory}} to configure filesystem access
# - MCP Tools: Uses filesystem MCP server to read and explore code
# - 5-step pipeline: Understand -> Explore -> Analyze -> Find -> Summarize

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
Write-Host "`nRunning Codebase Research Assistant pipeline..." -ForegroundColor Green
Write-Host "This pipeline will help you research and understand a codebase." -ForegroundColor Gray
Write-Host "The filesystem MCP server will be configured to access the current directory.`n" -ForegroundColor Gray

$orchestrationFile = Join-Path $scriptDir "orchestration.json"
$mcpFile = Join-Path $scriptDir "mcp.json"

dotnet run --project $consoleProject -c Release --no-build -- -o $orchestrationFile -m $mcpFile
