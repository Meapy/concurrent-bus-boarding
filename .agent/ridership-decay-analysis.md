# Ridership decay investigation

Symptom: over a long session with Concurrent Bus Boarding active, cims progressively stop
choosing buses until bus usage is effectively zero. Reproduced by the maintainer.

Status: static analysis of the mod source plus the recorded installed-IL findings in
`.agent/CONTINUITY.md`. Not yet confirmed against a live run or a fresh `Game.dll` trace.

## What actually runs

`Mod.OnLoad` + `Mod.RegisterBoardingSystems` register only these simulation systems:

- `ConcurrentBoardingSystem` — every 16 frames, before native/All Aboard car AI
- `RouteHandoffSystem` — every 16 frames, after car AI
- `PassengerDistributionSystem` — **every frame**, after car AI
- `BoardingHoldSystem` — **every frame**, after `CarNavigationSystem`
- `PublicTransportAttractivenessSystem` — before `ResidentAISystem`

`BoardingZoneApproachSystem` and `PassengerWaitingSpreadSystem` are compiled but never
registered. They are dead code and cannot contribute to the bug. (Worth deleting or
marking, because they are misleading during triage.)

Only two entities are mutated in a way that can affect ridership: each bus's
`PublicTransport.m_State` / dwell fields, and each stop's `BoardingVehicle` slot.

## Hypothesis 1 (primary): `Boarding` flag toggling relatches native boarding forever

`ConcurrentBoardingSystem.PrepareForVehicleAi` runs for every active bus before car AI:

```csharp
if (BoardingPolicy.ShouldExposeBoardingToVehicleAi(active.UsesNativeBoarding != 0, selected))
    transport.m_State |= PublicTransportFlags.Boarding;
else
    transport.m_State &= ~PublicTransportFlags.Boarding;
```

`ShouldExposeBoardingToVehicleAi` is `usesNativeBoarding && selected`. `selected` is one bus
per stop per rotation. So **every native-boarding bus that is not this tick's rotation winner
has its `Boarding` bit cleared immediately before `TransportCarAISystem` runs**, and
`PassengerDistributionSystem` sets it back immediately after.

`CONTINUITY.md` records, from installed 1.6.0 IL:

- `StartBoarding` initialises departure/distance state and enqueues
  `BoardingData.BeginBoarding`.
- `StopBoarding` tests the stop's single `BoardingVehicle.m_Vehicle`.
- Native `StopBoarding` is reached only after `PathEndReached`/parking completion.

A stationary bus at its target stop with `Boarding` cleared is, from the car AI's point of
view, a bus that has arrived and has not started boarding. The expected consequence is that
`StartBoarding` is re-entered on the next car-AI tick, which:

1. enqueues another `BoardingData.BeginBoarding` with no matching `EndBoarding` — the mod
   deliberately never enqueues `EndBoarding` (2026-07-22T21:21 decision); and
2. **re-initialises `m_DepartureFrame` to a fresh future frame.**

(2) is the decay engine. For a native session the mod never applies its own timeout — release
depends on native AI clearing `Boarding`, which requires `StopBoarding`, which requires
`frame >= m_DepartureFrame`. If `m_DepartureFrame` is pushed forward on every car-AI tick,
that condition is never met. The bus keeps `ConcurrentBoardingActive`, `BoardingHoldSystem`
keeps its speed and velocity at zero every frame, and it never leaves the stop.

This is monotonic and irreversible per bus: a bus only has to lose the rotation coin-flip at
the wrong moment once. The population of permanently parked buses grows with session length.
As lines lose their moving vehicles, service collapses, and CS2's transit path costs make
buses an unattractive choice for everyone — ridership trends to zero rather than to a new
equilibrium.

This matches the maintainer-reported history: "buses still remain stuck in Boarding with
Concurrent Bus Boarding and All Aboard enabled" (2026-07-26T00:08), and the earlier
"repeated native boarding-admission loop" (2026-07-22T18:17), which was addressed with a
per-visit attempt guard that was later removed.

Aggravating factor: `RotationIndex` for the vehicle-AI selection uses
`ConcurrentBoardingSystem.m_Turn`, while the passenger pointer uses
`frameIndex / 16`. These are two independent counters with independent phase. There is no
guarantee that a given bus holds `BoardingVehicle.m_Vehicle` on the same frame that the car
AI ticks, so even the "selected" bus may not satisfy `StopBoarding`'s slot test.

## Hypothesis 2 (contributing): dead first boarding window on synthetic sessions

`ConcurrentBoardingSystem.BeginBoarding`:

```csharp
transport.m_DepartureFrame = math.max(transport.m_DepartureFrame, m_SimulationSystem.frameIndex + 64u);
transport.m_MaxBoardingDistance = 0f;
transport.m_MinWaitingDistance = float.MaxValue;
```

Two problems.

`m_MaxBoardingDistance = 0f` means no waiting cim can board this bus at all. Native
`StartBoarding` opens the window (`float.MaxValue`) and `StopBoarding` ratchets it down; this
does the reverse. The window only opens on the first `TryCompleteBoarding` call, which is
gated on the bus winning the vehicle-AI rotation. Meanwhile the bus holds a share of the
stop's `BoardingVehicle.m_Vehicle` pointer via `PassengerDistributionSystem`, so cims are
routed to a bus that is structurally incapable of accepting them. This is pure lost boarding
throughput at every stop, every rotation, for the whole session.

