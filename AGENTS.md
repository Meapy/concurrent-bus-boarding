# Concurrent Bus Boarding contributor notes

This is a managed Cities: Skylines II code mod. Keep the implementation small and use the game's native ECS components and boarding flow.

## Local toolchain

- The official CS2 modding toolchain and proprietary game assemblies are supplied by an installed copy of the game; never commit or redistribute them.
- Build on Windows with `CSII_TOOLPATH` pointing to `Cities2_Data/Content/Game/.ModdingToolchain`.
- The toolchain's generated Unity project normally lives under the game's user-data `.cache/Modding` directory.
- Containers cannot legally or practically provide the proprietary inputs. Use a container only for portable checks that do not require the game.

## Checks

- Run `powershell -ExecutionPolicy Bypass -File scripts/test-policy.ps1` for the dependency-free policy check.
- Run `npm ci` and `npm test` in `ConcurrentBusBoarding.UI` for the production UI bundle and smoke check (or use its Dockerfile).
- Run `dotnet build ConcurrentBusBoarding.slnx -c Release` against the installed game before release.

## BoardingPolicy.cs

`scripts/test-policy.ps1` compiles this one file on its own with the .NET Framework 4.0 `csc.exe`,
against `tests/BoardingPolicyCheck.cs`, so the policy rules can be checked without the game
assemblies. It must therefore stay dependency-free and within C# 5: no `using` directives, no
expression-bodied members, no `Unity.Mathematics`. Any of those builds fine in the real project and
fails only in the policy check, which is what makes it easy to walk into. A guard in the script now
rejects all three.

## Simulation systems

- No `EntityManager` data access or structural change on a per-frame path inside `GameSimulation`. Every
  such call completes the scheduled jobs writing that component before it returns, so the main thread waits
  for the simulation once per simulation frame — that, not the mod's own logic, was 86% of its cost. Read and
  write through `ComponentLookup`/`BufferLookup` in a job scheduled on `Dependency`. `scripts/test-policy.ps1`
  enforces this between `ConcurrentBoardingSystem` and `BoardingHelpers`.
- Session state is enabled and disabled, never added and removed. A command buffer is not an alternative on a
  per-simulation-frame path: barriers play back once per *rendered* frame, and the simulation runs up to 6
  steps inside one. `BoardingStateProvisionSystem` provisions the components in Modification1.
- Shared stop and zone resolution is generic over `IBoardingAccess` so the jobs and the main-thread overlay
  run the same code. Do not fork it: the main-thread callers cannot use lookups, because a lookup used there
  needs a job completion even to test for a component.

## UI

- `ConcurrentBusBoarding.mjs` is the whole frontend. The game registers only a UI module's `.mjs` as a
  UI mod location, so anything emitted beside it is served but never loaded; the stylesheet is bundled
  into the `.mjs` and injected as a `<style>` element at registration. Do not reintroduce
  `mini-css-extract-plugin` or any other separate asset the game is assumed to pick up.
- The UI and managed halves deploy independently, so run the UI build before the managed build and
  treat a UI build failure as blocking, or a stale bundle ships beside a fresh assembly in silence.
- See `.agent/ui-notes.md` for the findings behind both rules.
