$ErrorActionPreference = 'Stop'
# Test fixture: lists *.txt files in the folder named by config.folder, mirroring the shipped example.
$stdin = [Console]::In.ReadToEnd()
$req = $stdin | ConvertFrom-Json

$folder = $req.config.folder
$items = @()
if (Test-Path -LiteralPath $folder) {
    Get-ChildItem -LiteralPath $folder -File -Filter '*.txt' | Sort-Object Name | ForEach-Object {
        $items += @{ path = $_.FullName; name = $_.Name; size = $_.Length }
    }
}

$response = @{ protocolVersion = '1'; items = $items; cursor = $req.cursor }
[Console]::Out.Write(($response | ConvertTo-Json -Depth 10 -Compress))
exit 0
