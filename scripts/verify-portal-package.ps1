param(
  [Parameter(Mandatory)][string]$Version,
  [Parameter(Mandatory)][string]$Source,
  [string]$EvidenceDirectory = (Join-Path $PSScriptRoot '../artifacts/onboarding/package-verification')
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Source = [IO.Path]::GetFullPath($Source)
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$temporaryBase = if ($IsWindows -and (Test-Path "$env:LOCALAPPDATA/Temp/opencode")) { "$env:LOCALAPPDATA/Temp/opencode" } else { [IO.Path]::GetTempPath() }
$root = Join-Path $temporaryBase "orchestra-package-$([Guid]::NewGuid().ToString('N'))"
$data = Join-Path $root 'data'
$configRoot = Join-Path $root 'config'
$tools = Join-Path $root 'tools'
[IO.Directory]::CreateDirectory($root) | Out-Null
[IO.Directory]::CreateDirectory($evidence) | Out-Null
# A colocated empty config isolates the test from both ancestor configs and global sidecars.
# Creation of an absent configuration is covered by WorkspaceSettingsTests.
$configPath = Join-Path $configRoot 'Orchestra/orchestra.json'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($configPath)) | Out-Null
[IO.File]::WriteAllText($configPath, '{}')
$http = [Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(90)

function Start-IsolatedProcess([string]$executable, [string[]]$arguments) {
  $info = [Diagnostics.ProcessStartInfo]::new($executable)
  $info.WorkingDirectory = $root
  $info.UseShellExecute = $false
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  $info.CreateNoWindow = $true
  foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
  foreach ($key in @('ORCHESTRA_CONFIG_PATH', 'ORCHESTRA_ORCHESTRATIONS_PATH', 'ASPNETCORE_URLS', 'DOTNET_URLS', 'NUGET_API_KEY')) { $info.Environment.Remove($key) | Out-Null }
  $info.Environment['ORCHESTRA_PORTAL_DATA_PATH'] = $data
  $info.Environment['ORCHESTRA_CONFIG_PATH'] = $configPath
  $info.Environment['XDG_CONFIG_HOME'] = $configRoot
  $info.Environment['NUGET_PACKAGES'] = Join-Path $root 'nuget-cache'
  $info.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
  $info.Environment['NO_COLOR'] = '1'
  $process = [Diagnostics.Process]::new()
  $process.StartInfo = $info
  if (-not $process.Start()) { throw "Could not start $executable" }
  return @{ Process = $process; Stdout = $process.StandardOutput.ReadToEndAsync(); Stderr = $process.StandardError.ReadToEndAsync() }
}

function Stop-TestProcess($running, [string]$logName) {
  if ($null -eq $running) { return }
  try {
    if (-not $running.Process.HasExited) { $running.Process.Kill($true) }
    $running.Process.WaitForExit()
    $output = $running.Stdout.GetAwaiter().GetResult() + $running.Stderr.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $evidence "$logName.log"), $output.ReplaceLineEndings("`n"))
  } finally { $running.Process.Dispose() }
}

function Invoke-Checked([string]$executable, [string[]]$arguments, [string]$logName) {
  $running = Start-IsolatedProcess $executable $arguments
  try {
    if (-not $running.Process.WaitForExit(180000)) { throw "Timed out: $executable" }
    $output = $running.Stdout.GetAwaiter().GetResult() + $running.Stderr.GetAwaiter().GetResult()
    if ($running.Process.ExitCode -ne 0) { throw "$executable exited $($running.Process.ExitCode): $output" }
    return $output
  } finally { Stop-TestProcess $running $logName }
}

function Start-Portal([string]$tool, [string]$label) {
  $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
  $listener.Start()
  $port = $listener.LocalEndpoint.Port
  $listener.Stop()
  $url = "http://127.0.0.1:$port"
  $running = Start-IsolatedProcess $tool @('portal', '--urls', $url)
  $running.Url = $url
  $running.Label = $label
  try {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
      if ($running.Process.HasExited) { throw "Portal exited: $($running.Stderr.GetAwaiter().GetResult())" }
      try {
        $response = $http.GetAsync("$url/api/workspace").GetAwaiter().GetResult()
        try { if ($response.IsSuccessStatusCode) { return $running } } finally { $response.Dispose() }
      } catch [Net.Http.HttpRequestException] { }
      Start-Sleep -Milliseconds 150
    }
    throw "Portal did not become ready at $url"
  } catch { Stop-TestProcess $running $label; throw }
}

function Invoke-Portal($portal, [string]$path, $body = $null) {
  $request = [Net.Http.HttpRequestMessage]::new($(if ($null -eq $body) { [Net.Http.HttpMethod]::Get } else { [Net.Http.HttpMethod]::Post }), $portal.Url + $path)
  try {
    if ($null -ne $body) { $request.Content = [Net.Http.StringContent]::new(($body | ConvertTo-Json -Depth 20 -Compress), [Text.Encoding]::UTF8, 'application/json') }
    $response = $http.SendAsync($request).GetAwaiter().GetResult()
    try {
      $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
      if (-not $response.IsSuccessStatusCode) { throw "$path returned $($response.StatusCode): $content" }
      if (-not [string]::IsNullOrWhiteSpace($content)) { return $content | ConvertFrom-Json }
    } finally { $response.Dispose() }
  } finally { $request.Dispose() }
}