`math.max(...)` means a stale future `m_DepartureFrame` left by native AI is preserved rather
than reset, which extends the dwell further. This was already identified once
(2026-07-21T23:10, "reset to frame +64 rather than preserving a stale future departure") but
the `math.max` is still present.

## Hypothesis 3 (contributing, not the cause): dwell inflation degrades transit path cost

Even with no bug, every managed session adds a minimum 64-frame dwell plus a full
waiting-distance ratchet, and `BoardingHoldSystem` pins the bus at zero speed for the whole
time. Longer dwell raises line round-trip time and encourages bunching. CS2 feeds line
timing into transit path costs, so this is a real, gradual downward pressure on ridership.
On its own it should reach a new equilibrium rather than zero, so it explains part of the
slope but not the floor.

## Hypothesis 4 (unlikely to be the cause, but a real bug)

`PublicTransportAttractivenessSystem.CaptureOriginalCosts` only inserts prefabs it has not
already seen, and it runs again on every slider change. Any passenger pathfind prefab that
first appears *after* a non-100% value has been applied is captured at its already-scaled
value and treated as the baseline. Repeated slider changes then compound against a drifting
baseline, and `OnDestroy` restores the wrong values. This cannot cause decay at the default
100% (the system no-ops), so it is not the reported bug, but it should be fixed:
restore-to-baseline before every re-capture.

## Round 1 test result (2026-07-26, in game)

```
15 active sessions, oldest 0 frames, 0 expired
12 active sessions, oldest 1504 frames, 8 expired
16 active sessions, oldest 1792 frames, 23 expired
 9 active sessions, oldest 1664 frames, 34 expired
18 active sessions, oldest 1328 frames, 47 expired
10 active sessions, oldest 1792 frames, 60 expired
 8 active sessions, oldest 1792 frames, 70 expired
```

Ridership still decayed. The telemetry reframes the problem:

- Active sessions are **bounded** (8-18, oscillating). The permanent-latch failure mode is
  gone, so the deadline works and Hypothesis 1's fix did something real.
- Oldest session age pegs at **~1792 frames**, just under the 1800-frame deadline, in almost
  every report.
- Expirations grow at a steady ~13 per 4096 frames, i.e. **essentially every session is
  ending by timeout rather than by completing.**

So sessions were never completing; the previous build merely converted a permanent stall
into a 1800-frame (~10 in-game-minute) stall at every stop. Round-trip times stay enormous,
lines still effectively do not run, and ridership still collapses. Same symptom, one layer
down.

### Why no session completes

`CONTINUITY.md` (2026-07-22T19:54, from installed IL): native `StopBoarding` is reached only
after `PathEndReached`/parking-space completion. `BoardingHoldSystem` pins every admitted bus
at zero velocity **behind** the native stop marker, so a follower never reaches its path end
and native `StopBoarding` can never run for it — regardless of its `Boarding` flag.

Meanwhile `ConcurrentBoardingSystem`'s first admission loop marks any bus already flagged
`Boarding` inside the zone as `UsesNativeBoarding = 1`, and `ShouldCompleteManagedBoarding`
was `!usesNativeBoarding && selected`. So those buses were excluded from managed completion
too. They had **no completion path at all** and could only die by timeout.

`UsesNativeBoarding` was being used to mean "was already Boarding when we found it", when the
property that actually matters is "native AI parked this bus at its own path end".

### Round 2 change

`ShouldCompleteManagedBoarding` now grants managed completion to any selected session once
the native lifecycle has had `NativeCompletionGraceFrames` (256) to finish on its own.
A genuine lead bus that did reach its endpoint still completes natively well inside that
window; a follower that never can now completes on the real passenger-ready and
waiting-distance gates instead of the deadline.

The health line now separates `native=` / `managed=` / `expired=` completions, which makes
the next run conclusive: expired should become a small minority.

## Round 2 test result (2026-07-26, in game)

Final line after ~10 minutes: `native=4 managed=109 expired=122`, `oldest` still 1200-1800.

Two things stand out.

**Managed completion now works** (109), so the round 2 gate change was correct and necessary.
But `expired` is still ~53% of terminations and `oldest` still runs near the deadline, so
dwell is still far too long and ridership still decays.

**`native=4` is the bigger finding.** Almost nothing completes through the native lifecycle,
including lead buses. `BoardingHoldSystem` freezes a bus the moment it is admitted, and
admission only requires the bus to be inside the zone, which extends *backward* from the stop
marker. So the lead is frozen before it reaches its own path end, `PathEndReached` never
fires, and native `StopBoarding` is unreachable for every bus, not just followers. The mod is
effectively running its own boarding lifecycle for the entire city while still depending on
native gates designed for a single bus per stop.

### Why the remaining sessions expire

`TryCompleteBoarding` inherits the native waiting-distance ratchet: it only completes on a
cycle where `m_MinWaitingDistance` came back as `float.MaxValue`, i.e. nobody was waiting
within range. That assumes one bus serves the whole queue. Here the passenger slot rotates
between concurrent buses, so each bus gets roughly 1/N of the boarding opportunity while its
ratchet still requires the *whole* queue to fall quiet. At a busy stop with continuous
arrivals that condition may simply never occur, and the session runs to the deadline.

