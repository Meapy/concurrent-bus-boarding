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

## UI

- `ConcurrentBusBoarding.mjs` is the whole frontend. The game registers only a UI module's `.mjs` as a
  UI mod location, so anything emitted beside it is served but never loaded; the stylesheet is bundled
  into the `.mjs` and injected as a `<style>` element at registration. Do not reintroduce
  `mini-css-extract-plugin` or any other separate asset the game is assumed to pick up.
- The UI and managed halves deploy independently, so run the UI build before the managed build and
  treat a UI build failure as blocking, or a stale bundle ships beside a fresh assembly in silence.
- See `.agent/ui-notes.md` for the findings behind both rules.
