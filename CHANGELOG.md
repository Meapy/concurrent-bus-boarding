# Changelog

## 1.3.0 - 2026-07-25

- Add a colour wheel for the global boarding-zone overlay colour.
- Use each served bus stop's first line colour by default while retaining global transparency.
- Add a separate confirmed action that resets all per-stop colour choices without changing customized zone lengths.
- Persist the global colour as primitive RGBA channels so the Options page saves reliably and existing settings cannot make overlays transparent.
- Show the native game colour wheel both in Options and beside a selected stop's Global/Line colour choice.
- Separate overlay opacity from RGB so the colour wheel cannot force zones opaque; default opacity is now 18% and Options provides a 5–60% slider.
- Let the selected-stop colour wheel save a custom colour either for that stop or for its whole first served line.

## 1.2.0 - 2026-07-23

- Show mod-reported errors in the in-game error UI.
- Keep synthetic follower sessions out of native boarding completion unless vehicle AI explicitly adopts them.
- Stop managed boarding and route restoration when a bus retires, changes route, loses its target, or references stale route data.
- Validate both current and next route waypoints before changing a completed follower's target.
- Remove deleted stops from the boarding-zone render cache and reject stale or non-finite lane geometry before drawing.
- Add a confirmed settings action that resets every customized zone in the current city to automatic sizing.
- Keep the stop's passenger-facing bus fixed for a complete resident update sweep so passengers retry buses behind a full lead bus.
- Preserve the native boarding lifecycle for the first bus while managed concurrent boarding remains reserved for followers.
- Request the native stop signal for eligible buses inside their target zone so they cannot skip waiting passengers and advance to the next stop.

## 1.1.0 — 2026-07-22

- Allow stopped buses within the same boarding zone to board and unload concurrently.
- Keep each bus stationary until its own dwell and passenger transfers finish, even after the bus ahead departs.
- Advance completed follower buses directly to their next route waypoint instead of making them crawl toward the old stop.
- Anchor every zone at the stop and extend it backward along connected inbound lane segments.
- Preserve the detected physical pull-in lane and prevent inferred main-road or driveway overlays from replacing it.
- Keep automatic curbside stops at two buses; size automatic pull-ins by usable lane length; allow all contained buses in customized zones.
- Default overlays to the selected stop and retain the in-map rear-edge zone editor.
- Remove temporary crash breadcrumb logging from release builds.
- Add the official Paradox Forums support and feedback thread to the mod listing.

## 1.0.1 — 2026-07-21

- Improved boarding-zone overlays and per-stop editing.
- Added selected-stop-only overlay settings and Paradox Mods media.
- Removed unsafe physical bus repositioning that could cause reversing.

## 1.0.0 — 2026-07-21

- Initial public release.
