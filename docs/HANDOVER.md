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
- Active buses share the stop's native `BoardingVehicle` pointer round-robin. Each bus keeps the pointer for one complete
  16-frame resident update sweep so every waiting-resident partition can try it before the next bus is selected, while
  the game retains route, fare, capacity, animation, and passenger-buffer behavior.
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

Release procedure completed on 2026-07-22. The publisher-specific `ForumLink` correction was merged through PR #4 as
`be0a0d1`, and the live metadata update succeeded.

## Next work

Re-run the gameplay calibration after each Cities: Skylines II update that changes `Game.dll`. Do not restore direct
`CarCurrentLane`, `CarNavigationLane`, transform, or rotation writes for front packing; the tested release deliberately
uses native traffic spacing.

### Post-release crash audit (2026-07-23)

The current source still has crash-capable lifecycle edges even though diagnostic v6 passed its gameplay run:

1. **Synthetic boarding remains the highest risk.** `ConcurrentBoardingSystem.BeginBoarding` writes the native
   `Boarding` flag and stop slot without queuing the installed game's matching
   `TransportBoardingHelpers.BoardingData.BeginBoarding`. Installed 1.6.0 IL confirms that a selected synthetic bus can
   later reach native `TransportCarTickJob.StopBoarding`, which can queue `EndBoarding`. The policy test only rejects an
   explicit call in mod source and therefore does not cover this indirect native path.
2. **Route restoration can undo an intentional native transition.** Both `EnsureRouteAssociation` and the 512-frame
   `RouteHandoffSystem` re-add `CurrentRoute` when the route entity still exists, without checking the bus's current
   target or `Returning`, `AbandonRoute`, maintenance, disabled, or out-of-control state. Installed IL confirms that
   native transport AI intentionally removes `CurrentRoute` during depot return, dispatch, and boarding-abandon paths.
3. **The manual next-waypoint handoff accepts a stale waypoint.** `TryAdvanceToNextWaypoint` checks the route buffer and
   current index, but not whether the chosen next waypoint exists, has `Waypoint`, or is non-`Deleted`/non-`Temp`.
   Installed `VehicleUtils.SetTarget` only copies the entity and marks the path dirty; it performs no validation.
4. **The render cache is unbounded.** `BoardingZoneRenderSystem.m_Zones` retains deleted stops and their managed
   lane-piece lists forever. This is a low-probability long-session memory/iteration risk, not an evidenced immediate
   crash.

The preserved Coherent UI/V8 failure does not implicate this bundle: registration is idempotent, the CBB UI log has no
module exception, and the crash boundary instead names BetterTransitView/Move It activity plus a logger null reference.
The repeated empty-stack native failures remain correlated with synthetic admission of saved bus `268079:1` and an
unresolved StarQ bus prefab. Requiring `CurrentRoute` prevented that known entity from entering diagnostic v6, but it is
an exclusion guard rather than proof that the synthetic lifecycle is safe.

Recommended diagnostic patch order: stop restoring routes across explicit native retirement states; fully validate the
next waypoint before `SetTarget`; then redesign follower admission so begin/end are paired or the bus never enters the
native completion path. A gameplay A/B should cover route abandonment, depot return, deleting an active stop, and a
save with a missing custom-bus asset.

For a local crash investigation, build with
`dotnet build ConcurrentBusBoarding.slnx -c Release -p:CbbDiagnostics=true`. The diagnostic package writes the bounded,
auto-flushed `Logs/ConcurrentBusBoarding-breadcrumbs.log`; never pass that property to the Paradox publishing build.

### Crash hardening implementation (2026-07-23)

The post-release audit findings are addressed in the current workspace. The exact verified package was deployed to the
local `ConcurrentBusBoarding` Mods folder on 2026-07-23 while Cities II was closed; gameplay testing remains:

- Mod logger errors now opt into the in-game error UI.
- `ConcurrentBoardingActive` records whether the game or the mod began the boarding session. Synthetic sessions have
  their `Boarding` flag cleared before `TransportCarAISystem`, so native `StopBoarding` cannot consume an unpaired
  lifecycle. If vehicle AI begins boarding during that tick, the mod detects the returned flag and adopts the now-native
  session instead of using managed completion.