### Round 3 change

Completion now also succeeds when the bus's **own** exchange has settled: if its passenger
count is unchanged across `IdleAttemptsBeforeDeparture` (3) consecutive selected attempts,
its share of the boarding is finished regardless of what the shared queue is doing. The
minimum dwell and the onboard passenger-ready gate still apply.

Per-gate counters were added (`dwell` / `distance` / `passengers` / `waypoint`) so if
expirations persist, the next log says which gate is responsible instead of requiring
another inference.

## Round 3 test result (2026-07-26, in game) - root cause found

Final line: `native=3 managed=46 expired=65; dwell=8665 distance=0 passengers=1362 waypoint=0`.

The gate counters are decisive and they falsify the round 3 hypothesis:

- **`distance=0`.** The waiting-distance ratchet never blocks anything. The busy-queue theory
  was wrong. The idle-exchange completion added in round 3 is harmless but was not the fix.
- **`waypoint=0`.** Waypoint advance never fails.
- **`dwell=8665`** is benign: most attempts simply land inside the minimum dwell window.
- **`passengers=1362`, growing steadily.** `ArePassengersReady` is the real blocker. Sessions
  that expire are sessions where an onboard passenger never gets `CreatureVehicleFlags.Ready`.

### Root cause

`ArePassengersReady` returns false while any passenger holds `CurrentVehicle` pointing at the
bus without the `Ready` flag - a cim that is mid-transition, climbing aboard. That cim can
only finish its transition while the stop's `BoardingVehicle.m_Vehicle` still points at the
bus it is boarding.

`PassengerDistributionSystem` rotates that slot between concurrent buses every tick. When the
slot rotates away from a bus with a cim mid-transition:

- the cim is stranded holding `CurrentVehicle` with `Ready` unset;
- the bus can never satisfy `ArePassengersReady`, so it never completes;
- it dwells until the 1800-frame deadline, then force-departs.

So the rotation - the mod's core mechanism for sharing one native stop slot between several
buses - was itself creating the stall. Every concurrent boarding event had a chance of
half-boarding a cim and pinning its bus for ten in-game minutes. Across a city that is
exactly the observed gradual collapse, and it also explains `native=3`: native completion
tests the same readiness.

### Round 4 change

Slot rotation is now sticky: `PassengerDistributionSystem` will not rotate away from a bus
whose passengers are not all `Ready`. The in-flight boarder finishes, the bus becomes
completable, and only then does the slot move on. The dwell deadline still bounds the hold,
so a genuinely stuck cim cannot starve the stop. A `sticky=` counter reports how often the
hold engages.

## Round 4 test result (2026-07-26, in game) - hypothesis falsified, cause identified

Final line: `native=3 managed=71 expired=81; dwell=11153 distance=0 passengers=1524 sticky=100651`.

`sticky` is enormous - the slot is pinned almost permanently - and `passengers` still grows at
exactly the previous rate. So the stranded cims are **not** waiting on the slot pointer.
Round 4's premise was wrong.

The decisive evidence was `distance=0`, which had been under-read for two rounds.
`m_BlockedByDistance` can only increment when `m_MaxBoardingDistance` came back finite, which
only happens when `ResidentAISystem` set `m_MinWaitingDistance` finite, which only happens
when a waiting cim was found **near that bus**. Zero across four sessions means no waiting cim
is ever within boarding range of a managed bus.

### Root cause

Waiting cims queue at the stop marker. `BoardingHoldSystem` freezes admitted buses wherever
they are inside the zone, which extends up to 26 m behind the marker on an ordinary stop and
up to 200 m on a custom zone. Follower buses are therefore parked where nobody can board
them. They exchange almost no passengers, strand the occasional cim that does get assigned
(the `passengers` counter), and still hold a full dwell before departing.

So each stop was taking 2+ buses out of service for a long dwell while only the bus at the
marker could actually work. Multiplied across every stop and line, that is the ridership
collapse - and it is a design gap, not a lifecycle bug. The earlier rounds fixed real defects
but none of them addressed this.

`PassengerWaitingSpreadSystem` was written precisely to solve it and has never been
registered since the 2026-07-22 crash-isolation rollback.

### Round 5 change

Two coupled changes, per user decision:

1. **Reachability gate.** `CanAdmit` now also requires the candidate to be within
   `PassengerReachDistance` (20 m) of the stop marker, for ordinary, pull-in and custom zones
   alike. A bus further back is left entirely to native behaviour rather than admitted and
   frozen. Reported as `out-of-reach=`.
2. **Passenger spread re-enabled.** `PassengerWaitingSpreadSystem` is registered again so
   waiting cims distribute along the zone toward the follower, front-biased. Their spread is
   clamped by `LimitWaitingBoundsToReach` to the same 20 m band, so the spread can never move
   cims outside the range in which a bus is allowed to be admitted.

The two are deliberately consistent: cims spread across exactly the band where buses may be
held, and no further. `PassengerReachDistance` is the single tuning knob for both.

## Round 5 test result (2026-07-26, in game) - and a correction to this analysis

Final line: `native=2 managed=48 expired=82; dwell=11809 distance=0 passengers=1407
sticky=94033 out-of-reach=841`.

