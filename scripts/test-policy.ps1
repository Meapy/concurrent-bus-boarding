$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\checks'
New-Item -ItemType Directory -Force $output | Out-Null
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}
& $compiler /nologo /out:"$output\BoardingPolicyCheck.exe" `
    "$root\ConcurrentBusBoarding\BoardingPolicy.cs" `
    "$root\tests\BoardingPolicyCheck.cs"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& "$output\BoardingPolicyCheck.exe"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$boardingSystems = Get-Content -Raw "$root\ConcurrentBusBoarding\BoardingSystems.cs"
$project = Get-Content -Raw "$root\ConcurrentBusBoarding\ConcurrentBusBoarding.csproj"
$breadcrumbs = Get-Content -Raw "$root\ConcurrentBusBoarding\CrashBreadcrumbs.cs"
if ($boardingSystems -notmatch 'm_DepartureFrame = math\.max' -or
    $boardingSystems -notmatch 'm_MaxBoardingDistance = 0f' -or
    $boardingSystems -notmatch 'm_MinWaitingDistance = float\.MaxValue') {
    throw 'Stopped buses must retain the proven 1.0.0 boarding dwell handshake.'
}
if ($boardingSystems -match '\.BeginBoarding\(') {
    throw 'The native boarding queue must not admit secondary buses before passenger exchange.'
}
if ($boardingSystems -notmatch 'if \(!foundCurrentLane\)[\s\S]*?if \(element\.m_Target == zone\.Lane\)[\s\S]*?continue;') {
    throw 'Rear-zone pieces must not start before the physical lane is found in the route path.'
}
if ($boardingSystems -notmatch 'ConsiderLane\(entityManager, routeLane\.m_EndLane[\s\S]*?if \(lane == Entity\.Null\)[\s\S]*?ConsiderLane\(entityManager, routeLane\.m_StartLane') {
    throw 'The stop-side route end lane must remain authoritative over the approach lane.'
}
if ($boardingSystems -notmatch 'transport\.m_State \|= PublicTransportFlags\.EnRoute \| PublicTransportFlags\.Boarding;[\s\S]*?Add\(boarding, stop, bus\);') {
    throw 'Passenger distribution must expose every active bus for concurrent boarding.'
}
if ($boardingSystems -notmatch 'internal Entity Stop;' -or
    $boardingSystems -notmatch 'if \(active\.Stop != stop\)') {
    throw 'An admitted bus must remain held until its route target advances to another stop.'
}
if ($boardingSystems -match 'active\.SelectedForVehicleAi != 0[\s\S]*?slot\.m_Vehicle != bus') {
    throw 'Shared stop-slot rotation must not release another bus from its boarding hold.'
}
if ($boardingSystems -notmatch 'CanFinishBoarding[\s\S]*?ArePassengersReady' -or
    $boardingSystems -notmatch 'VehicleUtils\.SetTarget') {
    throw 'A completed follower must use the passenger-ready gate and next waypoint.'
}
if ($boardingSystems -match 'BoardingData|ScheduleBoarding|EndBoarding') {
    throw 'Synthetic follower sessions must not invoke an unmatched native boarding job.'
}
if ($boardingSystems -notmatch 'internal Entity Route;' -or
    $boardingSystems -notmatch 'EnsureRouteAssociation\(bus, active\)' -or
    $boardingSystems -notmatch 'AddComponentData\(bus, new CurrentRoute\(active\.Route\)\)' -or
    $boardingSystems -notmatch 'BeginRouteHandoff\(bus, active\.Route\)' -or
    $boardingSystems -notmatch 'class RouteHandoffSystem' -or
    $boardingSystems -notmatch 'AddComponentData\(bus, new CurrentRoute\(handoff\.Route\)\)') {
    throw 'Managed boarding must preserve the bus line association across native stop completion.'
}
if ($boardingSystems -notmatch 'ComponentType\.ReadOnly<CurrentRoute>\(\),') {
    throw 'Concurrent admission must reject buses without a native line association.'
}
if (($boardingSystems | Select-String -Pattern 'm_Buses = GetEntityQuery' -AllMatches).Matches.Count -ne 5 -or
    ($boardingSystems | Select-String -Pattern 'ComponentType\.Exclude<Deleted>\(\)' -AllMatches).Matches.Count -lt 5 -or
    ($boardingSystems | Select-String -Pattern 'ComponentType\.Exclude<Game\.Tools\.Temp>\(\)' -AllMatches).Matches.Count -lt 5) {
    throw 'Simulation queries must exclude deleted and temporary buses and stops.'
}
if ($boardingSystems -notmatch 'ComponentType\.ReadOnly<Owner>\(\)' -or
    $boardingSystems -notmatch 'ComponentType\.ReadOnly<PathOwner>\(\)' -or
    $boardingSystems -notmatch 'ComponentType\.ReadOnly<CarCurrentLane>\(\)' -or
    $boardingSystems -notmatch 'ComponentType\.Exclude<TripSource>\(\)' -or
    $boardingSystems -notmatch 'ComponentType\.Exclude<OutOfControl>\(\)' -or
    $boardingSystems -notmatch 'prefabSystem\.TryGetPrefab\(prefab, out CarPrefab _\)') {
    throw 'Admission candidates must match native transport-car safety requirements and have a loaded car prefab.'
}
if ($project -notmatch 'CbbDiagnostics' -or
    $project -notmatch 'CBB_DIAGNOSTICS' -or
    $breadcrumbs -notmatch '\[Conditional\("CBB_DIAGNOSTICS"\)\][\s\S]*?void Start\(' -or
    $breadcrumbs -notmatch '\[Conditional\("CBB_DIAGNOSTICS"\)\][\s\S]*?void Write\(' -or
    $breadcrumbs -notmatch '\[Conditional\("CBB_DIAGNOSTICS"\)\][\s\S]*?void Stop\(') {
    throw 'Crash breadcrumbs must remain opt-in for local diagnostic builds only.'
}
