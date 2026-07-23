# Changelog

## Unreleased

- Show mod-reported errors in the in-game error UI.
- Keep synthetic follower sessions out of native boarding completion unless vehicle AI explicitly adopts them.
- Stop managed boarding and route restoration when a bus retires, changes route, loses its target, or references stale route data.
- Validate both current and next route waypoints before changing a completed follower's target.
- Remove deleted stops from the boarding-zone render cache and reject stale or non-finite lane geometry before drawing.
- Add a confirmed settings action that resets every customized zone in the current city to automatic sizing.

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