The reach gate fires (841) and the spread is registered, yet nothing improved: `passengers`,
`expired` and `sticky` all grow at their previous rates.

### Correction: the distance counter was not trustworthy

Round 3 introduced the gate counters **and** `exchangeSettled` in the same build.
`exchangeSettled` makes the distance condition non-blocking in `CanFinishBoarding`, so
`m_BlockedByDistance` can essentially never increment regardless of cim positions. The
distance gate has therefore never been observed un-masked, and the round 5 reachability
argument was built on an instrument that a previous round had defanged. Recorded here so it
is not repeated: **do not reason from a counter introduced in the same build as a change that
alters the condition it measures.**

The reach gate and bounded spread are defensible on their own merits and are retained, but
they are not evidenced as the cause and did not fix the symptom.

### What survives

`passengers` and `sticky` are independent measurements that agree: cims acquire
`CurrentVehicle` pointing at a managed bus and never receive `CreatureVehicleFlags.Ready`.
`sticky` in particular counts only "the current slot holder has unready passengers", and it
is near-continuous, so the stall is persistent rather than transient. Why they never become
ready remains unproven.

### Untested foundational assumption

Five rounds have assumed this mod causes the ridership decay. That was never verified. The
bug was observed with the mod active; it has never been observed to stop when the mod's
simulation systems are inactive.

### Round 6: observer-only A/B

Added the `CbbObserverOnly` build property (`CBB_OBSERVER_ONLY`), mirroring the existing
`CbbDiagnostics` pattern. When set, `Mod.OnLoad` registers only the settings, zone tool,
overlay renderer and editor UI - no boarding, holding, passenger distribution, spread, route
handoff or transport attractiveness. A policy assertion refuses to validate an observer-only
build as a release package.

Run the same save with that package:

- ridership still decays -> this mod is not the cause and the target has been wrong;
- ridership stable -> causation confirmed and every later measurement becomes meaningful.

Only after that result is it worth fixing the instrumentation (measure each gate
independently, un-mask the distance gate) and resuming.

## Round 6 result: causation confirmed

Observer-only package on the same save: ridership "dropped a little, but a lot more steady".
The collapse-to-zero does not happen when the mod's simulation systems are unregistered, so
**this mod's simulation systems cause the decay**. The residual drift is consistent with
vanilla time-of-day demand cycles and is not treated as signal.

`PublicTransportAttractiveness` is confirmed at 100%, where the system returns before applying
anything. That feature is exonerated; the boarding systems own the bug.

## Round 7: measurement only, no behaviour change

Three hypotheses have now been falsified and one counter was compromised by the change
shipped alongside it. This round adds no behavioural change at all.

- Every completion gate is measured **independently** on every attempt, not as an if/else
  chain that only ever reported the first failure. `attempts` is reported so each gate can be
  read as a fraction.
- `distance` is now measured directly on `m_MaxBoardingDistance`, un-masked by
  `exchangeSettled`.
- `sessions that ever saw a waiting cim` answers the question the old distance counter was
  wrongly assumed to answer: does the resident AI ever report a waiting cim near a managed
  bus? Compare against `ended`.
- `boarded` / `alighted` measure whether managed sessions do any passenger work at all. If
  concurrent buses board nobody, the feature is a net loss regardless of mechanism.
- `unready passengers` and `of which pointing at another vehicle` test a specific suspicion:
  `ArePassengersReady` iterates the bus's `Passenger` buffer and rejects any unready
  `CurrentVehicle` **without checking that `CurrentVehicle.m_Vehicle` is this bus**. If the
  second number is a large share of the first, that omission is the stall, and the fix is
  one condition. This is deliberately measured rather than "fixed" on suspicion.

One small correctness change was unavoidable: `LastPassengerCount` is now initialised from
the real passenger count at admission. Previously it defaulted to 0, so a bus admitted with
passengers aboard recorded a false boarding delta on its first attempt.

### Reading the next log

- `sessions that ever saw a waiting cim` near zero while `ended` is large -> managed buses are
  never visible to waiting cims, and the problem is placement/visibility, not the lifecycle.
- `boarded` near zero -> concurrent buses do no work; the feature should be narrowed rather
  than repaired.
- `of which pointing at another vehicle` a large share of `unready passengers` -> the
  readiness check is wrong and the fix is small and specific.
- `distance` now large -> the ratchet does block after all, and round 3's masking hid it.

## Round 7 result: the mechanism, with evidence

Final line: `ended=321 (native=10 managed=91 expired=220); sessions that ever saw a waiting
cim=0; boarded=4846 alighted=1903; attempts=32444 dwell=28773 distance=0 passengers=12339
settled=29163 waypoint=0; unready passengers=71618 of which pointing at another vehicle=0`.

### Two hypotheses killed

- **`of which pointing at another vehicle = 0`.** The suspicion that `ArePassengersReady`
  ignores vehicle identity is wrong. Every unready passenger legitimately belongs to its bus.
  No fix needed there.
- **`boarded=4846, alighted=1903`.** Managed buses do plenty of passenger work. The
  "followers are unreachable and board nobody" theory from round 5 is dead. The reach gate and
  bounded spread are harmless and are retained, but they were solving a non-problem.

`sessions that ever saw a waiting cim = 0` also reads the opposite way to the round 5 guess:
`m_MinWaitingDistance` stays `float.MaxValue` because **no cim is ever left behind**, not
because none is ever near.

