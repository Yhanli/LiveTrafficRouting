# Research spike: how CS2 routes cars around traffic

Game version studied: **1.6.4f1**. Source: `Game.dll` decompiled with ILSpy (`ilspycmd 9.1`) to
`..\_decompiled\Game-1.6.4f1\` (reference only, outside this repo, never commit or publish it).

## TL;DR

- **Vanilla already does congestion-aware routing, but it uses typical traffic, not live traffic.** Every car
  lane has a `FlowOffset` (0–255) that slows the lane down in the pathfinder's cost. It is learned from how
  fast cars actually drove there, kept separately for four times of day, and updated very slowly
  (each lane every ~45 game minutes, moving 12.5 % toward the new sample). A fresh jam takes on the order of
  a game day or more to show up in routing. That is like a navigation app's "typical traffic", not "live traffic".
- **Cars almost never re-route because of traffic.** A car picks its route when the trip starts. It only
  recomputes when the path becomes invalid (road edited or deleted, lane gone) or when it is detected as stuck.
- **There is a clean, public hook:** `PathfindQueueSystem.Enqueue(FlowAction, JobHandle)` lets a mod set any
  lane's `FlowOffset` in the pathfinder graph. No Harmony patch of the pathfinder is needed.
- So the mod's job is: **(1) make the congestion signal live** and **(2) let some drivers re-route mid-trip**.

## 1. How the cost works

`Game.Pathfind.PathUtils.CalculateSpeed` (used by the A* job in `PathfindJobs.cs`, `CalculateCost`):

```
speed = min(vehicleMaxSpeed, laneMaxSpeed)
speed = speed − speed × FlowOffset / 256          // unless PathfindFlags.IgnoreFlow
timeCost = length / speed                         // added to the "time" weight of the edge cost
```

`FlowOffset = 0` means free flow and `255` means about 1/256 of the speed limit, a near-blocked lane.
The time cost is then dotted with the citizen's `PathfindWeights` (time, behaviour, money, comfort),
which come from `CitizenUtils.GetPathfindWeights` for residents.

Trips that **ignore** flow (`PathfindFlags.IgnoreFlow`): public transport vehicles (bus, train, ship, plane lines),
route path previews, service coverage and resource availability, and tourists choosing targets. Residents'
personal car and taxi trips **do** use flow.

## 2. Where FlowOffset comes from (the data pipeline)

1. **`CarNavigationSystem`** (each car, every simulation step on its lane) enqueues
   `LaneEffects.m_Flow = (timeOnLane × speedLimit, distanceTravelled) / laneLength`, which is added into the
   lane's `Game.Net.LaneFlow.m_Next` (or `SecondaryFlow` for bicycles).
2. **`Game.Simulation.TrafficFlowSystem`**, `GetUpdateInterval = 512` frames, lanes split into 16 update groups,
   `UPDATES_PER_DAY = 32`:
   - `UpdateLaneFlowJob`: `m_Duration/m_Distance (float4, one per time-of-day bucket) = lerp(old, m_Next, timeFactor × 0.125)`,
     then clears `m_Next`. The four buckets are night/morning/afternoon/evening, blended by `TimeSystem.normalizedTime`.
   - `UpdateRoadFlowJob`: `flowSpeed = saturate(distance / duration)` (actual speed ÷ speed limit),
     `FlowOffset = clamp(256 − dot(flowSpeed, timeFactors) × 256, 0, 255)`. If it changed, it writes
     `CarLane.m_FlowOffset` and enqueues a `FlowActionData { m_Owner = lane, m_FlowOffset }`.
     It also fills `Road.m_TrafficFlowDuration/Distance0/1`, which the **traffic flow info view** and the
     city-wide average use.
   - `PathfindQueueSystem.Enqueue(FlowAction)` → `ModificationJobs` writes it into the native graph with
     `NativePathfindData.SetFlowOffset(edgeID)`.
3. Timing: a game day is 262,144 frames. Each lane is refreshed every 512 × 16 = 8,192 frames (1/32 of a day,
   about 45 game minutes), and each refresh only moves 12.5 % (times the bucket weight) toward the new sample.

**Why this feels "not live":** the slow smoothing, the time-of-day buckets (a jam at 8am mostly teaches the
"morning" bucket, which matters again tomorrow morning), and the fact that the signal is purely
*speed of the cars that drove there*. It knows nothing about how many cars are queued right now.

## 3. The other "density" is not traffic

`DensityAction` / `NetEdgeDensitySystem` computes **building density along a road** (residents + workers per meter,
sqrt'd). It is used for service coverage, resource availability and parking, not for car travel time.
Not relevant to this mod.

## 4. When cars re-route

`PathFlags` on `PathOwner`: `Obsolete` triggers a new pathfind from the current position. Set by:
- `CarNavigationSystem` when the current lane is gone or obsolete, or the remaining path became invalid
  (road edited, lane removed),
- `StuckMovingObjectSystem` (`PathFlags.Stuck`) for vehicles that haven't moved,
- various AI systems for trip changes (target moved, divert, parking reset).

Nothing re-plans a moving car because a better route opened up or its route jammed. A mod can trigger this by
setting `PathFlags.Obsolete` on chosen cars' `PathOwner`. The existing invalid-path handling then recomputes from
where the car is, using the same plumbing as when a player deletes a road.

## 5. Implementation options

### A. Replace the flow signal (recommended first milestone)
- Disable vanilla `TrafficFlowSystem` (`World.GetExistingSystemManaged<TrafficFlowSystem>().Enabled = false`) and
  run our own system that:
  - keeps doing everything vanilla did for `LaneFlow` and `Road.m_TrafficFlow*`, so the **traffic info view
    and city stats keep working**,
  - computes FlowOffset from a **live** blend: recent `m_Next` samples with fast smoothing (for example 50 % per
    update, updated every few game minutes), optionally plus current lane occupancy
    (`LaneObject` buffer count ÷ lane capacity) for a BPR-style penalty `1 + α (v/c)^β`,
  - mixes in the slow historical value (setting: "live vs typical traffic"),
  - writes `CarLane.m_FlowOffset` and enqueues a `FlowAction`, exactly as vanilla does.
- Why replace instead of run alongside: vanilla rewrites `FlowOffset` whenever its own value differs from
  `CarLane.m_FlowOffset`, so two writers would fight.
- Risk: low to medium. Uses public types and APIs; breaks if CO changes `TrafficFlowSystem`'s outputs.

**Status: implemented** in `LiveTrafficFlowSystem.cs`:
- Runs every 64 frames over one of the 16 lane groups, so each lane gets a live update every 1,024 frames (~5.6 game minutes).
- Live sample = growth of `LaneFlow.m_Next` (+ `SecondaryFlow.m_Next`) since our last visit. Smoothed per lane
  (`Reaction speed`). Kept in a `NativeParallelHashMap<Entity, LiveLane>` in memory, not in the save; pruned when lanes
  disappear and cleared on game load.
- Confidence: below about one "car traversal at the limit" per window, the live speed is blended towards free flow.
- Typical-traffic history (`LaneFlow.m_Duration/m_Distance` lerp at 12.5 % × time factor, clearing `m_Next`) runs on
  every 8th visit, which is the same 8,192-frame rate as vanilla. Road info-view values are aggregated exactly as vanilla.
- Blending is done on travel time, not speed: `time = lerp(1/typical, 1/live, liveTrust)`, then
  `time = 1 + (time − 1) × penaltyStrength`, capped at the "Avoid jams up to" factor; `FlowOffset = 256 − 256/time`.
  (Blending speeds, as in the first version, capped a full jam at 1/(1 − liveTrust): only 4× on Balanced.)
- Both jobs are scheduled single-threaded (they share the hash map), which is fine for ~1/16 of the lanes per run.
- Not yet: occupancy (counting queued cars) on top of the speed signal. Revisit if jams that cars avoid entirely
  "recover" too quickly in the live data.
- Known minor issue: `TriggerSystem` calls vanilla `TrafficFlowSystem.RefreshCityTrafficAverages()`, which reads
  `Road` without waiting for our job. In release builds this is at worst a slightly stale city average.

### B. Mid-trip re-routing (second milestone, the "navigation app" feel)
- A periodic, throttled system picks a fraction of moving personal cars and taxis (setting: "% of drivers with
  live navigation"), estimates whether their remaining path got much slower (sum of current lane costs vs cost at
  departure), and if worse by more than a threshold, sets `PathFlags.Obsolete`.
- Must rate-limit: every re-route is a full A* job on the shared pathfind queue.

**Status: implemented** in `RerouteSystem.cs`:
- Runs every 64 frames over one of 16 vehicle groups (each vehicle checked every 1,024 frames). Covers personal cars
  (carrying people, or through-traffic), delivery trucks (not cancelled; incl. through-traffic) and taxis (not boarding,
  arriving or disabled), on a road lane, not parking or changing lane, no pending or special path state. Vehicles with
  trailers are allowed: only the lead vehicle has a path, trailers follow its lanes (`CarNavigationSystem` trailer update).
- "Live navigation" share is a deterministic hash of the car's entity index.
- Remaining-route estimate: sum over the path elements ahead of `length / (speedLimit × (1 − FlowOffset/256))`, the same
  slowdown the pathfinder applies, against the free-flow time. The ratio at first sight of a path is the baseline.
- Re-plan when the cooldown has passed and either (a) ratio ≥ baseline × (1 + threshold) with ≥ 20 s extra delay
  vs baseline, or (b) standing jam: ratio ≥ 1 + 2 × threshold with ≥ 40 s extra vs free flow. Remaining trip ≥ 30 s.
  Cooldown 4,096 frames, doubled after each re-plan that found no better route (max 8×), reset after a better one.
  At most 64 re-plans per run.
- Re-plan steps copy `CarNavigationSystem`'s invalid-path handling: `VehicleUtils.ClearNavigationForPathfind` (keeps
  braking distance), clear `PathElement`, `m_ElementIndex = 0`, set `PathFlags.Obsolete`. `PersonalCarAISystem.FindNewPath`
  then requests a path from the current location to the same target.
- Tracking: one run later (~64 frames) the new path's estimated time is compared with the old one and logged
  (found faster route / no better route, average time saved).
- Not covered: public transport (by design: fixed lines and stops, and they already use `IgnoreFlow`).
  Service vehicles (garbage, post, police, fire, ambulance, hearse, maintenance) not yet: review each AI system's
  `FindNewPath`/dispatch handling first.
- Risk: medium. Touches `PathOwner` state that many AI systems read. Needs care with parked, parking and
  trailer vehicles, and anything with `PathFlags.Pending`.

### C. Patch the cost function directly (not recommended)
- Harmony on Burst-compiled jobs does not work (Burst compiles from IL ahead of the managed patch). Option A gives
  the same effect through data.

## 6. Anti-oscillation knobs (settings)

- Live/typical blend and smoothing speed.
- Max slowdown (cap FlowOffset) so no road becomes "infinitely" avoided.
- % of drivers with navigation (only they re-route mid-trip; everyone still sees live costs at departure).
- Re-route threshold (for example 15 % faster) and cooldown per car.

## 7. Open questions to check in game

1. How big are typical `LaneFlow` sample values per update on a busy road vs a quiet one (log a few lanes)?
2. What do the traffic info view colors read from: `Road.m_TrafficFlow*` only, or also `CarLane.m_FlowOffset`?
   (`LaneProperty.FlowOffset` in rendering is a random seed, so most likely the former.)
3. Pathfind queue load: how many pathfind requests per second does a 100k city already make? That sets the
   re-route budget.
4. Compatibility with other traffic mods (for example krzychu124's "Traffic"): check whether any of them also
   touch `TrafficFlowSystem` or `FlowOffset`.

## Key files (decompiled)

| What | File |
|---|---|
| Flow → FlowOffset, info-view data | `Game.Simulation/TrafficFlowSystem.cs` |
| Per-car flow samples | `Game.Simulation/CarNavigationSystem.cs` (~line 2170, `LaneEffects`) |
| Lane flow storage | `Game.Net/LaneFlow.cs`, `Game.Net/CarLane.cs` (`m_FlowOffset`) |
| Cost and speed | `Game.Pathfind/PathUtils.cs` (`CalculateSpeed`), `Game.Pathfind/PathfindJobs.cs` (`CalculateCost`) |
| Queue API | `Game.Pathfind/PathfindQueueSystem.cs` (`Enqueue(FlowAction, …)`) |
| Graph writes | `Game.Pathfind/ModificationJobs.cs` (`SetFlowOffset`) |
| Re-route flags | `Game.Pathfind/PathFlags.cs`, `Game.Vehicles/VehicleUtils.cs`, `Game.Simulation/StuckMovingObjectSystem.cs` |
| Building density (not traffic) | `Game.Simulation/NetEdgeDensitySystem.cs` |
