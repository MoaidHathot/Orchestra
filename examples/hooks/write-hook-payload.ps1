param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$payload = [Console]::In.ReadToEnd()
$fullPath = [System.IO.Path]::GetFullPath($OutputPath)
$directory = Split-Path -Parent $fullPath

if (-not [string]::IsNullOrWhiteSpace($directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$payload | Set-Content -Path $fullPath
Write-Output "Hook payload written to $fullPath"
