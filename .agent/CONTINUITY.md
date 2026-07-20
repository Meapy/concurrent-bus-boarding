[PLANS]
- 2026-07-20T20:59:36+01:00 [USER] Initialize a new Cities: Skylines II code-mod repository that lets multiple buses board concurrently at a stop when space allows; use `C:\Users\dkras\source\repos\Fix-Signatures` as the repository-shape reference; create and push a GitHub repository.
- 2026-07-20T20:59:36+01:00 [ASSUMPTION] Implement the smallest managed ECS intervention supported by the installed game assemblies, then verify a Release package before publishing source to GitHub.

[DECISIONS]
- 2026-07-20T20:59:36+01:00 [TOOL] Use the installed CS2 modding toolchain at `F:\SteamLibrary\steamapps\common\Cities Skylines II\Cities2_Data\Content\Game\.ModdingToolchain` and managed assemblies at `F:\SteamLibrary\steamapps\common\Cities Skylines II\Cities2_Data\Managed`.
- 2026-07-20T20:59:36+01:00 [TOOL] Follow the reference repository's minimal managed-mod layout; no UI is currently required by the feature.
- 2026-07-20T21:32:00+01:00 [USER] Treat a detected pull-in/slip lane as the full boarding zone, visually distinguish the road zone, and require a bus to be fully inside it before boarding; cap all other bus stops at two concurrent buses.
- 2026-07-20T21:32:00+01:00 [CODE] Use the game's projected overlay renderer for a translucent lane marking; do not mutate road prefabs or lane colors.

[PROGRESS]
- 2026-07-20T20:59:36+01:00 [TOOL] Confirmed the workspace is an empty Git repository on unborn `master`; inspected the CS2 skill guidance and reference mod structure.

[DISCOVERIES]
- 2026-07-20T20:59:36+01:00 [TOOL] Installed `Game.dll` contains `Game.Routes.BoardingVehicleSystem`, `Game.Routes.BoardingVehicle`, `Game.Simulation.TransportBoardingHelpers`, and a nested `TransportBoardingHelpers.Concurrent`; exact producer/consumer behavior remains under inspection.
- 2026-07-20T21:32:00+01:00 [TOOL] `Game.Routes.BoardingVehicle` contains one boarding and one testing vehicle entity per stop. Native boarding rejects a second actively boarding vehicle, so controlled slot rotation is the narrow intervention point.
- 2026-07-20T21:32:00+01:00 [TOOL] Stop waypoints expose `AccessLane`; pull-in lanes can be conservatively identified by `SecondaryLane` or secondary `CarLaneFlags`, and `OverlayRenderSystem` supports colored projected curves.

[OUTCOMES]
- 2026-07-20T20:59:36+01:00 [TOOL] No implementation or remote publication completed yet.
