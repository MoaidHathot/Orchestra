$ErrorActionPreference = 'Stop'
# Orchestra watcher fetcher (protocol v1): lists *.txt files in config.folder.
# stdin:  { protocolVersion, watcher, cursor, subscribers, config }
# stdout: { protocolVersion, items, cursor }
# Use [Console]::In.ReadToEnd() (not $input) so non-ASCII payloads survive on Windows.
$stdin = [Console]::In.ReadToEnd()
$req = $stdin | ConvertFrom-Json

$folder = if ($req.config -and $req.config.folder) { $req.config.folder } else { './inbox' }
if (-not (Test-Path -LiteralPath $folder)) {
    [Console]::Out.Write((@{ protocolVersion = '1'; items = @(); cursor = $req.cursor } | ConvertTo-Json -Depth 10 -Compress))
    exit 0
}

$items = @()
Get-ChildItem -LiteralPath $folder -File -Filter '*.txt' | Sort-Object Name | ForEach-Object {
    $items += @{ path = $_.FullName; name = $_.Name; size = $_.Length }
}

# The cursor is echoed back unchanged: this source has no stream position, dedup does the work.
$response = @{ protocolVersion = '1'; items = $items; cursor = $req.cursor }
[Console]::Out.Write(($response | ConvertTo-Json -Depth 10 -Compress))
exit 0
