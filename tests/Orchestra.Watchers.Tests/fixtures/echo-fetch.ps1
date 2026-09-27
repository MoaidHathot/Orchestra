$ErrorActionPreference = 'Stop'
# Test fixture: echoes the request back as items so tests can assert on what Orchestra sent.
$stdin = [Console]::In.ReadToEnd()
$req = $stdin | ConvertFrom-Json

$items = @(
    @{ id = 'a'; watcher = $req.watcher; config = $req.config; subscribers = @($req.subscribers) },
    @{ id = 'b'; note = 'second item' }
)

$response = @{
    protocolVersion = '1'
    items = $items
    cursor = @{ lastEventId = 42; previous = $req.cursor }
    diagnostics = @{ message = 'fixture ok' }
}

[Console]::Out.Write(($response | ConvertTo-Json -Depth 10 -Compress))
exit 0