### The actual mechanism

Chain the surviving numbers together:

1. `m_MinWaitingDistance` is never finite (nobody left behind), so the ratchet
   `maxBoardingDistance = minWaiting + 1` never triggers and always yields `float.MaxValue`.
2. `distance=0` confirms it: **`m_MaxBoardingDistance` is permanently `float.MaxValue`. The
   bus never closes its doors.**
3. So the bus keeps admitting new boarders for as long as it sits there, and cims keep
   arriving (`boarded=4846`).
4. `ArePassengersReady` requires *every* passenger to be `Ready`. With a continuous arrival
   stream there is always someone mid-transition - about 5.8 unready per blocked attempt
   (`71618 / 12339`).
5. The readiness gate therefore never opens. `passengers` blocks 38% of attempts, `sticky` is
   near-continuous, and **69% of sessions (220/321) run to the 1800-frame deadline.**

Buses spend most of their lives parked at stops boarding an endless queue. Line round-trip
times explode, effective frequency collapses, and cims stop choosing buses. That is the decay.

Native code avoids this because `StopBoarding` ratchets `m_MaxBoardingDistance` *down* to shut
the window, which stops the stream so the last boarders can finish. The mod inherited the
formula but never the closing behaviour.

### Round 8 change: two-phase departure

Departure now works like a real stop.

- **Phase one** accepts passengers, as now.
- **Phase two** closes the doors: `m_MaxBoardingDistance = 0`, no new boarders admitted. Then
  the only remaining condition is that the cims already climbing aboard finish.

Doors close when the exchange settles (quiet stop) or when `BoardingWindowFrames` (512)
elapses since admission (busy stop) - well inside the 1800-frame deadline. Once closed, the
dwell and distance gates are not re-tested, since re-testing them is what reintroduces the
stall. Reported as `doors closed=`.

## Round 8 result: the boarding lifecycle is healthy

| Measure | Round 7 (~8 min) | Round 8 (~7 min) |
| --- | --- | --- |
| sessions ended | 321 | 296 |
| expired at deadline | **220 (69%)** | **0 (0%)** |
| oldest active session | 1200-1792 frames | 48-480 frames |
| concurrent active sessions | 9-18 | 2-8 |
| completion attempts | 32444 | 3328 |

`expired=0` across 296 sessions, not merely a small minority. Sessions now last a fraction of
the deadline instead of pegging against it, and buses stop accumulating at stops. The 10x drop
in `attempts` is a consequence: sessions are short, so far fewer attempts are needed.

`doors closed=307` against `ended=296` tracks one-to-one as predicted, the surplus being
sessions currently in the closing phase. `distance=38` is now non-zero, which is the closing
phase correctly registering `m_MaxBoardingDistance = 0`. Both confirm the mechanism is the one
described in round 7.

`passengers` still gates 80% of attempts, but that is now the closing phase legitimately
waiting a few attempts for in-flight boarders. Since nothing expires, that wait always
resolves.

### Open items now that the stall is fixed

- **`native=15` vs `managed=281`.** Almost everything completes through the managed path; the
  native lifecycle is effectively bypassed. Not a defect while `expired=0`, but it means the
  mod owns the boarding lifecycle in practice, which is worth acknowledging in the design.
- **The round 5 reach gate has been reverted.** `boarded`/`alighted` proved buses board fine at
  zone distances, so the reachability premise was wrong, and `out-of-reach=662` showed it was
  rejecting real candidates and narrowing the feature for no measured benefit. `CanAdmit` no
  longer takes a reach argument, `IsWithinPassengerReach` is deleted, and a policy assertion
  now forbids reintroducing distance-based admission rejection. `PassengerReachDistance`
  survives, but bounds only the waiting band.
- **`PassengerWaitingSpreadSystem`** is kept; it is the mod's original design and is now
  bounded.
- **Telemetry.** The health lines are cheap (every 4096 frames) and have proved their worth.
  Recommend keeping them, possibly demoted, rather than removing them.

## Round 9 result: healthy lifecycle, unhealthy city - and a testing confound

`ended=43` in ~4 minutes (vs 296 in ~7 in round 8), 1-4 active sessions, `expired=0`, `oldest`
low. The lifecycle is fine and the mod is barely engaging. But `boarded=265` against
`alighted=351`: **more cims are getting off than on**. The user also reports lines now sitting
at 0% usage that were never at 0% before, and observed a cim waiting at a stop then abandoning
the wait before its bus arrived.

### The confound that now dominates

Every run this evening has continued from a save that the broken builds were already degrading.
CS2 citizens do not re-adopt transit quickly once they have re-planned around cars, and a line
that has lost its riders does not repopulate on its own. **A correct build tested on a damaged
save can look like a failed build.**

`alighted > boarded` is exactly the signature of a system draining rather than one failing:
the remaining passengers are getting off and few new ones are getting on.

This must be resolved before any further inference from ridership:

1. Test the current build on a save from **before** this evening's testing, or a fresh city.
2. If ridership is healthy there, the fixes are good and the test save is simply scarred.
3. If it still decays on a clean save, there is a remaining defect and the counters are not
   capturing it.

Running the observer-only package on the *damaged* save is also informative: if ridership keeps
falling there too, the damage is in the save, not in the current build.

