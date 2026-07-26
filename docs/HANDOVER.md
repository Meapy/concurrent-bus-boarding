# Handover

## Release candidate

Version 1.4.2 is the current release candidate for Cities: Skylines II 1.6.0.

> Version 1.4.2 fixes the public transport attractiveness slider's runtime scheduling. It now waits for loaded passenger
> pathfind data during normal simulation, refreshes route costs before resident path selection, and logs the saved
> percentage plus the number of affected native cost profiles.

> Version 1.3.0 is the published concurrent-boarding and overlay-colour implementation. Version 1.4.0 adds one
> global public-transport attractiveness slider without changing boarding, vehicle movement, or route ownership.

- **Public transport attractiveness** ranges from 50% to 200%. The 100% default preserves native passenger-route
  starting costs; higher values reduce those costs for every passenger transport line while cargo remains unchanged.
- The slider initializes inline at 100 as well as through `SetDefaults()`, so settings files created before 1.4.0 do
  not display C#'s zero-value default.
- Each passenger line pathfinding prefab is adjusted from its captured native baseline, and native route edges are
  refreshed only when the setting changes. Destroying the system restores the exact captured costs.

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

### First-bus native admission correction (2026-07-23)

The crash-hardening build could classify the first stopped bus at an idle stop as a synthetic session immediately before
native vehicle AI ran. Synthetic state is intentionally hidden from `TransportCarAISystem` to prevent an unpaired
native completion, so that first bus could resume driving instead of starting its vanilla boarding lifecycle.

Synthetic admission now requires at least one already-active boarding bus at the stop. The first bus remains unmanaged
until native vehicle AI begins boarding; the mod then adopts that paired native session on the next 16-frame update.
Following stopped buses can still enter managed boarding once that lead session exists.

Verification:

- Policy checks cover both boundaries: zero active buses reject synthetic admission and one active bus permits it.
- The UI production bundle, smoke check, whitespace verification, and `git diff --check` pass.
- The official toolchain builds `artifacts/first-bus-native-20260723/ConcurrentBusBoarding` with 0 warnings and 0 errors.
- The staged 54,272-byte DLL SHA-256 is
  `5C1D91A07F0BE038AEB6705C721743B956637E2352A393CDC605BCFBDB47FBAC`.
- After Cities II closed, all eight staged files were copied to the live local Mods package and their SHA-256 hashes
  were verified. The replaced package is recoverable from
  `artifacts/pre-first-bus-native-live-20260723-113348/ConcurrentBusBoarding`.
- Gameplay confirmation remains: verify that an empty first bus stops and enters native boarding, then that a stopped
  follower boards concurrently and receives passengers when the lead bus is full.

### Target-zone stop request (2026-07-23)

Gameplay showed that an empty first bus could still pass a stop with waiting passengers: its target changed to the next
waypoint as it reached the stop. Installed 1.6.0 IL shows that residents normally set
`PublicTransportFlags.RequireStop`, but resident and vehicle AI update independently. If no resident partition marks
the approaching bus before vehicle AI reaches the waypoint, native AI can advance it without starting boarding.

`ConcurrentBoardingSystem` now sets that same native `RequireStop` flag when an unmanaged bus is inside its target
stop's valid boarding zone and the zone has admission capacity. It does not alter navigation, movement, transforms, or
the target waypoint. The first bus still enters the paired native boarding lifecycle; a stopped follower still uses
managed admission only after a lead session exists.

Verification:

- Policy checks cover eligible, out-of-zone/full-zone, and already-boarding stop-request decisions.
- The UI production bundle and zone-editor smoke check pass.
- Whitespace verification and `git diff --check` pass.
- The official toolchain builds `artifacts/lead-bus-require-stop-20260723/ConcurrentBusBoarding` with 0 warnings and
  0 errors.
- The staged and live 54,272-byte DLL SHA-256 is
  `F48BCF8D1706D3921EA3AE87430EA4596FA2351B3CCD170FD040BCCB0A4B3A50`.
- All eight staged files were copied to the live local Mods package and verified byte-for-byte. The replaced package
  is recoverable from `artifacts/pre-lead-stop-live-20260723-115214/ConcurrentBusBoarding`.
- Gameplay confirmation remains: verify that an empty first bus stops for waiting passengers, then verify a follower
  behind a full lead bus also stops and boards.

