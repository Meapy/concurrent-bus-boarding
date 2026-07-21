# Concurrent Bus Boarding

A Cities: Skylines II code mod that lets more than one bus board at the same stop.

Install version 1.0.0 from [Paradox Mods](https://mods.paradoxplaza.com/mods/152153/Windows).

## Intended behaviour

- Ordinary curbside stops allow up to two stopped buses within the 26 m zone behind the native stop position. Before boarding, every bus is assigned the furthest available front-to-back position that keeps its body inside the zone; pull-in and edited zones use the same packing rule across their usable length.
- Pull-in stops are detected generically from the resolved and route lane entities: native secondary-lane metadata identifies purpose-built bays, a route that changes lanes within the same road edge identifies an inset stop lane, and split-and-merge topology identifies branch-and-rejoin bays. Their full physical lane is the boarding zone, with bus count limited by usable lane length.
- A bus may join concurrent boarding when its centre is in the local boarding zone and its speed is at most 1.0 m/s. Native traffic AI provides collision spacing; the lane projection has a 2 m tolerance.
- Buses keep the game's native lane direction, collision avoidance, and transforms. Before boarding starts, the mod may move a bus's same-physical-lane endpoint only to its forward packed position. A strictly-ahead check prevents turning around; when neither the final navigation entry nor current physical lane is safely writable, the bus keeps its native stop position and remains eligible to board.
- Once a bus is admitted, it remains stopped at that exact queue position while boarding, even if space opens ahead. When its native boarding session finishes, the hold is removed before navigation and it proceeds to the next stop normally.
- Bus boarding zones are learned from approaching buses and shown as a translucent blue overlay on the road at the stop. A bus within 40 m of its target waypoint makes its actual current lane authoritative; that physical observation remains cached over inferred route lanes. This keeps paired pull-in stops on their own side while preventing a distant junction or driveway from moving the zone.
- The game options page defaults **Only show the selected stop** on, hiding zone overlays until a bus stop is selected; map editing always keeps the active zone visible. Existing saved preferences are preserved.
- Select any served bus stop to open **Bus Boarding Zone** below the transit-lines section. Before a bus arrives, the initial zone is inferred from that stop's connected route waypoint; a nearby bus later confirms the exact physical driven lane. The **Position** slider moves the zone along the lane (negative is behind the native stop marker), and **Length** sizes it from 6 m to 200 m. **Edit on map** focuses the camera on the stop and adds four cyan corner handles for resizing either end plus a white centre handle for moving the complete zone along its lane. Right-click or press Escape to leave map editing. The edited values are saved on that stop with the city; **Use automatic** removes the override.
- A customized zone replaces automatic pull-in detection and the ordinary two-bus cap for that stop. Every bus whose centre is inside the edited zone and whose speed is at most 1.0 m/s may board concurrently. Editing the zone affects new admissions only; a bus already boarding remains held until its native session finishes.
- Waiting passengers use the complete automatic or customized boarding-zone length instead of clustering around the stop marker. Their native sidewalk queue areas are distributed deterministically along the zone with a bias toward its forward end, placing more passengers near the first buses while retaining access for buses behind.
- Other public transport types and cargo vehicles retain the vanilla behaviour.

The mod keeps the game's passenger-transfer system in charge of route checks, capacity, fares, animations, and passenger buffers. It tracks naturally stopped buses independently of the stop's single native vehicle slot. One admitted bus owns that slot during each vehicle-AI tick, then every admitted bus is restored to boarding before resident AI and exposed to waiting residents in round-robin order. Every admitted bus is physically held until native end-boarding clears the managed state; only then can it navigate to its next stop.

## Build

The official modding toolchain and game assemblies come from a local Cities: Skylines II installation and are not included in this repository.

```powershell
Set-Location ConcurrentBusBoarding.UI
npm ci
npm test
Set-Location ..
dotnet build ConcurrentBusBoarding.slnx -c Release
```

The UI folder also includes a minimal Dockerfile for the portable `npm test` step. The managed Release still requires the locally installed official game toolchain and proprietary assemblies.

Run the dependency-free policy check with:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-policy.ps1
```

## Compatibility note

This mod integrates with internal ECS components from `Game.dll`. A game update can change those internals, so a Release build and an in-game bus-bay test should be repeated after each game update.

Implementation and gameplay-calibration details are recorded in [docs/HANDOVER.md](docs/HANDOVER.md).
