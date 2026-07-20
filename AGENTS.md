# Concurrent Bus Boarding contributor notes

This is a managed Cities: Skylines II code mod. Keep the implementation small and use the game's native ECS components and boarding flow.

## Local toolchain

- The official CS2 modding toolchain and proprietary game assemblies are supplied by an installed copy of the game; never commit or redistribute them.
- Build on Windows with `CSII_TOOLPATH` pointing to `Cities2_Data/Content/Game/.ModdingToolchain`.
- The toolchain's generated Unity project normally lives under the game's user-data `.cache/Modding` directory.
- Containers cannot legally or practically provide the proprietary inputs. Use a container only for portable checks that do not require the game.

## Checks

- Run `powershell -ExecutionPolicy Bypass -File scripts/test-policy.ps1` for the dependency-free policy check.
- Run `dotnet build ConcurrentBusBoarding.slnx -c Release` against the installed game before release.