### Version 1.2.0 release preparation (2026-07-23)

The user authorized pushing, merging, and publishing the complete `feature/crash-hardening` branch. `CHANGELOG.md`
and `Properties/PublishConfiguration.xml` now identify version 1.2.0 and summarize crash hardening, safe zone
rendering, the global zone reset, full-sweep rear-bus passenger selection, native first-bus admission, and target-stop
requests.

Release input:

- Source metadata commit: `1055d5b` (`Release Concurrent Bus Boarding 1.2.0`).
- Verified package: `artifacts/lead-bus-require-stop-20260723/ConcurrentBusBoarding`.
- Managed DLL: 54,272 bytes, SHA-256
  `F48BCF8D1706D3921EA3AE87430EA4596FA2351B3CCD170FD040BCCB0A4B3A50`.
- All eight package hashes were re-read successfully; required DLL, MJS, CSS, and three platform libraries are present.
- Policy checks, XML parsing, `git diff --check`, UI production build/smoke test, and whitespace verification pass.
- A fresh duplicate build was not run because the execution service rejected the elevated toolchain command for usage
  limits. The selected package is the already verified official-toolchain build of the exact runtime commit
  `99d82e6`; release metadata does not change packaged binaries.

Remote state is unchanged. The official Paradox `NewVersion` upload and `git push` were each rejected before execution
by the execution service's usage limit. Do not record version 1.2.0 as public until the branch is pushed, its PR is
merged, the exact package above is accepted by ModPublisher, and both GitHub and public mod `152153` are verified.

### Overlay colour customization (2026-07-25)

- The first deployed candidate failed in game. `Player.log` recorded
  `TargetParameterCountException` in `Colossal.Json.DiffUtility.DiffObject`: settings reflection traversed
  `UnityEngine.Color.Item` without its required index parameter. The unsupported property was also omitted from
  Options, and the new value loaded as transparent black, hiding selected-stop overlays.
- The corrected settings model persists four hidden integer RGBA channels. A visible
  `ChooseGlobalOverlayColor` settings button is replaced in the frontend with the installed game's native
  `ColorCustomizeField`, which opens its radial colour wheel. The same wheel is shown in the selected-stop panel when
  that stop uses the global colour. An all-zero missing value safely falls back to the previous blue at 0.28 alpha.
- `BoardingZoneColorOverride` is a separate serializable per-stop component, preserving the released
  `BoardingZoneOverride` layout. Component absence means global colour.
- **Use line colour** resolves `ConnectedRoute.m_Waypoint -> Owner.m_Owner -> Game.Routes.Color`. It uses the first
  valid route in the native connected-route/Lines order, follows later line RGB changes, retains global alpha, and
  falls back to global when no valid route colour exists.
- **Use global colour** removes only that stop's colour component. The existing confirmed reset still removes only
  customized lengths; the new confirmed **Reset all stop overlay colours** action removes only colour overrides.
- Policy checks, UI production bundling/smoke testing, `git diff --check`, and the official 1.6.0 Release build pass
  with 0 warnings and 0 errors. The corrected live eight-file package was deployed by the official toolchain while
  Cities II was closed; its 59,392-byte DLL SHA-256 is
  `40F6FCD89242BC79FF88586979DD4726A9AB2438CCEE30C7043D9BE1948E2C81` and its MJS SHA-256 is
  `F3D58655171A5D31BCEFF4E0023F3C748D6DB438EB05C00CF21D2F02C6156F49`.
- The malformed local settings file was moved, not deleted, to
  `ConcurrentBusBoarding.coc.broken-overlay-colour-20260725`. It contained the incorrect root type
  `ConcurrentBusBoarding.Color`; the corrected build will regenerate settings defaults on next launch. City-saved
  zone lengths and per-stop colour-source components are unaffected.
- Source is pushed on `feature/overlay-colours`; draft PR #5 is
  `https://github.com/Meapy/concurrent-bus-boarding/pull/5`.
- Corrected runtime commit `583163a` is pushed and the draft PR body records the regression, root cause, replacement
  persistence model, exact build hashes, and remaining gameplay gate. In-game confirmation remains for Options
  visibility, both colour-wheel locations, selected-stop overlay visibility, global updates, line recolouring, shared
  stops, old/new save loading, save/reload, separate resets, selected-only rendering, and map editing.