### Change: passenger spread unregistered again

`PassengerWaitingSpreadSystem` was registered in round 5 on the reachability premise, which
round 7 disproved. Its only remaining effect is to displace waiting cims from their stop, and
the user's observation - a waiting cim abandoning its wait - points directly at waiting
position. With its justification gone and a symptom that matches, it is unregistered again and
a policy assertion keeps it that way. `LimitWaitingBoundsToReach` is retained so that any
future re-registration is bounded from the start.

## Round 10: fresh save still decays - confound rejected, real defect remains

On a **fresh save** every line fell from ~50% to 10-20% and kept falling. So the decay is not
save damage, and the round 9 confound theory is rejected along with it. A real defect remains
in the current build.

### The mod takes over every bus at every stop

`CanAdmit` for an ordinary stop with `activeBusCount = 0` reduces to
`0 < OrdinaryStopLimit`, which is always true. The **first** bus at **every** stop was therefore
admitted, frozen by `BoardingHoldSystem`, and run through the managed lifecycle instead of the
native one. The active-session counts bear this out: 1-4, mostly 1 - i.e. most sessions were
single-bus sessions with no concurrency to resolve.

For a single bus the mod provides no benefit and adds real cost. Sessions run 128-528 frames
where a native dwell is seconds. Across every stop on every line that inflates round-trip times
citywide, which is precisely "all lines decline together". It also explains why round 8 fixed
the stall convincingly in the counters without helping the city: the counters measured the
managed lifecycle's health, not the dwell it was adding.

### Change: engage only on contention

`ShouldEngageConcurrentBoarding(busesAtStop)` requires more than one bus close to the stop.
With one bus there is no session, no hold and no slot override - the stop visit is left
entirely to native AI. Sessions already running are not disturbed when a partner departs, so a
leaving bus cannot cut another's boarding short. Reported as
`contended stop visits` versus `single-bus visits left to native AI`.

Expect `single-bus visits` to dominate heavily. If it does not, buses are bunching far more
than assumed and that is itself the finding.

## Round 11: IL confirms the bypass - unpaired native BeginBoarding

Dumped from the installed 1.6.0 `Game.dll` via `scripts/dump-boarding-il.ps1`
(`artifacts/il`, gitignored).

`TransportCarAISystem/TransportCarTickJob::StopBoarding`, IL_0171-IL_01e9:

```
if ((publicTransport.m_State & 96) != 0) goto skip;   // 96 = Evacuating | PrisonerTransport
if (!isBoardingVehicle)                  goto skip;   // stop's BoardingVehicle.m_Vehicle == this bus
m_BoardingData.EndBoarding(vehicle, currentRoute.m_Route, connected.m_Connected,
                           target.m_Target, storageCompany, nextStorageCompany);
return true;
```

`isBoardingVehicle` is computed at IL_0000-0039 from `Target -> Connected -> BoardingVehicle`.
For an ordinary passenger bus both guard flags are zero, so **native completion always issues
`EndBoarding`** for the bus holding the stop slot.

`TransportBoardingHelpers/TransportBoardingJob::EndBoarding` then **clears the stop's
`BoardingVehicle.m_Vehicle`** (`IL_0031: stfld`, `IL_0048: set_Item`), which is how a stop is
released for the next vehicle. `BeginBoarding` sets it. The slot is owned by that asynchronous
job, not by whoever writes the component.

### The defect

`TryCompleteBoarding` clears flags and advances the target but never issues `EndBoarding`. The
2026-07-22T21:21 decision forbade it, reasoning that the mod never enqueues a matching
`BeginBoarding`.

That reasoning holds only for **synthetic** sessions, where the mod's own `BeginBoarding` just
sets flags. It is backwards for **native** sessions (`UsesNativeBoarding = 1`), which reached
`Boarding` through native `StartBoarding` - and `StartBoarding` *does* enqueue
`BoardingData.BeginBoarding` (`StartBoarding.txt` IL_01a5 and three sibling AI systems).

Round 8 measured `native=15` against `managed=281`. So roughly **95% of boarding sessions
citywide enqueue a native `BeginBoarding` that never receives its `EndBoarding`**, while the
mod separately clobbers `BoardingVehicle.m_Vehicle` directly every frame. The native job's view
of which stops are mid-boarding and the actual state diverge permanently, and nothing in the
mod's own counters can see it, because they only watch the managed lifecycle.

This is the best-supported explanation so far for cims declining to *choose* buses while the
buses themselves appear to run: the stop-side boarding records are corrupt.

### The pairing cannot be issued from a separate system

`TransportBoardingHelpers.type.txt` gives the full API:

```
BoardingData                       // struct holding NativeQueue<BoardingItem>
  .ctor(Allocator)
  ScheduleBoarding(SystemBase, CityStatisticsSystem, TransportUsageTrackSystem,
                   AchievementTriggerSystem, BoardingLookupData, uint frame, JobHandle)
  ToConcurrent() -> BoardingData/Concurrent
  Dispose() / Dispose(JobHandle)

BoardingData/Concurrent
  BeginBoarding(vehicle, route, stop, waypoint, currentStation, nextStation, refuel)
  EndBoarding  (vehicle, route, stop, waypoint, currentStation, nextStation)
```

