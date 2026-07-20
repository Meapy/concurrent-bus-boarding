# Concurrent Bus Boarding

A Cities: Skylines II code mod that lets more than one bus board at the same stop.

## Intended behaviour

- Ordinary curbside stops allow at most two buses to board concurrently.
- Stops whose access lane is a secondary pull-in lane use the full lane as their boarding zone; the number of buses is limited by the usable lane length.
- A bus must be completely inside a pull-in lane before it can join concurrent boarding.
- Boarding zones are shown as a translucent blue road overlay.
- Other public transport types and cargo vehicles retain the vanilla behaviour.

The mod deliberately keeps the game's passenger-transfer and vehicle-AI systems in charge. It only relaxes and rotates the stop's single vanilla boarding-vehicle slot.

## Build

The official modding toolchain and game assemblies come from a local Cities: Skylines II installation and are not included in this repository.

```powershell
$env:CSII_TOOLPATH = 'F:\SteamLibrary\steamapps\common\Cities Skylines II\Cities2_Data\Content\Game\.ModdingToolchain'
dotnet build ConcurrentBusBoarding.slnx -c Release
```

Run the dependency-free policy check with:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-policy.ps1
```

## Compatibility note

This mod integrates with internal ECS components from `Game.dll`. A game update can change those internals, so a Release build and an in-game bus-bay test should be repeated after each game update.

