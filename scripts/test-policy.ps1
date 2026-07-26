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
$settings = Get-Content -Raw "$root\ConcurrentBusBoarding\ConcurrentBusBoardingSettings.cs"
$zoneEditor = Get-Content -Raw "$root\ConcurrentBusBoarding\BoardingZoneEditorUISystem.cs"
$zoneRenderer = Get-Content -Raw "$root\ConcurrentBusBoarding\BoardingZoneRenderSystem.cs"
$colorOverride = Get-Content -Raw "$root\ConcurrentBusBoarding\BoardingZoneColorOverride.cs"
$customColor = Get-Content -Raw "$root\ConcurrentBusBoarding\BoardingZoneCustomColor.cs"
$transitAttractiveness = Get-Content -Raw "$root\ConcurrentBusBoarding\PublicTransportAttractivenessSystem.cs"
$project = Get-Content -Raw "$root\ConcurrentBusBoarding\ConcurrentBusBoarding.csproj"
$breadcrumbs = Get-Content -Raw "$root\ConcurrentBusBoarding\CrashBreadcrumbs.cs"
$mod = Get-Content -Raw "$root\ConcurrentBusBoarding\Mod.cs"
$concurrentStart = $boardingSystems.IndexOf('public partial class ConcurrentBoardingSystem')
$concurrentEnd = $boardingSystems.IndexOf('[UpdateAfter(typeof(TransportCarAISystem))]', $concurrentStart)
$concurrentSystem = $boardingSystems.Substring($concurrentStart, $concurrentEnd - $concurrentStart)
if ($boardingSystems -match '\.BeginBoarding\(') {
    throw 'The native boarding queue must not admit secondary buses before passenger exchange.'
}
if ($boardingSystems -notmatch 'if \(!foundCurrentLane\)[\s\S]*?if \(element\.m_Target == zone\.Lane\)[\s\S]*?continue;') {
    throw 'Rear-zone pieces must not start before the physical lane is found in the route path.'
}
if ($boardingSystems -notmatch 'ConsiderLane\(entityManager, routeLane\.m_EndLane[\s\S]*?if \(lane == Entity\.Null\)[\s\S]*?ConsiderLane\(entityManager, routeLane\.m_StartLane') {
    throw 'The stop-side route end lane must remain authoritative over the approach lane.'
}
if ($concurrentSystem -match 'slot\.m_Vehicle|SetComponentData\(stop,|m_State \|=.*PublicTransportFlags\.Boarding|VehicleUtils\.SetTarget|ConcurrentBoardingActive\(') {
    throw 'CBB stop admission must not virtualize native passenger ownership, boarding state, or route targets.'
}
if ($concurrentSystem -notmatch 'PublicTransportFlags\.RequireStop' -or
    $concurrentSystem -notmatch 'BoardingPolicy\.CanAdmit') {
    throw 'CBB must retain safe multi-bus zone admission through native stop requests.'
}
if ($mod -match 'PassengerDistributionSystem|RouteHandoffSystem') {
    throw 'Only native transport AI may own passenger boarding and route completion.'
}
if ($boardingSystems -notmatch 'ComponentType\.ReadOnly<CurrentRoute>\(\),') {
    throw 'Concurrent admission must reject buses without a native line association.'
}
if (($boardingSystems | Select-String -Pattern 'm_Buses = GetEntityQuery' -AllMatches).Matches.Count -ne 4 -or
    ($boardingSystems | Select-String -Pattern 'ComponentType\.Exclude<Deleted>\(\)' -AllMatches).Matches.Count -lt 4 -or
    ($boardingSystems | Select-String -Pattern 'ComponentType\.Exclude<Game\.Tools\.Temp>\(\)' -AllMatches).Matches.Count -lt 4) {
    throw 'Simulation queries must exclude deleted and temporary buses and stops.'
}
if ($boardingSystems -match 'm_MaxSpeed = 0f' -or $boardingSystems -match 'Moving moving') {
    throw 'Concurrent boarding must not leave native vehicle movement capped after a stop.'
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
if ($settings -notmatch '\[SettingsUIButton\][\s\S]*?\[SettingsUIConfirmation' -or
    $settings -notmatch 'public bool ResetAllZones' -or
    $settings -notmatch 'RequestResetAllZones\(\)' -or
    $zoneEditor -notmatch 'ComponentType\.ReadOnly<BoardingZoneOverride>\(\)' -or
    $zoneEditor -notmatch 'RemoveComponent<BoardingZoneOverride>\(m_ZoneOverrides\)') {
    throw 'The confirmed global reset must remove every live per-stop zone override through the UI system.'
}
if ($settings -match 'UnityColor GlobalOverlayColor' -or
    $settings -notmatch '\[SettingsUIHidden\][\s\S]*?GlobalOverlayRed' -or
    $settings -notmatch 'SettingsUISlider\(min = 5f, max = 60f, step = 1f, unit = "%"\)' -or
    $settings -notmatch 'public int OverlayOpacity' -or
    $settings -notmatch 'SetGlobalOverlayColor\(string rgb\)' -or
    $zoneEditor -notmatch 'TriggerBinding<string>\(BindingGroup, "setGlobalOverlayColor"' -or
    $zoneEditor -notmatch 'TriggerBinding<string, bool>\(BindingGroup, "setStopOverlayColor"' -or
    $settings -notmatch 'RequestResetAllZoneColors\(\)' -or
    $zoneEditor -notmatch 'RemoveComponent<BoardingZoneColorOverride>\(m_ColorOverrides\)' -or
    $zoneEditor -notmatch 'RemoveComponent<BoardingZoneCustomColor>\(m_CustomColors\)' -or
    $colorOverride -notmatch 'struct BoardingZoneColorOverride : IComponentData, ISerializable' -or
    $customColor -notmatch 'struct BoardingZoneCustomColor : IComponentData, ISerializable' -or
    $zoneRenderer -notmatch 'HasComponent<BoardingZoneCustomColor>\(route\)' -or
    $zoneRenderer -notmatch 'else if \(TryGetCustomRouteColor\(stop, global\.a, out UnityColor routeColor\)\)' -or
    $zoneRenderer -notmatch 'if \(!TryGetFirstRoute\(stop, out Entity nativeRoute\)' -or
    $zoneRenderer -notmatch 'nativeLine\.a = global\.a') {
    throw 'Overlay colours must use primitive settings, global opacity, default native line colours, saved stop/line custom colours, and separate colour reset.'
}
if ($settings -notmatch 'SettingsUISlider\(min = 50f, max = 200f, step = 5f, unit = "%"\)' -or
    $settings -notmatch 'public int PublicTransportAttractiveness \{ get; set; \} = 100;' -or
    $transitAttractiveness -notmatch 'line\.m_PassengerTransport' -or
    $transitAttractiveness -notmatch 'line\.m_PathfindPrefab' -or
    $transitAttractiveness -notmatch 'adjusted\.m_StartingCost\.m_Value' -or
    $transitAttractiveness -notmatch 'AddComponent<PathfindUpdated>\(m_RouteElements\)' -or
    $transitAttractiveness -match 'Game\.Citizens|TripNeeded|PublicTransportFlags') {
    throw 'Public transport attractiveness must adjust only native passenger-line starting costs and refresh route edges.'
}