`TransportCarAISystem` holds **no** `BoardingData` field - only `m_BoardingLookupData`. The
`BoardingData/Concurrent m_BoardingData` field belongs to `TransportCarTickJob`. With
`.ctor(Allocator)`, `ScheduleBoarding` and `Dispose(JobHandle)`, this is the standard
create-use-dispose-within-`OnUpdate` pattern: **the queue exists only for the duration of one
`TransportCarAISystem.OnUpdate` call.**

There is therefore no persistent queue for another system to enqueue into. Pairing
`EndBoarding` is impossible without Harmony-patching `TransportCarAISystem.OnUpdate`.

`ScheduleBoarding` also takes `CityStatisticsSystem` and `TransportUsageTrackSystem`, and
`TransportBoardingJob` owns `m_StatisticsEventQueue` and `m_TransportUsageQueue`. **Transport
usage statistics are emitted by that job, from those queue items.** A boarding session that
never enqueues its items contributes nothing to usage tracking - which matches the reported
symptom precisely: lines reading 0% usage.

### The underlying tension, now proven rather than inferred

- A stop has exactly one `BoardingVehicle` slot, and boarding is finalised asynchronously from
  a queue private to one `OnUpdate`.
- Native completion requires the bus to hold that slot **and** to have reached its path end.
- Concurrent boarding necessarily means extra buses that hold neither.
- Completing them in the mod leaves native `BeginBoarding` records unpaired and their usage
  events unemitted.

Concurrent boarding at a single native stop therefore cannot be made bookkeeping-correct
without patching `TransportCarAISystem.OnUpdate`. This is an architectural limit, not a bug to
be fixed in the existing systems.

### Superseded plan: pair the call

`TransportBoardingHelpers.BoardingData` was used by this mod once before (2026-07-22T16:45)
and is public, so the pairing is feasible. Before writing it, dump
`TransportBoardingHelpers`, `TransportBoardingSystem`, `EndBoarding` and every `BoardingData`
reference to establish how a managed system obtains the queue and its `Concurrent` writer.
Those targets are now in `scripts/dump-boarding-il.ps1`.

Ordering constraint: `EndBoarding` takes `target.m_Target`, so it must be issued **before**
`TryAdvanceToNextWaypoint` changes the target. It also makes the mod's manual clearing of
`BoardingVehicle.m_Vehicle` unnecessary, since the native job performs it.

## Superseded: native route bookkeeping is being bypassed

Worth recording as the next suspect if engagement-gating does not restore ridership.

Round 8 measured `native=15` completions against `managed=281`. Around 95% of sessions
complete through `TryCompleteBoarding`, which advances the bus with `VehicleUtils.SetTarget`
rather than letting native `StopBoarding` finish the visit. If the native path is also where
CS2 records stop service, route timing or waiting-passenger cleanup, bypassing it on almost
every visit would degrade exactly the statistics that feed a cim's decision to use a line -
without any of the mod's own counters noticing, since they only measure the managed lifecycle.

This should be checked directly against the installed `Game.dll`: find every write performed by
`StopBoarding` after the readiness test, and confirm whether `TryCompleteBoarding` reproduces
all of them or only the target advance.

## Implementation status

Items 1-5 below are implemented. Item 6 was deliberately deferred: deleting the two
unregistered systems would also require rewriting the `BoardingZoneApproach` /
`BoardingZoneFallback` guards in `ConcurrentBoardingSystem` and the query-count policy
assertions, which enlarges the diff for a build whose whole purpose is to isolate this fix.

A bounded `Mod.Log.Info` health report was added to `PassengerDistributionSystem`
(every 4096 frames: active session count, oldest session age, expired-session count). This
is the evidence channel described under "How to confirm" and is intentionally not gated
behind `CBB_DIAGNOSTICS`, since it must be present in the build under test. Note that
`RequireForUpdate` means the report simply stops appearing when no sessions are active.

## Proposed fix

Ordered by expected impact. 1 and 2 together should be enough; 3 is the safety net that
guarantees the failure mode can never be permanent again.

**1. Stop clearing `Boarding` on native sessions.**
Change `BoardingPolicy.ShouldExposeBoardingToVehicleAi` to return `usesNativeBoarding`
(drop `&& selected`). A native boarding session then stays continuously visible to the car
AI exactly as vanilla expects, `StartBoarding` is entered once, and `m_DepartureFrame` is
never re-armed. Concurrency is still expressed the only way it needs to be: through the
rotating `BoardingVehicle.m_Vehicle` passenger pointer.

**2. Align the passenger-pointer rotation with the car-AI cadence.**
Drive both rotations from one counter, phased so that each active bus is guaranteed to hold
`BoardingVehicle.m_Vehicle` across at least one full car-AI tick per rotation. Otherwise
native `StopBoarding`'s slot test can starve a bus indefinitely. Concretely: rotate on
`ConcurrentBoardingSystem`'s own 16-frame tick and have `PassengerDistributionSystem` hold
the pointer stable between ticks rather than recomputing it from `frameIndex / 16`.

