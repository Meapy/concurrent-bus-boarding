# Handover

## Release candidate

Version 1.1.0 is the published concurrent-boarding implementation for Cities: Skylines II 1.6.0.

> Diagnostic v6 was deployed with all eight hashes matching, and the user confirmed the corrected boarding and route
> panel behavior works properly. Version 1.1.0 is approved for release as a clean non-diagnostic build.

- The native stop position is the fixed forward edge of every boarding zone. Ordinary zones extend 26 m backward;
  edited zones extend 6–200 m backward; automatic pull-ins use their usable physical lane behind the stop.
- Zone geometry follows connected inbound `RouteSegment`/`PathElement` lane pieces, so long custom zones can cross
  ordinary segment and junction boundaries without moving onto a nearby main road or driveway.
- The closest observed physical bus lane remains authoritative over inferred geometry. This keeps paired pull-in stops
  on their respective bays.
- Native traffic controls bus movement, direction, collision spacing, current-lane occupancy, navigation buffers,
  transforms, and rotation. The registered systems never reposition a bus or rewrite its navigation endpoint.
- A stopped bus whose centre is inside the zone can enter managed boarding. Ordinary automatic stops admit two buses;
  pull-ins use vehicle-length capacity; custom zones admit every contained stopped bus.
- Active buses share the stop's native `BoardingVehicle` pointer round-robin so waiting residents can choose each bus
  while the game retains route, fare, capacity, animation, and passenger-buffer behavior.
- Each admitted bus is physically held at its own queue position. The bus ahead leaving cannot pull it forward.
- Concurrent admission requires the bus's native `CurrentRoute`, rejecting line-detached or orphaned save vehicles.
  The managed latch retains the route, and a bounded post-stop handoff restores a one-time removal after the latch ends
  so the vehicle Line row and route panel remain available without overriding a later genuine route change.
- A follower completes only after the native dwell time, waiting-distance sweep, and every onboard passenger transition
  are ready. It then clears its managed boarding state and targets the next route waypoint. Synthetic sessions must not
  enqueue native `EndBoarding`, because they never enqueue the matching native `BeginBoarding` record.
- **Only show the selected stop** defaults on. Selecting a served stop exposes the rearward zone length and **Edit on
  map**; either cyan rear corner resizes the saved zone while the stop-side edge remains fixed.
- Crash breadcrumb source remains available for local diagnosis. Calls are compiled only when the build passes
  `-p:CbbDiagnostics=true`; ordinary Release and Paradox publishing builds omit them and have no breadcrumb file I/O.

## Verification

- User gameplay confirmation: simultaneous boarding, stable stopped followers, passenger exchange, and clean departure
  all behave correctly.
- Live diagnostic candidate: the initial same-stop pair and many later followers completed independently, with active
  hold counts falling rather than accumulating.
- Preserved crash evidence: `artifacts/crash-20260722-202916` ends after a successful managed admission boundary,
  missing regional-prefab warnings, and asset cleanup. The final breadcrumb does not establish crash causality.
- The paired v3 trace in `artifacts/crash-20260722-210157` narrows the failure to native AI immediately after managed
  admission of a saved low-index vehicle; the same fatal boundary reports unresolved
  `CarPrefab:StarQ Bus03AsPublicTransport`. Admission now matches the native transport-car component exclusions and
  requires `PrefabSystem.TryGetPrefab<CarPrefab>` to succeed before changing boarding state.
- `artifacts/crash-20260722-212117` shows that the prefab guard was not sufficient: the saved bus passed resolution,
  later admissions also completed, and the process still ended in an empty-stack native Mono crash. The remaining
  unpaired `BoardingData.EndBoarding` job has therefore been removed; the next diagnostic run is the release gate.
- `artifacts/crash-20260722-214523` is a different failure signature: the native stack is Coherent UI/V8 immediately
  after `[BetterTransitView] Button rendering...`, with a `UnityLogger` null reference. The paired CBB trace ended about
  ten seconds earlier. Diagnostic v5 also preserves `CurrentRoute`, which the native vehicle UI requires for the Line
  row and route panel.
- `artifacts/crash-20260722-221352` returns to the empty-stack native Mono signature. Its v5 trace again ends immediately
  after admitting saved bus `268079:1`, followed by an unresolved `CarPrefab:StarQ Bus03AsPublicTransport` warning.
  V6 requires `CurrentRoute` at admission and carries the route across the native `Boarding` to `En route` transition.
- `powershell -ExecutionPolicy Bypass -File scripts/test-policy.ps1` passes.
- `dotnet format ConcurrentBusBoarding.slnx whitespace --verify-no-changes --no-restore` passes.
- Diagnostic v6 builds with the official toolchain into `artifacts/diagnostic-v6` with 0 warnings and 0 errors; managed
  DLL SHA-256 is `09CB676AE9A0E068386E1EFA4DDA1F62781FDEE4262BD98773A7496DA6507729`.
- The UI production bundle and smoke check pass with webpack 5.97.1.
- The clean non-diagnostic package builds into `artifacts/release-1.1.0-final` with 0 warnings and 0 errors.
- The clean package DLL SHA-256 is `F87D520F03528C667640EB503587012B2613783A12223A13E1C241F6087B0E3C`;
  Mono.Cecil verification finds zero calls to `CrashBreadcrumbs` outside the retained diagnostic class itself.
- Support and feedback thread: https://forum.paradoxplaza.com/forum/threads/mod-concurrent-bus-boarding-allow-several-buses-use-the-same-stop-at-the-same-time.1935925/
- Paradox Mod Publisher requires the dedicated `ForumLink` element; a generic `ExternalLink Type="forum"` is ignored.
- GitHub PR #3 was squash-merged as `1c688d3`; Paradox Mods accepted version 1.1.0 and its metadata-only
  `ForumLink` correction. The public mod page returns HTTP 200.
- Publish the exact verified staged package with `ModPublisher.exe NewVersion` and verify Paradox mod `152153` reports
  version 1.1.0, compatibility `1.6.0*`, all five screenshots, and the GitHub source link.

## Release procedure

Release procedure completed on 2026-07-22. The only follow-up commit is the publisher-specific `ForumLink` correction.

## Next work

Re-run the gameplay calibration after each Cities: Skylines II update that changes `Game.dll`. Do not restore direct
`CarCurrentLane`, `CarNavigationLane`, transform, or rotation writes for front packing; the tested release deliberately
uses native traffic spacing.

For a local crash investigation, build with
`dotnet build ConcurrentBusBoarding.slnx -c Release -p:CbbDiagnostics=true`. The diagnostic package writes the bounded,
auto-flushed `Logs/ConcurrentBusBoarding-breadcrumbs.log`; never pass that property to the Paradox publishing build.
