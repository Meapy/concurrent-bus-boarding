# Dumps the installed game's boarding IL for analysis.
#
# Output goes to artifacts/il, which is gitignored. Never commit these files or the assemblies
# they came from.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/dump-boarding-il.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/dump-boarding-il.ps1 -ManagedPath 'D:\...\Cities2_Data\Managed'

param(
    [string]$ManagedPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scanner = Join-Path $root '.agent\inspector\CecilScan.exe'
if (-not (Test-Path $scanner)) {
    throw "CecilScan.exe was not found at $scanner. Build .agent/inspector first."
}

$tried = New-Object System.Collections.Generic.List[string]

function Test-ManagedPath([string]$path) {
    if (-not $path) { return $null }
    $script:tried.Add($path) | Out-Null
    if (Test-Path (Join-Path $path 'Game.dll')) {
        return (Resolve-Path $path).Path
    }
    return $null
}

if (-not $ManagedPath) {
    $toolPath = $env:CSII_TOOLPATH
    if (-not $toolPath) {
        $toolPath = [System.Environment]::GetEnvironmentVariable('CSII_TOOLPATH', 'User')
    }
    if (-not $toolPath) {
        $toolPath = [System.Environment]::GetEnvironmentVariable('CSII_TOOLPATH', 'Machine')
    }
    if ($toolPath) {
        Write-Host "CSII_TOOLPATH = $toolPath"
        # .ModdingToolchain sits under Cities2_Data/Content/Game; Managed is a sibling of Content.
        $ManagedPath = Test-ManagedPath (Join-Path $toolPath.TrimEnd('\') '..\..\..\Managed')
    }
    else {
        Write-Host 'CSII_TOOLPATH is not set in this shell.'
    }
}

if (-not $ManagedPath) {
    # Steam can place the library on any drive, so probe each fixed drive's default layout.
    $suffix = 'SteamLibrary\steamapps\common\Cities Skylines II\Cities2_Data\Managed'
    foreach ($drive in [System.IO.DriveInfo]::GetDrives()) {
        if (-not $drive.IsReady) { continue }
        $ManagedPath = Test-ManagedPath (Join-Path $drive.RootDirectory.FullName $suffix)
        if ($ManagedPath) { break }
        $ManagedPath = Test-ManagedPath (Join-Path $drive.RootDirectory.FullName `
            'Program Files (x86)\Steam\steamapps\common\Cities Skylines II\Cities2_Data\Managed')
        if ($ManagedPath) { break }
    }
}

if (-not $ManagedPath) {
    Write-Host ''
    Write-Host 'Paths tried:'
    foreach ($path in $tried) { Write-Host "  $path" }
    throw 'Could not locate Cities2_Data\Managed. Pass it with -ManagedPath.'
}
Write-Host "Managed path: $ManagedPath"

$game = Join-Path $ManagedPath 'Game.dll'
if (-not (Test-Path $game)) {
    throw "Game.dll was not found in $ManagedPath."
}
Write-Host ("Game.dll {0:N1} MB, modified {1}" -f `
    ((Get-Item $game).Length / 1MB), (Get-Item $game).LastWriteTime)

$output = Join-Path $root 'artifacts\il'
New-Item -ItemType Directory -Force $output | Out-Null

# Each entry is a label plus the CecilScan arguments. A three-argument call dumps every
# instruction of each method whose full name matches; 'type:' dumps a type's shape instead.
$targets = @(
    @{ Name = 'TransportCarAISystem.type'; Args = @('type:TransportCarAISystem') },
    @{ Name = 'StopBoarding';              Args = @('-', 'StopBoarding') },
    @{ Name = 'StartBoarding';             Args = @('-', 'StartBoarding') },
    @{ Name = 'BoardingVehicle.type';      Args = @('type:BoardingVehicle') },
    @{ Name = 'PublicTransport.type';      Args = @('type:Game.Vehicles.PublicTransport') },
    @{ Name = 'writers.BoardingVehicle';   Args = @('BoardingVehicle') },
    @{ Name = 'writers.WaitingPassengers'; Args = @('WaitingPassengers') },
    # The mod must pair every native BeginBoarding with an EndBoarding. These reveal the queue's
    # shape and which system owns it, so the pairing can be issued without guessing the API.
    @{ Name = 'TransportBoardingHelpers.type'; Args = @('type:TransportBoardingHelpers') },
    # TransportUsageTrackSystem consumes the queue's usage events, so it is the likeliest owner of
    # the per-line usage figure that is collapsing.
    @{ Name = 'TransportUsageTrackSystem.type'; Args = @('type:TransportUsageTrackSystem') },
    @{ Name = 'TransportBoardingJob.BeginBoarding'; Args = @('-', 'TransportBoardingJob::BeginBoarding') },
    @{ Name = 'BoardingData.EndBoarding';  Args = @('-', 'BoardingData/Concurrent::EndBoarding') },
    @{ Name = 'TransportBoardingJob.EndBoarding'; Args = @('-', 'TransportBoardingJob::EndBoarding') },
    @{ Name = 'writers.BoardingData';      Args = @('BoardingData') },
    # For a bus-only attractiveness slider: confirm TransportLineData carries a transport type,
    # and that bus lines do not share a m_PathfindPrefab with other passenger modes.
    @{ Name = 'TransportLineData.type';    Args = @('type:Game.Prefabs.TransportLineData') },
    @{ Name = 'PathfindTransportData.type'; Args = @('type:PathfindTransportData') },
    @{ Name = 'writers.PathfindPrefab';    Args = @('m_PathfindPrefab') }
)

foreach ($target in $targets) {
    $file = Join-Path $output ("{0}.txt" -f $target.Name)
    Write-Host "Dumping $($target.Name) -> $file"
    $arguments = @($game) + $target.Args
    # A pattern with no matches produces no output, so write explicitly rather than piping,
    # otherwise the file is never created and the next read fails the whole run.
    $result = & $scanner @arguments 2>&1
    if ($null -eq $result) { $result = @() }
    Set-Content -Encoding UTF8 -Path $file -Value $result
    $lines = @($result).Count
    if ($lines -eq 0) {
        Write-Warning "  no matches for $($target.Args -join ' ')"
    }
    else {
        Write-Host "  $lines lines"
    }
}

Write-Host ''
Write-Host "Done. Dumps are in $output (gitignored)."