**3. Add a hard, unconditional session deadline.**
Add `AdmittedFrame` to `ConcurrentBoardingActive`. In `PassengerDistributionSystem`, if
`frameIndex - AdmittedFrame` exceeds the All Aboard dwell (or the 1800-frame fallback),
force-release the session regardless of path: restore `EnRoute`, clear `Boarding` if the mod
set it, clear the stop slot, remove `ConcurrentBoardingActive`, and hand the bus back to
native AI. This must not depend on `m_DepartureFrame`, because `m_DepartureFrame` is the
value that gets corrupted. This converts any residual livelock into a bounded stall.

**4. Fix the synthetic dwell fields.**
In `BeginBoarding`, set `m_MaxBoardingDistance = float.MaxValue` (match native
`StartBoarding`) and `m_DepartureFrame = frameIndex + 64u` without `math.max`.

**5. Restore before re-capture in `PublicTransportAttractivenessSystem`.**
Write the stored baselines back before calling `CaptureOriginalCosts` on a slider change.

**6. Delete `BoardingZoneApproachSystem` and `PassengerWaitingSpreadSystem`,** or gate them
behind an explicit registration, so future triage is not misled by unregistered code.

## How to confirm before shipping

The cheapest decisive check is a counter, not a gameplay session.

- Add a periodic (every ~4096 frames) `Log.Info` from `PassengerDistributionSystem`:
  number of `ConcurrentBoardingActive` buses, and the maximum
  `frameIndex - AdmittedFrame` across them. If both climb monotonically over a session and
  never fall back, Hypothesis 1 is confirmed.
- For active native sessions, also log `m_DepartureFrame - frameIndex`. A value that keeps
  increasing rather than counting down is direct proof of `StartBoarding` re-entry.
- In game, compare each line's vehicle count against the number of buses actually moving.
  Divergence that only grows is the same signal.
- A/B against the observer-only configuration the repo has used before (register only the
  settings, tool, render and UI systems). If ridership is stable there and decays with the
  simulation systems registered, the cause is in this set of five systems.

Re-verify the `StartBoarding` / `StopBoarding` / `m_DepartureFrame` reasoning directly
against the installed `Game.dll` (and All Aboard's `PatchedTransportCarAISystem`) before
implementing, since the whole fix rests on it and this pass could not read the assemblies.

## Why waiting cims cannot be spread along the boarding zone

Settled by measurement, not argument. Do not reopen this with a new formula.

The feature was rebuilt and instrumented nine times. Three separate defects were found and
fixed along the way, and the crowd still never moved:

1. **`HumanCurrentLane.m_QueueEntity` is the route waypoint, not the stop.** It carries
   `[AccessLane, Connected, Owner, Position, PrefabRef, RouteLane, Simulate, VehicleTiming,
   WaitingPassengers, Waypoint]`. Every zone lookup failed for every cim, so the position code
   never executed at all. The stop is reached via `Connected.m_Connected`. Telemetry:
   `noZone` exactly equalled `waiting` until this was fixed.
2. **`ResidentAISystem` overwrites `HumanCurrentLane.m_QueueArea` every tick.**
   `ResidentTickJob::SetQueuePosition` recomputes it from the stop's own transform via
   `CreatureUtils.GetQueueArea`, then `TickQueue` copies it into `Creature.m_QueueArea`
   (a pure copy, IL_009f-00a7). Writing only the lane field is discarded before use.
3. **The front-weighted spread curve was itself the bunching.** `(x + x^2)/2` has mean 0.42,
   so on a 26 m zone the whole crowd sat within ~11 m of the marker. Measured: cims stood
   9.7 m back on average, which is exactly what the curve requested.

After all three were fixed the writes landed and persisted, and the cims still ignored them:

- `Creature.m_QueueArea` **persists** - only 2% of cims (`reset=3740` of `moved=185530`) had
  it reset between visits - and is still ignored. `avgToTarget` (16.8 m) matched `avgOffset`
  (16.4 m): the cim never moved toward its assigned spot at all. The queue area bounds *where
  queuing is permitted*, it does not position anyone. `CreatureCollisionIterator::CheckQueue`
  reads it for queue ordering and collision only.
- `HumanNavigation.m_TargetPosition` is the value a cim actually walks to, but its only
  writers are `InitializeCreaturesJob` and `HumanNavigationSystem`'s `UpdateStumbling` and
  `UpdateNavigationTarget`. Written *before* that system it is recomputed the same frame.
  Written *after* it, the queue link is already torn down: `moved` collapsed from ~180k to
  ~17k per report, `nullStop` rose to ~165k, `zonedStops` fell from 85 to 15, and
  `m_QueueEntity` pointed at a `PedestrianLane`. The target was ignored either way.

A Harmony patch on `ResidentTickJob::SetQueuePosition` is not a remaining option worth
trying: these are Burst-compiled jobs, so the native path would not go through a patched
managed method, and the patch would silently do nothing.

Visual confirmation: with the overlay on, the crowd is a symmetric blob ~14 m across centred
on the stop marker and spilling outside the zone on the near side, while the 24 m zone sits
empty. That is the shape of the native queue sphere, not of any distribution this mod wrote.

`PassengerWaitingSpreadSystem`, the `SpreadWaitingPassengers` setting,
`WaitingSpreadMaxDistance`, `WaitingSpreadFraction`, `WaitingPosition`,
`LimitWaitingBoundsToReach` and `TryGetPositionAlongZone` were all removed. A policy
assertion in `scripts/test-policy.ps1` fails if the system file reappears.
