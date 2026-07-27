# Changelog

## 1.5.0 - 2026-07-27

Buses could get stuck at stops and stay there until their dwell limit expired, which inflated
journey times across every line. This release fixes that and greatly narrows when the mod
intervenes at all.

Note: this does not claim to fix declining passenger numbers. Testing with the mod fully
disabled showed the same decline, so that has a cause outside this mod.

- Fix buses becoming stuck at a stop until their dwell limit expired. A concurrently boarding bus could keep its native boarding session re-armed indefinitely and never depart.
- Keep a native boarding session continuously visible to the vehicle AI instead of hiding it between rotations.
- Hold the passenger-facing stop slot for a whole vehicle-AI tick so a boarding bus can actually complete and leave.
- Release any concurrent boarding session that exceeds the configured dwell limit, whatever state it is in.
- Let a following bus finish boarding on its own passenger and dwell gates. A follower is held short of the native stop marker, so the native lifecycle could never complete it and it previously sat at the stop until the dwell limit expired.
- Let a following bus accept passengers from the moment it is admitted rather than only from its first completion attempt.
- Repair cities saved with an earlier version. Stops still reserved for a bus that has gone, and buses left unable to accept passengers or depart, are cleared automatically each time a city loads, and on demand from a new Options button.
- Leave a lone bus at a stop entirely to the game. Concurrent boarding now engages only when two or more buses are actually at the same stop, which is the situation it exists to resolve. Previously every bus at every stop was taken over and held, replacing a short native dwell with a longer managed one and inflating journey times across every line.
- Close a boarding bus's doors before requiring every passenger to be aboard. At a busy stop the arrival stream never stops on its own, so the bus kept accepting new boarders, always had someone still climbing aboard, and could never depart until its dwell limit expired.
- Leave waiting cims where the game puts them. Displacing them along the boarding zone had no measured benefit and coincided with cims abandoning the wait before their bus arrived.
- Stop stranding cims halfway aboard. Rotating the shared stop slot away from a bus while a cim was still climbing into it left that cim unable to finish and its bus unable to depart until the dwell limit expired. The slot now waits for boarding cims to finish before it moves on.
- Let each concurrently boarding bus finish on its own passenger exchange instead of waiting for the whole shared stop queue to fall quiet, which at a busy stop could never happen and held the bus until the dwell limit.
- Recompute public transport attractiveness from the true vanilla baseline when the slider changes after new lines are loaded.
- Log active concurrent-boarding session counts and ages periodically to make this class of problem visible in the game log.

## 1.4.2 - 2026-07-26

- Fix the public transport attractiveness slider so it applies after native passenger pathfind data becomes available.
- Refresh passenger route costs before residents plan paths and log the number of active transport cost profiles.

## 1.4.1 - 2026-07-26

- Integrate with All Aboard 0.1.13 even when it loads after Concurrent Bus Boarding.
- Apply All Aboard's configured maximum bus dwell time to managed follower buses.
- Let a managed follower leave after that dwell limit when a stale passenger-ready flag would otherwise keep it stuck in Boarding.

## 1.4.0 - 2026-07-25

- Add a 50–200% public transport attractiveness slider; 100% preserves vanilla passenger-route costs.
- Initialize the slider at 100% for existing settings files that predate the option.

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
