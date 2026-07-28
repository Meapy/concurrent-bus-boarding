# Changelog

## 1.6.0 - 2026-07-28

Bus lines no longer lose their passengers over a long session.

The cause was that holding a bus at a stop is counted by the game as part of that line's travel
time, which is what it uses to decide whether residents choose the line at all. Boarding itself
always worked, so the buses looked fine while fewer and fewer people were routed to the stops.
Every concurrent boarding session now gives that time back, and **Repair stuck bus stops**
clears the history a damaged city has already accumulated.

- Give back the line time consumed while holding a bus, so a line the mod helps is costed as if it had not been held.
- Concurrent boarding is on by default again. Cities where 1.5.1 to 1.5.3 switched it off automatically get it switched back on once, since the reason for disabling it is fixed; turn it off in Options if you prefer, and that choice is kept.
- The first city loaded after updating has its stop service history cleared automatically, so lines already damaged by an earlier version recover on their own.
- **Repair stuck bus stops** now also clears each stop's recorded service history and forces the line to be re-costed, so a city degraded by an earlier version recovers instead of staying unpopular. It acts immediately rather than gradually, and can be used on any other city.
- Release stops reserved by a bus that has been removed or replaced, continuously rather than only when a city loads.
- Report bus ridership, waiting passengers and per-stop service to the log, and add a **Write bus line report to log** action, to make this class of problem visible rather than invisible.
- Further reduce the mod's cost. Boarding-zone geometry is no longer rebuilt for every bus in the city when no zone is on screen, which is the normal case with the default overlay setting; the diagnostic systems no longer run every frame; and the whole-city passenger scan now runs only when a report is requested rather than on a timer.

With thanks to **CheeseBunny_Gaming**, **Eiden3000** and **Minimumderp** for reporting the
problem and sticking with the diagnosis.

## 1.5.3 - 2026-07-27

- Greatly reduce the mod's simulation cost. Boarding-zone geometry was rebuilt for every bus in the city several times a second, walking each route's segment and path-element buffers; it is now resolved only for stops where two or more buses are actually present, and the working collections are reused instead of reallocated every update. On a large city this was enough to make the simulation stutter, and the critical error reported in 1.5.0 and 1.5.2 was the game reacting to that stutter rather than a fault of its own.

## 1.5.2 - 2026-07-27

- Fix a critical simulation error introduced in 1.5.0. The stuck-stop repair and the line report ran in the wrong update phase and changed entities while the game was mid-simulation, which stopped the game obtaining an internal command buffer.

## 1.5.1 - 2026-07-27

- Concurrent boarding is now off by default and must be switched on in Options. A bus held at a stop takes a little longer than the game's own boarding, and the game uses stop waiting times when residents choose a route, so on a busy network it can cost passengers.
- Updating from an earlier version switches it off once, even if it was previously on. Turn it back on in Options if you want it; the choice is then remembered.
- Fix residents gradually giving up on bus lines the mod was helping. Holding a bus at a stop was counted by the game as part of the line's travel time, which made the line look permanently slower and pushed residents onto other options, even though boarding itself worked fine. Each concurrent boarding session now gives that time back.
- **Repair stuck bus stops** also clears each stop's recorded service history, so a line degraded by an earlier version is costed as if newly built and residents start choosing it again.
- **Repair stuck bus stops** in Options now frees every blocked stop at once instead of recovering gradually. Use it if a line has stopped carrying passengers and it should pick up again straight away.
- Everything else works either way: the boarding-zone overlays, the per-stop zone editor, the stuck-stop repair, and the fixes from 1.5.0.

## 1.5.0 - 2026-07-27

Fixes bus lines steadily losing their passengers over a long session. Several separate faults
combined so that stops quietly stopped serving anyone and buses spent far too long parked, and
because the game uses stop waiting times when residents choose a route, whole lines emptied.

- Fix bus lines losing all their passengers over time. When a bus finished boarding and moved on, the stop was left reserved for it. The game refuses to start boarding at a stop reserved by another bus that is still boarding elsewhere, so that stop could never board anyone again, and lines died stop by stop until nobody could use them.
- Add an extra bus attractiveness setting, 100-200% and defaulting to 100%, that makes residents prefer buses specifically. It scales only route costs used exclusively by bus lines, so trams, trains and ferries are unaffected, and it stacks with the existing public transport setting. At the 100% default nothing is changed.
- Fix buses becoming stuck at a stop until their dwell limit expired. A concurrently boarding bus could keep its native boarding session re-armed indefinitely and never depart.
- Keep a native boarding session continuously visible to the vehicle AI instead of hiding it between rotations.
- Hold the passenger-facing stop slot for a whole vehicle-AI tick so a boarding bus can actually complete and leave.
- Release any concurrent boarding session that exceeds the configured dwell limit, whatever state it is in.
- Let a following bus finish boarding on its own passenger and dwell gates. A follower is held short of the native stop marker, so the native lifecycle could never complete it and it previously sat at the stop until the dwell limit expired.
- Let a following bus accept passengers from the moment it is admitted rather than only from its first completion attempt.
- Continuously release stops still reserved by a bus that is no longer there. A bus removed or replaced while boarding could leave its stop reserved forever, and the game will not start boarding at a reserved stop, so that stop quietly stopped serving anyone.
- Repair cities saved with an earlier version. Stops still reserved for a bus that has gone, and buses left unable to accept passengers or depart, are cleared automatically each time a city loads, and on demand from a new Options button.
- Leave a lone bus at a stop entirely to the game. Concurrent boarding now engages only when two or more buses are actually at the same stop, which is the situation it exists to resolve. Previously every bus at every stop was taken over and held, replacing a short native dwell with a longer managed one and inflating journey times across every line.
- Close a boarding bus's doors before requiring every passenger to be aboard. At a busy stop the arrival stream never stops on its own, so the bus kept accepting new boarders, always had someone still climbing aboard, and could never depart until its dwell limit expired.
- Leave waiting cims where the game puts them. Displacing them along the boarding zone had no measured benefit and coincided with cims abandoning the wait before their bus arrived.
- Stop stranding cims halfway aboard. Rotating the shared stop slot away from a bus while a cim was still climbing into it left that cim unable to finish and its bus unable to depart until the dwell limit expired. The slot now waits for boarding cims to finish before it moves on.
- Fix buses leaving stops empty while passengers waited. The game admits waiting passengers in waves, widening the boarding range a little at a time from zero. A managed bus was opening that range fully at once and then treating the first quiet moment as "boarding finished", so it could close its doors and depart before anyone had been admitted.
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
