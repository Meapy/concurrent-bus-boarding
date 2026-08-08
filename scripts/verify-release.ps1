# Verifies a staged Release package before it is deployed or published.
#
# Checks the artifacts the game actually loads are present and non-empty, then records SHA-256
# hashes so the exact verified package can be matched against what gets published.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/verify-release.ps1 `
#     -ReleaseFolder '.\artifacts\release-1.5.0\ConcurrentBusBoarding' `
#     -AssemblyName 'ConcurrentBusBoarding' -RequireUI

param(
    [Parameter(Mandatory = $true)][string]$ReleaseFolder,
    [Parameter(Mandatory = $true)][string]$AssemblyName,
    [switch]$RequireUI
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ReleaseFolder)) {
    throw "Release folder not found: $ReleaseFolder. Run the Release build first."
}
$ReleaseFolder = (Resolve-Path $ReleaseFolder).Path

$required = @("$AssemblyName.dll")
if ($RequireUI) {
    # The .mjs is the entire frontend. The game registers only a UI module's .mjs as a UI
    # mod location, so a .css shipped beside it is never loaded; the stylesheet lives
    # inside the bundle and is injected at runtime.
    $required += "$AssemblyName.mjs"
}

$missing = @()
foreach ($name in $required) {
    $path = Join-Path $ReleaseFolder $name
    if (-not (Test-Path $path)) {
        $missing += $name
        continue
    }
    if ((Get-Item $path).Length -le 0) {
        $missing += "$name (empty)"
    }
}
if ($missing.Count -gt 0) {
    throw "Staged package is incomplete: $($missing -join ', ')"
}

$files = Get-ChildItem -Recurse -File $ReleaseFolder | Sort-Object FullName
Write-Host "Package: $ReleaseFolder"
Write-Host "Files:   $($files.Count)"
Write-Host ''

foreach ($file in $files) {
    $hash = (Get-FileHash -Algorithm SHA256 $file.FullName).Hash
    $relative = $file.FullName.Substring($ReleaseFolder.Length).TrimStart('\')
    Write-Host ("{0}  {1,10:N0}  {2}" -f $hash, $file.Length, $relative)
}

# A managed mod that ships no UI bundle still loads, so surface it rather than failing silently.
if (-not $RequireUI) {
    $ui = Get-ChildItem -File $ReleaseFolder -Filter '*.mjs' -ErrorAction SilentlyContinue
    if (-not $ui) {
        Write-Warning 'No .mjs present. Pass -RequireUI if this package is expected to ship UI.'
    }
}

Write-Host ''
Write-Host 'Verification passed. Publish only this exact folder.'
