# Concurrent Bus Boarding

A Cities: Skylines II code mod that lets more than one bus board at the same stop.

Install the latest release from [Paradox Mods](https://mods.paradoxplaza.com/mods/152153/Windows).
Questions, feedback, and bug reports are welcome in the
[Concurrent Bus Boarding forum thread](https://forum.paradoxplaza.com/forum/threads/mod-concurrent-bus-boarding-allow-several-buses-use-the-same-stop-at-the-same-time.1935925/).

## Intended behaviour

- The native bus-stop position is always the forward edge of the boarding zone. Ordinary curbside zones extend 26 m backward; edited zones extend 6–200 m backward, and can never be moved ahead of the stop.
- Pull-in stops are detected generically from the resolved and route lane entities: native secondary-lane metadata identifies purpose-built bays, a route that changes lanes within the same road edge identifies an inset stop lane, and split-and-merge topology identifies branch-and-rejoin bays. Their automatic zone uses the available physical lane behind the stop, with bus count limited by usable length.
- A bus may join concurrent boarding when its centre is in the local boarding zone and its speed is at most 1.0 m/s. Native traffic AI provides collision spacing; the lane projection has a 2 m tolerance.
- Buses keep the game's native lane direction, collision avoidance, transforms, current-lane occupancy, and navigation endpoints. The mod never moves a bus toward a synthetic packed position; native traffic simply queues following buses behind the stop.
- Once a bus is admitted, it remains stopped at that exact queue position, even if space opens ahead. A follower uses the game's dwell, waiting-distance, and onboard-passenger readiness checks; only after all three finish does the mod advance a synthetic session to a validated next route waypoint. A session begun by vehicle AI stays on the native completion path.
- Bus boarding zones are learned from approaching buses and shown as a translucent blue overlay on the road at the stop. A bus within 40 m of its target waypoint makes its actual current lane authoritative; that physical observation remains cached over inferred route lanes. This keeps paired pull-in stops on their own side while preventing a distant junction or driveway from moving the zone.
- The game options page defaults **Only show the selected stop** on, hiding zone overlays until a bus stop is selected; map editing always keeps the active zone visible. Existing saved preferences are preserved.
- Select any served bus stop to open **Bus Boarding Zone** below the transit-lines section. Before a bus arrives, the initial zone is inferred from that stop's connected route waypoint; a nearby bus later confirms the exact physical driven lane. The **Length** slider sizes the rearward zone from 6 m to 200 m. **Edit on map** focuses the camera on the stop and adds cyan handles at the rear edge; the stop-side edge remains fixed. The rear edge follows the line's native inbound lane path across ordinary road-segment and junction boundaries instead of stopping at the first lane curve. Right-click or press Escape to leave map editing. The edited length is saved on that stop with the city; **Use automatic** removes the override. Existing saves retain their serialized offset field for compatibility, but it is intentionally ignored.
- A customized zone replaces automatic pull-in detection and the ordinary two-bus cap for that stop. Every bus whose centre is inside the edited zone and whose speed is at most 1.0 m/s may board concurrently. Editing the zone affects new admissions only; a bus already boarding remains held until its native session finishes.
- Passenger waiting positions remain native while this simplified boarding path is calibrated.
- Other public transport types and cargo vehicles retain the vanilla behaviour.

The mod keeps the game's passenger-transfer system in charge of route checks, capacity, fares, animations, and passenger buffers. It tracks naturally stopped buses independently of the stop's single native vehicle slot. One admitted bus owns that slot during each vehicle-AI tick, then every admitted bus is restored to boarding before resident AI and exposed to waiting residents in round-robin order. Native and synthetic sessions are tracked separately: only a native session is exposed to native completion, while a synthetic follower uses the equivalent readiness gate and a fully validated next route waypoint. Managed state is released without restoring the route when the bus is returning, disabled, out of control, reassigned, or no longer targets a live waypoint on that route.

## Build

The official modding toolchain and game assemblies come from a local Cities: Skylines II installation and are not included in this repository.

```powershell
Set-Location ConcurrentBusBoarding.UI
npm ci
npm test
Set-Location ..
dotnet build ConcurrentBusBoarding.slnx -c Release
```

Local diagnostic builds can retain the bounded crash breadcrumb log by adding
`-p:CbbDiagnostics=true`. Normal builds, including Paradox Mods releases, compile those calls out and perform no
breadcrumb file I/O.

The UI folder also includes a minimal Dockerfile for the portable `npm test` step. The managed Release still requires the locally installed official game toolchain and proprietary assemblies.

Run the dependency-free policy check with:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-policy.ps1
```

## Compatibility note

This mod integrates with internal ECS components from `Game.dll`. A game update can change those internals, so a Release build and an in-game bus-bay test should be repeated after each game update.

Implementation and gameplay-calibration details are recorded in [docs/HANDOVER.md](docs/HANDOVER.md).
