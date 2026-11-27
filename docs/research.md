# Research spike: how CS2 routes cars around traffic

Game version studied: **1.6.4f1**. Source: `Game.dll` decompiled with ILSpy (`ilspycmd 9.1`) to
`..\_decompiled\Game-1.6.4f1\` (reference only, outside this repo, never commit or publish it).

This file is about how the game works and how each system is implemented. What we chose and why (thresholds, rules,
test results) is in `decisions.md`.

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
- v1.1, instead of occupancy: a **forecast** of planned demand (`DemandForecastSystem`). Every road vehicle's route
  (current lane, `CarNavigationLane` buffer, then `PathElement`s) is walked up to 120 s of congested drive time; each
  lane reached counts 1. Double-buffered, one `UpdateFrame` group per run, so the published count covers all vehicles
  and is at most ~2 rotations old. Keys are path targets: master lanes for grouped lanes; `UpdateRoadsJob` gives slave
  lanes their own count plus the master's count split over the group.
- Cost pipeline in `ApplyLane` (v1.1): speed factor (above) → `max` with the forecast `1 + α·(v/c)⁴` (capacity =
  120 s × 0.5 vehicles/s, a first guess) → local streets × `1 + k·excess/capacity`, where excess is planned demand above
  1.2 × usual + 2 (usual = per-lane slow average of the demand, 1/64 per visit, in memory) → player road marks →
  ±10% × spread dithering per lane and rotation on lanes above 1.2× → clamp. A closed road mark gives offset 255.
- Why a forecast and not occupancy: an avoided lane gets no speed samples and resets to free flow, so all drivers
  flee to the same detour until it jams too. Planned demand sees the herd before it arrives.
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
  At most 64 re-plans per run (v1.0). v1.1: 8-128 per run, from the smoothed share of re-plans whose new path is not
  back one run later (`EvaluateJob`); emergency vehicles have their own 16.
- v1.1: rule (a) waits for a junction with a choice: one of the next 8 path elements is a node lane (no `EdgeLane`) whose
  owner node has 3+ `ConnectedEdge`s. v1.1: rule (b) (standing jam) did not wait; in the first test hour ~half of all
  re-routes found nothing faster. Later in v1.1: rule (b) waits for a choice too, and no car re-routes while inside a junction
  or stopped first in line (road lane, curve position >= 0.9). Stats per rule: `reroute_jam*`, `reroute_at_junction`.
- Re-plan steps copy `CarNavigationSystem`'s invalid-path handling: `VehicleUtils.ClearNavigationForPathfind` (keeps
  braking distance), clear `PathElement`, `m_ElementIndex = 0`, set `PathFlags.Obsolete`. `PersonalCarAISystem.FindNewPath`
  then requests a path from the current location to the same target.
- Tracking: one run later (~64 frames) the new path's estimated time is compared with the old one and logged
  (found faster route / no better route, average time saved).
- Not covered: public transport (by design: fixed lines and stops, and they already use `IgnoreFlow`).
- v1.1: ambulances, fire engines and police cars on an emergency run (`CarFlags.Emergency`, not returning, at the target,
  extinguishing etc.). `AmbulanceAISystem` re-plans through `VehicleUtils.RequireNewPath` → `FindNewPath`, the same
  mechanism. None of the dispatch systems set `IgnoreFlow`, so dispatch and new service trips already used live costs.
  Garbage, post, hearse and maintenance vehicles are still not covered (routes through several stops).
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
   Partly answered: summed over a junction lane, `LaneFlow.m_Next.y` counts cars (distance / lane length per car);
   a jammed junction lane passes 10-50 cars per game hour.
2. What do the traffic info view colors read from: `Road.m_TrafficFlow*` only, or also `CarLane.m_FlowOffset`?
   (`LaneProperty.FlowOffset` in rendering is a random seed, so most likely the former.)
3. Pathfind queue load. Answered for the test city (~100k): the re-route budget (8-128 per run, from how many new
   paths come back within one run) stayed at 128 in every hour, so re-routes don't overload it.
4. Compatibility with other traffic mods (for example krzychu124's "Traffic"): check whether any of them also
   touch `TrafficFlowSystem` or `FlowOffset`. Lane connections made with Traffic's lane connector show up as normal
   junction lanes to the bottleneck finder (seen at the Ashbourne junction).

## 8. Bottleneck finder (v1.1): how it reads the game

What it detects and why each rule exists is in `decisions.md` ("Bottleneck finder"). This section is the game data it
relies on.

- **Queue head:** from a queued car (`BlockerType.Continuing`, < 1 m/s) follow `Blocker.m_Blocker` while the car ahead
  is a vehicle with a `Blocker` moving slower than max(3 m/s, 40% of its lane's limit), up to 256 cars. The last car
  is the head; its blocker type and lane give the cause. The place is the junction (node) or the street (aggregate) of
  the head's lane; 2-road nodes map to their street.
- **Blocker types seen:** Signal (red light), Oncoming (turning across), Crossing (another car's path), Spawn (leaving a
  building), Temporary (a pedestrian: `CarLaneSpeedIterator.CheckPedestrian` makes the person, a `Creature`, the
  blocker). A stopped bus at a stop is itself the head of the chain (it has a `Blocker` and is stopped).
- **Pedestrian lanes:** `PedestrianLane.m_Flags` has `Crosswalk` for marked crossings and `Unsafe` for crossings with
  no crosswalk; the blocking person's lane is `HumanCurrentLane.m_Lane`.
- **Lane merges:** inside a road piece, a lane that merges into another ends at the same path node
  (`Game.Net.Lane.m_EndNode`) as that lane. So lanes into a junction are counted by distinct end node, and a head on a
  lane whose end node another lane of the same piece shares is waiting to merge. Lane curves run in the driving
  direction; left is counter-clockwise seen from above (x east, z north).
- **Group ("master") lanes:** where cars can change between several lanes, the road piece and the junction also hold a
  `MasterLane` that stands for the group (`m_MinIndex..m_MaxIndex` into the owner's `SubLane` buffer); the real lanes
  have `SlaveLane`. Path elements may name the master lane. A master lane has its own end node and the union of its
  group's turns, so it must be left out of every lane count (it showed up as a phantom extra lane).
- **Junction lanes:** car lanes in the node's `SubLane` buffer without `EdgeLane`; a road lane feeds a junction lane
  when the road lane's `m_EndNode` equals the junction lane's `m_StartNode`. The turn is read from the curve's shape
  (start vs end direction), with the lane's turn flags as fallback.
- **Turn throughput:** `LaneFlow.m_Next.y` grows by distance / lane length for each car on the lane, so summed growth is
  cars; `TrafficFlowSystem` clears it every 512 frames per lane group, so it is read every 16 frames and a drop counts
  the new value.
- **Origin / destination:** origin = district (`BorderDistrict` of the edge) or street where the trip (same `Target`)
  was first seen, else the building that owns the path (cargo yards, parking lots), else "outside the city" within 600 m
  of the map edge (+-7168 m; outside connections enter on unnamed paths such as "Invisible Pathway"). Trips already
  driving at the first sample are unknown. Destination: `Target` -> `PropertyRenter.m_Property` -> `CurrentDistrict`,
  else `Building.m_RoadEdge`'s street.
- **Detours:** route ahead = current lane, `CarNavigationLane`s, `PathElement`s from `m_ElementIndex`, the end point of
  each lane's `Curve` (max 60 points / 6 km), 200 routes per sample. Pairs already joined by one road piece are dropped.
- **Despawns:** `StuckMovingObjectSystem` sets `PathFlags.Stuck` (a blocked loop, or a chain of 100+ blocked vehicles)
  and the car is deleted; a failed route sets `PathFlags.Failed`. Deleted vehicles are visible for one frame in
  Modification1 with `Deleted`.
- **Vehicle kinds:** buses/trams have `PublicTransport`; service vehicles that stop at buildings have `GarbageTruck`,
  `PostVan`, `MaintenanceVehicle` or `Hearse`; trucks are counted by `DeliveryTruck` (to be confirmed for cargo-station
  trucks).
- Checks to do in game (log with "Write traffic report to log"): share of queued cars without a head place, chains cut
  at the cap, destination/origin known shares, cost per sample, whether the top cause matches the map.

## 9. Other game facts found while testing v1.1

What we concluded from them is in `decisions.md` ("Measurement", "How we test").

- **The route planner also picks the travel mode.** Residents' pathfinding is multimodal (car, taxi, public
  transport, walking) and dots the same time cost with their `PathfindWeights`, so a higher cost on jammed car lanes
  also makes car trips lose to the other modes.
- **Bicycles are personal cars:** `PersonalCar` + `Bicycle` (`BicyclePrefab` builds a `Car` archetype and
  `PersonalCarAISystem` drives it). Exclude `Bicycle` in every car query.
- **Road speed limits are stored at about twice the shown value:** `CarLane.m_SpeedLimit` for a small road shown as
  40 km/h is ~80 km/h (22.2 m/s). In a city of mostly small roads, 98% of cars drove on lanes stored above 70 km/h.
  The game converts with x3.6 everywhere, so the doubling is in the prefab data, not in code.
- **Waiting at a stop** is `ResidentFlags.WaitingTransport` on the resident (`Game.Creatures.Resident`); the lane's
  `Transport` / `Taxi` flags only mark the boarding step.
- **People outside:** walkers are `Human` + `Resident` without `CurrentVehicle`; people waiting for a path have
  `PathFlags.Pending` (often still `Unspawned`, inside a building).
- **Lanes in a road piece:** `Game.Net.SubLane` of the edge; a road piece's `Curve.m_Length` is its length.
- **The game rewrites every log file on start**; nothing keeps the previous one unless the mod copies it.

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