- A second gameplay report showed why transparency still behaved incorrectly: the native RGB-only colour picker
  returned alpha 1.0, and the trigger persisted it as `GlobalOverlayAlpha = 255`. RGB and opacity are now independent.
  `OverlayOpacity` is a native 5–60% Options slider with an 18% default; every global, stop-custom, line-custom, and
  native-line colour uses that alpha.
- The selected-stop wheel now writes `BoardingZoneCustomColor` either to the stop or to its first served route,
  controlled by a **This stop / Whole line** toggle. Stop custom, explicit native-line, and explicit-global choices
  take precedence over inherited route custom colour. Line-wide custom colour is discovered across connected routes
  so shared stops can inherit it. The separate reset-colours action removes source and custom-colour components.
- UI smoke, policy, formatting, diff, and official 1.6.0 Release checks pass. The exact eight-file final package is
  staged in `artifacts/overlay-colour-scope-20260725/ConcurrentBusBoarding` and deployed while Cities II is closed.
  Its 62,464-byte DLL SHA-256 is
  `91C199FB893F5463092F9F674A6FEFD54B1459C98631F76E39D2781A38503CA7`; MJS SHA-256 is
  `4AEEAF245C87EEBD9520C85209D71E45A7F8E25CBE767DD8F5E34E3A74EDA8B5`.
- The replaced intermediate package is recoverable from
  `artifacts/pre-overlay-colour-scope-live-20260725/ConcurrentBusBoarding`. The rewritten mixed-root settings file is
  recoverable as `ConcurrentBusBoarding.coc.pre-opacity-scope-20260725`; next launch will generate clean defaults.
  Gameplay confirmation remains required before merging.
- Implementation commit `bbf53ab` is pushed to `feature/overlay-colours`. Draft PR #5 is open with its title,
  behavior, verification, exact deployed hashes, and remaining gameplay checks updated. GitGuardian passes; GitHub
  reported mergeability `UNKNOWN` while recalculating after the handover-only push. Do not mark it ready or merge it
  until the global wheel, opacity slider, stop/line scope, presets, persistence, and boarding behavior pass in-game.
- Served stops now inherit a custom line colour or their first route's native colour when no stop choice is saved;
  explicit Global and custom-stop choices still win. The panel replaces the stacked source/scope actions with compact,
  active **This stop / Whole line** and **Global / Line colour** selectors. Policy, UI production/smoke, formatting,
  diff, and the official 1.6.0 Release build pass with 0 warnings/errors. The behaviorally complete candidate is live,
  but Cities II started before the final unused-binding/CSS cleanup could be copied. The exact final staged DLL is
  62,464 bytes with SHA-256 `E67F42BA38363B13B7F3EE9B87C4331DAD4AED78EC249C551AA20A00724733D0`;
  MJS is 5,368 bytes with SHA-256 `FA87544CC02304D73A005F6159DF0BFE5A2A1153DC03C83379B073807A0FD638`.
  Install it after the game closes. The prior package is recoverable from
  `artifacts/pre-default-line-ui-live-20260725/ConcurrentBusBoarding`.
- Supersedes the pending release/deployment state above: version 1.3.0 was built from the final single-focus segmented
  selector source, preventing the live candidate's multiple-focus-key UI errors. PR #5 passed GitGuardian and merged
  to `master` as `708c002`. `ModPublisher NewVersion` accepted the exact eight-file
  `artifacts/release-1.3.0-final/ConcurrentBusBoarding` package for public mod `152153`. The official public API reports
  latest version ID 5, user version `1.3.0`, public access, package size 47,722 bytes, and creation time
  `2026-07-25T21:22:54Z`. The final 62,464-byte DLL SHA-256 is
  `E008434C1D9B4EA37209D0E8077F2CE33D5208850373D793AFDE99C670A7D599`; the 5,200-byte MJS SHA-256 is
  `51D5E17792D2DF6204FEBF787AFC32CDFEEE60B44E2E347B20D004187D758904`. Publisher output confirmed the Paradox
  Forums `1935925` support URL and GitHub link. Cities II remained running, so the exact public package still needs
  copying to the local Mods folder after the game closes; do not overwrite its loaded files.