$portal = $null
try {
  Invoke-Checked 'dotnet' @('tool', 'install', 'Orchestra', '--version', $Version, '--tool-path', $tools, '--source', $Source, '--no-cache') 'install' | Out-Null
  $tool = Join-Path $tools $(if ($IsWindows) { 'orchestra.exe' } else { 'orchestra' })
  $reported = (Invoke-Checked $tool @('--version') 'version').Trim()
  $payload = Join-Path $tools ".store/orchestra/$Version/orchestra/$Version/tools/net10.0/any"
  foreach ($file in @('wwwroot/index.html', 'schemas/orchestration.schema.json', 'templates/smoke-test.yaml', 'skills/orchestration-authoring/SKILL.md', 'builtins/create-orchestration.json', 'builtins/configure-orchestra.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $file))) { throw "Package is missing $file" }
  }
  $hashes = foreach ($component in @('Cli', 'Engine', 'Host', 'Copilot', 'OpenCode', 'Portal', 'Watchers', 'ProcessHost', 'Client')) {
    $name = "Orchestra.$component.dll"
    $shippedHash = (Get-FileHash -LiteralPath (Join-Path $payload $name) -Algorithm SHA256).Hash
    $builtHash = (Get-FileHash -LiteralPath (Join-Path $repo "src/Orchestra.$component/bin/Release/net10.0/$name") -Algorithm SHA256).Hash
    if ($shippedHash -ne $builtHash) { throw "Package contains a different assembly: $name" }
    @{ assembly = $name; sha256 = $shippedHash.ToLowerInvariant() }
  }

  $portal = Start-Portal $tool 'first-start'
  $initial = Invoke-Portal $portal '/api/workspace'
  if (-not $initial.showWelcome) { throw 'A fresh profile did not show onboarding.' }
  if ($initial.workflowDirectory -ne (Join-Path $data 'workspace/orchestrations')) { throw "Unexpected workspace: $($initial.workflowDirectory)" }
  $html = $http.GetStringAsync($portal.Url).GetAwaiter().GetResult()
  if (-not $html.Contains('assets/index.js')) { throw 'Portal HTML is missing its bundled script.' }
  $bundle = $http.GetStringAsync($portal.Url + '/assets/index.js').GetAwaiter().GetResult()
  if (-not $bundle.Contains('WorkspaceModal')) { throw 'The served Portal bundle predates onboarding.' }
  $demo = Invoke-Portal $portal '/api/workspace/templates/smoke-test' @{}
  $run = $http.GetStringAsync($portal.Url + "/api/orchestrations/$($demo.id)/run").GetAwaiter().GetResult()
  if (-not $run.Contains('Orchestra is working.')) { throw 'The packaged demo did not complete successfully.' }
  $created = Invoke-Portal $portal '/api/workspace/orchestrations' @{ name = 'package-created'; description = 'Package smoke check'; steps = @(@{ name = 'answer'; type = 'Transform'; template = 'package verified' }) }
  if (-not (Test-Path -LiteralPath $created.path)) { throw 'The created workflow was not saved durably.' }
  $assistant = Invoke-Portal $portal '/api/workspace/assistants/create-orchestration' @{}
  $settings = Invoke-Portal $portal '/api/workspace/settings'
  $saved = Invoke-Portal $portal '/api/workspace/settings' @{ revision = $settings.revision; changes = @{ defaultMaxConcurrency = 2 } }
  if (-not (Test-Path -LiteralPath $saved.configPath)) { throw 'Settings did not save the discovered configuration.' }
  if (-not $saved.configPath.StartsWith($configRoot)) { throw 'Settings escaped the isolated XDG configuration location.' }
  Invoke-Portal $portal '/api/workspace/dismiss' @{} | Out-Null
  Stop-TestProcess $portal 'first-start'
  $portal = $null

  $portal = Start-Portal $tool 'restart'
  $restored = Invoke-Portal $portal '/api/workspace/settings'
  if ($restored.effective.defaultMaxConcurrency -ne 2) { throw 'Settings did not survive restart.' }
  $workflow = Invoke-Portal $portal "/api/orchestrations/$($created.id)"
  if ($workflow.name -ne 'package-created') { throw 'Created workflow did not survive restart.' }
  $refreshed = Invoke-Portal $portal "/api/orchestrations/$($assistant.id)"
  if (-not ($refreshed.steps | Where-Object name -eq 'save').url.StartsWith($portal.Url)) { throw 'The assistant retained the old listener after restart.' }
  if ((Invoke-Portal $portal '/api/workspace').showWelcome) { throw 'The dismissed welcome screen reappeared after restart.' }
  $runs = @(Get-ChildItem -LiteralPath (Join-Path $data 'executions/smoke-test') -Filter 'run.json' -Recurse)
  if ($runs.Count -ne 1 -or (Get-Content -Raw -LiteralPath $runs[0].FullName | ConvertFrom-Json).status -ne 'Succeeded') { throw 'Demo history was not retained.' }
  $result = @{ packageVersion = $Version; assemblyVersion = $reported; assemblies = $hashes; freshProfile = $true; demo = $true; bundledAssets = $true; durableWorkflow = $true; settingsRestart = $true; assistantRefresh = $true; history = $true }
  $json = $result | ConvertTo-Json -Depth 8
  [IO.File]::WriteAllText((Join-Path $evidence 'verification.json'), $json.ReplaceLineEndings("`n") + "`n")
  $json
} finally {
  Stop-TestProcess $portal 'restart'
  $http.Dispose()
  if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
