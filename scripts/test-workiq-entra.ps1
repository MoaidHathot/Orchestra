<# Opt-in live integration. -Connect is the only mode that may open a sign-in window.
   Uses a temporary workspace with only the hosted Work IQ backend; no triggers or external proxy. #>
param(
    [Parameter(Mandatory)][string]$Cli,
    [Parameter(Mandatory)][guid]$TenantId,
    [Parameter(Mandatory)][guid]$ClientId,
    [string]$Model = 'claude-opus-4.8',
    [switch]$Connect
)
$ErrorActionPreference = 'Stop'
$Cli = (Resolve-Path $Cli).Path
$root = [IO.Directory]::CreateTempSubdirectory('orchestra-workiq-proof-').FullName
$previous = $env:ORCHESTRA_CONFIG_PATH
try {
    $env:ORCHESTRA_CONFIG_PATH = Join-Path $root 'orchestra.json'
    $scan = New-Item -ItemType Directory -Path (Join-Path $root 'workspace')
    @{ dataPath = (Join-Path $root 'data') } | ConvertTo-Json | Set-Content $env:ORCHESTRA_CONFIG_PATH
    $server = @{name='workiq';type='remote';endpoint='https://workiq.svc.cloud.microsoft/mcp';allowedTools=@('ask','retrieve');auth=@{
        type='entra';clientId=$ClientId.ToString();tenantId=$TenantId.ToString();redirectUri='http://localhost:12798/';
        scopes=@('fdcc1f02-fc51-4226-8753-f668596af7f7/WorkIQAgent.Ask')}}
    @{mcps=@($server)} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'orchestra.mcp.json')
    if ($Connect) {
        & dotnet $Cli mcp connect workiq
        if ($LASTEXITCODE) { throw 'Explicit Work IQ sign-in failed.' }
    }
    $discovery = & dotnet $Cli mcp test workiq
    if ($LASTEXITCODE -or ($discovery -join "`n") -notmatch 'workiq: 2 tools: ask, retrieve') {
        throw "Fresh-process discovery failed: $($discovery -join ' ')"
    }
    'Fresh process: silent discovery returned exactly ask and retrieve.'
    $workflow = Join-Path $root 'proof.json'
    @{name='workiq-entra-proof';description='Synthetic read-only MCP connectivity check';defaultModel=$Model;steps=@(@{
        name='probe';type='Prompt';mcps=@('workiq');systemPrompt='Perform one connectivity check. Never include search results or source names in your response.';
        userPrompt='Call the Work IQ retrieve tool exactly once to find the literal OrchestraWorkIQConnectivityCheck-9bc052a2. Reply only OK after it returns, including for no matches.'
    })} | ConvertTo-Json -Depth 5 | Set-Content $workflow
    $report = Join-Path $root 'report.json'
    $null = & dotnet $Cli run --mode isolated --no-interactive --run-file $workflow --orchestrations-path $scan.FullName --data-path (Join-Path $root 'data') --run-timeout 150 --report json --report-output $report --quiet
    if ($LASTEXITCODE -or !(Test-Path $report)) { throw "Isolated Orchestra run did not produce a successful report (exit $LASTEXITCODE)." }
    $run = Get-Content -Raw $report | ConvertFrom-Json
    $calls = @($run.steps[0].trace.toolCalls | Where-Object { $_.mcpServer -eq 'workiq' -and $_.toolName -eq 'retrieve' -and $_.success })
    if ($run.status -ne 'Succeeded' -or $calls.Count -ne 1) { throw 'The run did not record one successful Work IQ retrieval.' }
    'Fresh host: run succeeded; workiq/retrieve succeeded. No search contents retained.'
} finally {
    $env:ORCHESTRA_CONFIG_PATH = $previous
    Remove-Item -Recurse -Force -LiteralPath $root
}