- Managed route preservation now requires a live route, a live target waypoint owned by that route, matching waypoint
  index/buffer membership, an unchanged `CurrentRoute` when present, and a normal active transport state. Returning,
  evacuating, prisoner transport, maintenance, refueling, abandon-route, dummy-traffic, disabled, out-of-control,
  deleted, temporary, and route-reassigned buses are released without restoring the captured route.
- Synthetic cleanup clears only synthetic boarding state and only stop-slot references still owned by that bus. Native
  sessions are released without fabricating native cleanup.
- Manual next-waypoint advancement validates both the current and next waypoint before calling `VehicleUtils.SetTarget`.
- Stop, bus, prefab, and render-cache reads reject deleted/temporary entities. The overlay validates every lane piece,
  curve sample, bound, width, and saved custom length before drawing; it skips zero-length primitives and evicts stale
  geometry. Map dragging also rejects non-finite pointer positions and lengths.
- The global settings page has a confirmation-protected **Reset all customized zones** button. Its setter only queues
  the request; `BoardingZoneEditorUISystem` performs the structural ECS removal on its own update, removes every live
  `BoardingZoneOverride` in the current city, and invalidates the overlay.

Broad `try/catch` blocks were deliberately not added around simulation updates. A caught exception after partial ECS
mutation could leave more dangerous state behind, and managed catches cannot intercept Burst/native access violations.
The hardening instead prevents the invalid states found by the audit; the game remains responsible for surfacing
managed system exceptions, and the mod logger is configured to show its own errors in the UI.

Verification:

- Dependency-free boarding policy checks pass, including native/synthetic ownership and route-restoration assertions.
- Webpack 5.97.1 production UI build and zone-editor smoke check pass.
- Whitespace verification and `git diff --check` pass.
- The official Cities: Skylines II 1.6.0 toolchain builds the isolated
  `artifacts/hardening-20260723/ConcurrentBusBoarding` package with 0 warnings and 0 errors.
- The staged 54,272-byte DLL SHA-256 is
  `898DF2E4FF1C4AC227F095EA21B4520235379DEA1495835540305B02F3F7D6E0`.
- All eight staged/live file hashes match. The replaced diagnostic-v6 package is recoverable from
  `artifacts/pre-hardening-live-20260723-1029/ConcurrentBusBoarding`.

Before release, gameplay-test concurrent native and synthetic followers, depot return, route abandonment/reassignment,
deleting an active stop, and a save with a missing custom-bus asset. Confirm both boarding behavior and route-panel
persistence. Also toggle selected/all-stop overlays, edit a zone, and delete or rebuild roads while overlays are visible
to confirm stale zones disappear without rendering errors. Create several custom zones, cancel the global reset once,
then confirm it and verify that all zones return to automatic sizing and remain automatic after save/reload.

### Rear-bus passenger retry hardening (2026-07-23)

Installed Cities: Skylines II 1.6.0 IL confirms that `ResidentAISystem` updates residents in 16 fixed frame partitions.
For a waiting resident, `RouteUtils.GetBoardingVehicle` supplies only the stop's single advertised vehicle; if
`BoardingJob.TryFindVehicle` finds that vehicle full, it returns no vehicle and does not try another active bus.
Previously, the mod changed the advertised bus every frame. A resident in one fixed partition could therefore always
sample the full lead bus while a following bus was advertised only on other partitions.

`PassengerDistributionSystem` now derives its rotation turn from `simulationFrame / 16`. Each active bus owns the
passenger-facing stop slot for a complete native resident sweep before rotation advances. The rotation still includes
full buses so their onboard passengers retain a complete sweep in which to exit.

Verification:

- Policy checks prove that all frames 0-15 select the first bus and all frames 16-31 select the following bus.
- The UI production bundle and zone-editor smoke check pass.
- Whitespace verification and `git diff --check` pass.
- The official toolchain builds `artifacts/rear-boarding-20260723/ConcurrentBusBoarding` with 0 warnings and 0 errors.
- The staged 54,272-byte DLL SHA-256 is
  `403501CDD3DB9E12B2C756BE8666978E4CE9071BBB4464ACA0E2981276157AF9`.
- After Cities II closed, all eight staged files were copied to the live local Mods package and their SHA-256 hashes
  were verified. The replaced hardening/reset package is recoverable from
  `artifacts/pre-rear-boarding-live-20260723-1112/ConcurrentBusBoarding`.
- Gameplay confirmation remains: test a full lead bus, a following bus with capacity, and unloading from both buses.
