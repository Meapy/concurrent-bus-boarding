# Concurrent Bus Boarding

A Cities: Skylines II code mod that lets several buses safely use an extended bus-stop zone while preserving the game's native passenger-boarding lifecycle.

Install the latest release from [Paradox Mods](https://mods.paradoxplaza.com/mods/152153/Windows).
Questions, feedback, and bug reports are welcome in the
[Concurrent Bus Boarding forum thread](https://forum.paradoxplaza.com/forum/threads/mod-concurrent-bus-boarding-allow-several-buses-use-the-same-stop-at-the-same-time.1935925/).

## Intended behaviour

- The native bus-stop position is always the forward edge of the boarding zone. Ordinary curbside zones extend 26 m backward; edited zones extend 6–200 m backward, and can never be moved ahead of the stop.
- Pull-in stops are detected generically from the resolved and route lane entities: native secondary-lane metadata identifies purpose-built bays, a route that changes lanes within the same road edge identifies an inset stop lane, and split-and-merge topology identifies branch-and-rejoin bays. Their automatic zone uses the available physical lane behind the stop, with bus count limited by usable length.
- A bus may use the local boarding zone when its centre is in the zone. Native traffic AI provides collision spacing and the one passenger-facing owner required by the game.
- An eligible bus already inside its target stop's zone receives the game's native `RequireStop` request before vehicle AI advances the route waypoint. This closes the resident-update timing gap that could let even an empty first bus pass waiting passengers; native AI still owns braking and first-bus boarding.
- Buses keep the game's native lane direction, collision avoidance, transforms, current-lane occupancy, and navigation endpoints. The mod never moves a bus toward a synthetic packed position; native traffic simply queues following buses behind the stop.
- CBB never creates a synthetic passenger session. The game owns dwell time, waiting-distance checks, passenger readiness, stop-slot ownership, and next-waypoint advancement for every bus.
- Bus boarding zones are learned from approaching buses and shown as a translucent overlay on the road at the stop. The game options page provides the game's native colour wheel for the global overlay colour plus a 5–60% opacity slider, defaulting to a more transparent 18%. A bus within 40 m of its target waypoint makes its actual current lane authoritative; that physical observation remains cached over inferred route lanes. This keeps paired pull-in stops on their own side while preventing a distant junction or driveway from moving the zone.
- The game options page provides a **Public transport attractiveness** slider from 50% to 200%. The default 100% preserves vanilla route costs; higher values make residents more likely to choose passenger public transport. Changes affect newly planned or recalculated trips.
- The game options page defaults **Only show the selected stop** on, hiding zone overlays until a bus stop is selected; map editing always keeps the active zone visible. Existing saved preferences are preserved.
- The game options page also provides a confirmed **Reset all customized zones** action. It removes every saved per-stop zone length in the current city and returns those stops to automatic sizing.
- On city load, the mod rebuilds every existing bus stop's native pathfinding connections once while retaining attached lines. The confirmed **Reset all bus stops** action repeats that repair on demand.
- A separate confirmed **Reset all stop overlay colours** action returns every served stop to its line colour without changing customized zone lengths.
- Select any served bus stop to open **Bus Boarding Zone** below the transit-lines section. Before a bus arrives, the initial zone is inferred from that stop's connected route waypoint; a nearby bus later confirms the exact physical driven lane. The **Length** slider sizes the rearward zone from 6 m to 200 m. Served stops use their native line colour by default. Choose **This stop** or **Whole line**, then use the adjacent colour wheel to save a custom RGB value for that scope. A stop-specific value wins over an inherited line value. For an individual stop, the compact **Global / Line colour** selector switches between the Options colour and its line colour; all sources share the global opacity slider. **Edit on map** focuses the camera on the stop and adds cyan handles at the rear edge; the stop-side edge remains fixed. The rear edge follows the line's native inbound lane path across ordinary road-segment and junction boundaries instead of stopping at the first lane curve. Right-click or press Escape to leave map editing. The edited length and colour choices are saved with the city; **Use automatic** removes only the length override. Existing saves retain their serialized offset field for compatibility, but it is intentionally ignored.
- A customized zone replaces automatic pull-in detection and the ordinary two-bus cap for stop-request admission. Every bus whose centre is inside the edited zone may receive a native stop request; native AI remains the sole authority on passenger boarding.
- Passenger waiting positions remain native while this simplified boarding path is calibrated.
- The attractiveness slider applies to every passenger public transport type. Bus starting cost also receives a fixed 1.25× attractiveness multiplier, so its native base cost is 80% of vanilla before the global slider is applied. Cargo transport retains vanilla route costs.

The mod keeps the game's passenger-transfer system in charge of route checks, capacity, fares, animations, passenger buffers, stop-slot ownership, timing, and route advancement. CBB evaluates only safe zone capacity and asks native vehicle AI to stop eligible buses. This avoids the old synthetic follower lifecycle, which could overwrite the stop's single native passenger owner and make residents replan away from buses over time.

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

All Aboard 0.1.13 is supported. Concurrent Bus Boarding binds to All Aboard's replacement bus AI after mod loading completes and applies its configured maximum bus dwell time to managed follower buses.

Implementation and gameplay-calibration details are recorded in [docs/HANDOVER.md](docs/HANDOVER.md).
