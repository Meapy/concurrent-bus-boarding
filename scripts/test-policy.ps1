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
$mod = Get-Content -Raw "$root\ConcurrentBusBoarding\Mod.cs"
$zoneEditor = Get-Content -Raw "$root\ConcurrentBusBoarding\BoardingZoneEditorUISystem.cs"
$zoneRenderer = Get-Content -Raw "$root\ConcurrentBusBoarding\BoardingZoneRenderSystem.cs"
$colorOverride = Get-Content -Raw "$root\ConcurrentBusBoarding\BoardingZoneColorOverride.cs"
$customColor = Get-Content -Raw "$root\ConcurrentBusBoarding\BoardingZoneCustomColor.cs"
$transitAttractiveness = Get-Content -Raw "$root\ConcurrentBusBoarding\PublicTransportAttractivenessSystem.cs"
$project = Get-Content -Raw "$root\ConcurrentBusBoarding\ConcurrentBusBoarding.csproj"
$breadcrumbs = Get-Content -Raw "$root\ConcurrentBusBoarding\CrashBreadcrumbs.cs"
if ($boardingSystems -match 'm_DepartureFrame = math\.max') {
    throw 'A synthetic session must open its dwell window from the current frame, as native StartBoarding does.'
}
# Native StartBoarding begins with the window CLOSED at 0 and widens it each tick from
# m_MinWaitingDistance. Opening it fully at admission destroys that ratchet.
if ($boardingSystems -notmatch 'private void BeginBoarding[\s\S]*?m_MaxBoardingDistance = 0f;') {
    throw 'A boarding session must start with the native closed window, not a fully open one.'
}
# Closing the doors on a quiet passenger count fires before the ratchet has admitted anyone.
if ($boardingSystems -match 'ShouldCloseDoors\([^)]*exchangeSettled') {
    throw 'Doors must not close on a settled passenger count; the window needs time to widen.'
}
# Inflated dwell raises a line's measured waiting time, which is what drives cims away from buses.
if ($boardingSystems -notmatch 'BoardingPolicy\.ClampManagedDeparture\(') {
    throw 'A managed session must not inherit the native far-future departure frame.'
}
if ($boardingSystems -notmatch 'm_DepartureFrame = m_SimulationSystem\.frameIndex \+ 64u' -or
    $boardingSystems -notmatch 'm_MaxBoardingDistance = float\.MaxValue' -or
    $boardingSystems -notmatch 'm_MinWaitingDistance = float\.MaxValue') {
    throw 'Stopped buses must retain the boarding dwell handshake with an open first window.'
}
if ($boardingSystems -notmatch 'internal uint AdmittedFrame;' -or
    $boardingSystems -notmatch 'BoardingPolicy\.HasSessionExpired\(' -or
    $boardingSystems -notmatch 'ForceReleaseConcurrentBoarding\(EntityManager, bus, active\)') {
    throw 'Every managed session must be released by an unconditional admission-frame deadline.'
}
if ($boardingSystems -notmatch 'internal byte SelectedForPassengers;' -or
    $boardingSystems -notmatch 'SelectedForPassengers != 0' -or
    $boardingSystems -match 'PassengerSelectionTurn') {
    throw 'The passenger stop slot must follow the car-AI tick selection, not a free-running frame counter.'
}
if ($boardingSystems -notmatch 'ShouldExposeBoardingToVehicleAi\(active\.UsesNativeBoarding != 0\)') {
    throw 'A native boarding session must stay continuously visible to the car AI.'
}
if ($boardingSystems -notmatch 'BoardingPolicy\.NativeCompletionGraceFrames' -or
    $boardingSystems -notmatch 'm_NativeCompletions\+\+' -or
    $boardingSystems -notmatch 'm_ManagedCompletions\+\+') {
    throw 'A follower the native lifecycle cannot finish must reach managed completion before the dwell deadline.'
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
# The mod writes BoardingVehicle.m_Vehicle for every active session, so every release path must
# clear it. Leaving it set points the stop at a departed bus and blocks that stop permanently.
if ($boardingSystems -match 'UsesNativeBoarding == 0[\s\S]{0,600}?slot\.m_Vehicle = Entity\.Null') {
    throw 'Stop-slot release must not be conditional on the session kind.'
}
if ($boardingSystems -notmatch 'ArePassengersReady\(' -or
    $boardingSystems -notmatch 'BoardingPolicy\.CanFinishBoarding\(' -or
    $boardingSystems -notmatch 'VehicleUtils\.SetTarget') {
    throw 'A completed follower must use the passenger-ready gate and next waypoint.'
}
if ($boardingSystems -notmatch 'BoardingPolicy\.IdleAttemptsBeforeDeparture' -or
    $boardingSystems -notmatch 'internal byte IdleAttempts;') {
    throw 'A concurrent bus must be able to finish on its own exchange, not only on the shared queue ratchet.'
}
if ($boardingSystems -match 'else if \(!passengersReady && !timedOut\)' -or
    $boardingSystems -notmatch 'CountUnreadyPassengers\(EntityManager, bus,' -or
    $boardingSystems -notmatch 'm_SessionsThatSawAWaitingCim\+\+') {
    throw 'Completion gates must be measured independently; a chained counter hides every gate after the first.'
}
# Concurrent boarding is opt-in, and existing settings files must be migrated to match.
if ($settings -match 'public bool EnableConcurrentBoarding \{ get; set; \} = true;') {
    throw 'Concurrent boarding must stay opt-in.'
}
if ($settings -notmatch 'CurrentSettingsVersion' -or
    $settings -notmatch 'public int SettingsVersion' -or
    $mod -notmatch 'Settings\.SettingsVersion < ConcurrentBusBoardingSettings\.CurrentSettingsVersion' -or
    $mod -notmatch 'Settings\.EnableConcurrentBoarding = false;') {
    throw 'Updating players must be migrated to the opt-in default exactly once.'
}
if ($settings -notmatch 'public bool EnableConcurrentBoarding' -or
    ($boardingSystems | Select-String -Pattern '!Mod\.Settings\.EnableConcurrentBoarding' -AllMatches).Matches.Count -lt 2) {
    throw 'Concurrent boarding must have a runtime kill switch that also releases active sessions.'
}
if ($boardingSystems -notmatch 'BoardingPolicy\.ShouldEngageConcurrentBoarding\(contenders\)' -or
    $boardingSystems -notmatch 'if \(!engage \|\|') {
    throw 'A single bus at a stop must be left entirely to native AI; the mod only resolves contention.'
}
if ($boardingSystems -notmatch 'BoardingPolicy\.ShouldCloseDoors\(' -or
    $boardingSystems -notmatch 'internal byte DoorsClosing;' -or
    $boardingSystems -notmatch 'transport\.m_MaxBoardingDistance = 0f;') {
    throw 'A boarding session must stop admitting new passengers before it can require them all to be ready.'
}
if ($boardingSystems -notmatch 'entry\.Value\.Contains\(slot\.m_Vehicle\)' -or
    $boardingSystems -notmatch '!BoardingHelpers\.ArePassengersReady\(EntityManager, slot\.m_Vehicle\)') {
    throw 'Stop-slot rotation must not strand a cim that is still climbing aboard the current bus.'
}
$repair = Get-Content -Raw "$root\ConcurrentBusBoarding\BoardingRepairSystem.cs"
# Structural changes inside GameSimulation break the game's own command-buffer acquisition.
if ($mod -match 'UpdateAt<BoardingRepairSystem>\(SystemUpdatePhase\.GameSimulation\)' -or
    $mod -match 'UpdateAt<LineDiagnosticsSystem>\(SystemUpdatePhase\.GameSimulation\)') {
    throw 'Systems making structural changes must not run in the GameSimulation phase.'
}
if ($mod -notmatch 'UpdateAt<BoardingRepairSystem>' -or
    $repair -notmatch 'OnGameLoadingComplete' -or
    $repair -notmatch 'RequestRepair' -or
    $settings -notmatch 'RepairBoardingState') {
    throw 'Residue left in a saved city must be repaired on load and on demand.'
}
# A bus that is not boarding has a zero boarding distance and a stale departure frame by design.
# Repairing those would rewrite every healthy vehicle in the city instead of the stuck ones.
if ($repair -notmatch 'if \(boarding\)') {
    throw 'Vehicle repair must only touch buses that are actually stuck in Boarding.'
}
# One orphaned stop slot blocks that stop permanently, so it cannot wait for the next city load.
if ($repair -notmatch 'SweepIntervalFrames') {
    throw 'Orphaned stop slots must be swept continuously, not only when a city loads.'
}
# The sweep must never touch another transport mode's stop, and must never act on a single
# observation, which races the game's own asynchronous EndBoarding and steals live boardings.
if ($repair -notmatch 'IsPassengerBusStop\(EntityManager, stop\)' -or
    $repair -notmatch 'm_StaleLastSweep\.Contains\(stop\)') {
    throw 'The sweep must cover bus stops only and require two consecutive stale observations.'
}
# Measurement disproved the reachability premise: concurrent buses board and unload normally at
# zone distances, so admission must not reject a contained bus on distance alone.
if ($boardingSystems -match 'IsWithinPassengerReach') {
    throw 'Admission must not reject a contained bus on distance; measurement disproved that premise.'
}
# The spread system stays unregistered: its premise was disproven and displacing waiting cims
# correlates with them abandoning the wait. If it is ever re-registered, LimitWaitingBoundsToReach
# must bound it.
if ($mod -match 'UpdateAfter<PassengerWaitingSpreadSystem') {
    throw 'The passenger spread must stay unregistered; the native queue owns where cims wait.'
}
if ($boardingSystems -notmatch 'LimitWaitingBoundsToReach') {
    throw 'Keep the bounded waiting helper so any future spread cannot place cims outside the zone.'
}
if ($project -notmatch 'CbbObserverOnly' -or
    $project -notmatch 'CBB_OBSERVER_ONLY' -or
    $mod -notmatch '#if CBB_OBSERVER_ONLY') {
    throw 'The observer-only A/B package must stay an opt-in build flag.'
}
if ($env:CbbObserverOnly -eq 'true') {
    throw 'Refusing to validate an observer-only build as a release package.'
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
# A pathfind prefab shared with another passenger mode must never receive the bus multiplier.
if ($settings -notmatch 'public int BusAttractiveness \{ get; set; \} = 100;' -or
    $transitAttractiveness -notmatch 'm_BusOnlyCosts' -or
    $transitAttractiveness -notmatch 'line\.m_TransportType == TransportType\.Bus' -or
    $transitAttractiveness -notmatch 'existing && isBus') {
    throw 'The bus multiplier must apply only to pathfind costs used exclusively by bus lines.'
}
