# Live Traffic Routing

A Cities: Skylines II code mod (work in progress). Drivers react to **live** congestion, the way a navigation app with
real-time traffic would route you: jammed roads get slower in the route planner, so new trips avoid them, and some
drivers already on the road switch to a faster way when their route jams up.

The game already makes roads with slow traffic less attractive, but it learns this very slowly (over game days) and
per time of day, like "typical traffic". This mod adds a live measurement of the last few game minutes and blends it in.

Background and design: [docs/research.md](docs/research.md) (how the game works, how the mod is built) and [docs/decisions.md](docs/decisions.md) (what we chose and why, test results).

## Status

- **Milestone A (done, needs in-game testing):** live congestion signal. Replaces the game's `TrafficFlowSystem` with
  `LiveTrafficFlowSystem`. The traffic flow info view and city statistics work as before.
- **Milestone B (built, needs in-game testing):** mid-trip re-routing (`RerouteSystem`). Drivers with "live navigation"
  re-plan when the rest of their route looks a set % slower than when they planned it, or when they are heading
  through a standing jam (route ahead slower than free flow by twice that %). Vehicles that find no better route wait
  longer before checking again. Covers cars, delivery trucks
  (including semi-trailers) and taxis, including through-traffic. Public transport keeps to its line.
- **v1.1 (tested in one test city; the final Native / Balanced / Eager comparison is still to do, see `docs/decisions.md`):**
  - **Forecast** (`DemandForecastSystem`): counts, per lane, the vehicles that plan to drive it within the next
    2 minutes of drive time, and prices that planned demand with a capacity curve (BPR). A floor under the speed cost,
    so roads many drivers are heading for look busier before they jam. Plus small rotating noise on slowed lanes so
    drivers planning at the same moment don't all pick the same detour.
  - **Neighbourhoods**: local streets (50 km/h or less) whose planned traffic rises well above their usual level look
    slower, so through traffic stays on main roads. "Usual" is learned per lane while playing (in memory).
  - **Re-routing**: a re-route because the way ahead got slower waits for a junction with a choice (3+ roads); the
    per-update limit follows how busy the route planner is (8-128, was a fixed 64); ambulances, fire engines and
    police cars on an emergency run always re-route, with their own limit. Other service vehicles are not covered.
  - **Auto preset** (`AutoTuner`): switchback experiment, one value at a time (jam penalty, spread, neighbourhoods).
  - **Temporary roadworks** (`RoadMarkSystem`, `RoadMarkToolSystem`): close, avoid or prefer a road from the panel.
    In memory only, cleared on load, and the UI says so everywhere.
  - Statistics: how people travel (car share, walking, cycling, public transport, waiting for a route), vehicles the
    game removes when stuck and where, forecast and re-route details, traffic news, Copy summary.
  - **Bottleneck finder** (`BottleneckSystem`, `LaneDiagnosis`, statistics panel only, changes no routing). Follows each queued car's chain of stopped cars (`Blocker`) to the head of its queue; the head's
  blocker type and lane give the cause (traffic lights, oncoming traffic, cars crossing their path, give way/stop, busy
  junction, lanes merge or road narrows, cars merging onto a highway, cars entering, parking, buses or service vehicles
  stopping in the lane, people crossing at a crosswalk or where there is no crosswalk, slow traffic). Junction advice
  also covers shared turn lanes, turn lanes that are too short, lanes that merge before or after the junction, queues
  reaching back into the previous junction and queues of mostly trucks (see `docs/decisions.md`). Per queue head: destinations (district, else the street of the target
  building), origins (where the trip was first seen), through-traffic share. Detours on queued routes (two points
  <= 400 m apart that the route takes >= 1 km and 3x to connect) are suggested as possible new links, but only when
  the long way round passes the head of the driver's own queue (so the new road lets them skip it). Each fix belongs
  to its bottleneck and is shown in that place's details under "What you can do"; it disappears when the bottleneck
  is cleared or off the list. Panel tab
  "Causes"; `bottlenecks.csv` / `links.csv`; details in the log with "Write traffic report to log".

## Settings (Options > Live Traffic Routing)

**How drivers react to traffic jams** (one dropdown):

| Choice | What you'll see |
|---|---|
| Off (game default) | The game's normal behaviour. |
| Relaxed | A light touch: drivers avoid the worst jams and stay on main roads. Fewer jam warnings, little else changes. |
| Balanced (default) | Drivers react quickly; 3 in 4 re-route mid-trip. Tested: queues -28%, speed +3%, jam warnings -87%, local streets +10%. |
| Eager | Strongest: a jammed road is treated almost as closed, every driver re-routes. Tested: queues -38%, speed +6%, but local streets +33%. |
| Custom | Your own mix. Chosen automatically when you change a slider. |
| Auto | Tunes itself to the city: alternates game hours between its current values and one small step, keeps a step only when queues clearly drop without more local-street traffic. Sliders are not used. Learned values per city in `ModsData/LiveTrafficRouting/auto-tuning.txt`. |

The sliders are always visible (greyed out when Off or Auto). Picking a preset fills them in with these values; changing any
slider switches the dropdown to Custom:

| Slider | Meaning | Relaxed | Balanced | Eager |
|---|---|---|---|---|
| Right now vs usual traffic | 100% = only current traffic, 0% = only the usual traffic for this time of day (vanilla) | 75% | 100% | 100% |
| How quickly jams are noticed | Higher = notices new and cleared jams sooner, but traffic may swing between roads | 50% | 80% | 100% |
| Jam penalty strength (25-1000%) | Multiplies the extra time a jam adds (200%: twice as slow looks three times as slow) | 100% | 250% | 1000% |
| Avoid jams up to (2-100×) | The worst a jammed road can look vs driving it at the limit (the pathfinder's own limit is 256×) | 10× | 50× | 100× |
| Spread drivers over detours (v1.1) | Forecast strength: roads many drivers are heading for look busier before they jam (0% = off) | 25% | 50% | 50% |
| Keep through traffic out of neighbourhoods (v1.1) | Local streets with much more planned traffic than usual look slower (0% = off) | 50% | 50% | 75% |
| Drivers who re-route while driving | Share of drivers with live navigation (0% = no mid-trip re-routing) | 30% | 75% | 100% |
| Re-route when the way ahead gets slower by | How much worse the rest of the route must get vs when planned | 30% | 15% | 15% |

If you were on a preset whose values changed in an update, the new values are applied automatically. (v1 moved the
presets: the old Balanced is now Relaxed, the tested Eager is now Balanced, and Eager is the strongest tested setup.)

**For traffic geeks** (both off by default):
- **Statistics panel**: shows the Live Traffic icon and panel and records statistics (below). Off = nothing is recorded.
- **Don't pause for milestone popups while recording** (on by default, shown with the panel on): milestone and
  signature building popups pause the game and would stop a test run. This turns off the game's own "blocking
  popups" setting while statistics record, and puts your setting back when you turn it (or the panel) off.
- **Keep statistics for**: 1 game month, 3 months, **1 year** (default, about 11 MB), 3 years, 10 years (about 105 MB)
  or Unlimited. The last two show a warning: large files slow down loading. Above the limit the oldest *whole* runs
  move to archive files; the newest Native run of each city (baseline), runs marked **Keep** in the panel's Runs tab
  and the run being recorded are never removed. One limit for all cities and both files together.
- **Write traffic report to log**: also writes each recorded game hour to `Logs/LiveTrafficRouting.log`.

## Statistics (with the mod on and off)

With **Statistics panel** on, the mod measures traffic every game minute, **also when the mod is set to Off**, so you
can compare the game's own routing ("Native") with live traffic ("Live"). Each game hour is saved as one row,
tagged with its **run**, to
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\LiveTrafficRouting\captures.csv`.

| Metric | Meaning |
|---|---|
| Real speed | Average real driving speed of all road vehicles vs the speed limit |
| Waiting in queues | Vehicles stopped behind another stopped vehicle |
| Jam warnings | Number of jam warning signs the game shows (average and max per hour) |
| On local streets | Vehicles on roads with a limit of 50 km/h or less (detour traffic in neighbourhoods). Before 1.1.0 this missed almost all small roads (the game stores speed limits at about twice the shown value), so older runs show about 1% and are not comparable |
| Traffic flow | The game's own Traffic Flow % (the info view chart) |
| With navigation vs others | How fast vehicles get closer to their destination, for the drivers with live navigation vs everyone else. Same city, same time, so it is a fair comparison. With the mod off both should be about equal. |
| Re-routing | Re-routes by cars / trucks / taxis / emergency vehicles, share that found a faster route, estimated time saved, re-routes that waited for a junction, the per-update limit |
| Public transport share (v1.1) | People riding public transport, as a share of everyone riding a car, taxi or public transport at that moment. It also rises when car trips get faster (fewer people in cars at any moment), so it is not coloured good or bad: compare **People on public transport** (v1.1) to see whether people actually switched |
| Trip speed, Vehicles on roads, People in cars (v1.1) | How fast trips progress toward their destination; vehicles and car passengers (not cyclists) on the road at the same moment. Faster trips mean somewhat fewer vehicles on the road for the same number of trips; a big drop usually means people left the car (see below) |
| Car share of travellers, People walking, People cycling, Waiting for a route (v1.1) | Everyone on the move: in cars and taxis, on public transport, on bicycles, on foot, and people waiting for the game's route planner. The route planner also picks **how** people travel, so a strong setting can make people walk or take the bus instead of driving: that lowers queues too, but it is not better routing. Watch the car share when comparing settings |
| Forecast (v1.1) | Vehicles counted, share of lane updates priced by the forecast or the neighbourhood penalty, highest demand/capacity |
| Route planner view | Speeds with the mod's penalties, as the route planner sees them (Live only) |
| Mod processing time | The mod's own jobs, one run timed per game hour |

**Runs**: a run is one continuous recording with the same mode and settings (all slider values are stored). A new
run starts when you load a save, delete statistics, or change the mod's settings; the game hour in progress is then
not recorded, so every row belongs to exactly one setup.

The files are read by column name (older files are upgraded once, with a backup). Cleanup follows "Keep statistics
for" (whole runs to `*-archive-<date>.csv`); deleting keeps a `*-backup-<date>.csv`. Only the newest 5 of each are kept.

Where to see them:
- **In game:** the traffic icon in the top-left opens the **Live Traffic** panel (close it to collapse to the icon):
  - **Now**: the current hour.
  - **Compare**: pick run **A** and run **B** (default: latest Native vs latest Live) and compare by **same game
    time** (hour by hour, for the same save played twice), **same times of day**, or **all hours**, with a
    difference column and a **Real?** check: is the difference bigger than the simulation's own randomness
    (clear / likely / noise, from a paired or Welch t-test that also corrects for neighbouring hours being related).
    Hover it for the 95% range.
  - **History**: both runs side by side, **by time of day** or as a **timeline**, for any metric.
  - **Hotspots**: the streets and junctions with the most vehicles waiting in queues (and jam warnings), run A vs
    run B. Recorded per game hour (top 15 places) to `hotspots.csv`.
  - **Causes** (v1.1): the last game hour's top 5 bottlenecks, i.e. where queues *start*, with the cause, cars queued
    behind, where those drivers go and come from, through traffic share, and up to 3 possible new links. Click to show
    it on the map. Recorded to `bottlenecks.csv` and `links.csv`.
    Clicking a place shows it on the map (`BottleneckOverlaySystem`): red = lanes the queue stands on (wider = more
    cars), turns the queued cars want through the junction (blue left, white straight, purple right, yellow U-turn,
    wider = more; dashed grey = allowed but unused), with advice from `LaneDiagnosis` (e.g. "91% turns left but only 1
    of 3 lanes allow it"). A possible link shows the detour (orange) and the suggested connection (dashed green).
    Turn throughput (`TurnFlowSystem`): for the junctions on the list, cars per lane per game hour for each movement
    (distance driven on the junction lanes, from the game's `LaneFlow`, / lane length). When most of a queue waits to
    turn and turning moves much fewer cars per lane than going straight, the advice says the turn is slow (a second
    turn lane, a wider turn, or a slip lane). Found in the first test: the Ashbourne queue cleared once both lanes
    could turn.
    **Ongoing** view (default): places ranked by stuck cars added up over the last 24 game hours ("in 20 of 24
    hours"), and fix suggestions only when seen in at least a quarter of those hours (from 4 hours on), so one-hour
    flashes don't show. Each place says whether it is **getting better** or **getting worse** (last 3 game hours against
    the 24-hour average), or **cleared** when it had no queue for 3 game hours (a fix that worked shows here at once;
    cleared places move to the bottom and leave once their old hours are out of the window). "This hour" shows the
    last hour only. A street queue names the junction it ends at and gets
    that junction's lane and turn advice. Going to / coming from / streets are clickable (camera moves there, no
    selection pin) and drawn on the map as dashed arcs (cyan from, pink to).
    Overlay colours and widths: `ModsData/LiveTrafficRouting/overlay-style.txt`, re-read while the game runs. Each
    shown overlay is also written to `overlay-preview.json`.
  - **Despawns** (v1.1, Compare metrics): vehicles the game removed per game hour because they were stuck (vanilla
    `StuckMovingObjectSystem`: a blocked loop or a 100+ vehicle chain) or their route failed, plus failed re-routes
    (Re-routing metrics). Counted by `DespawnCounterSystem` in Modification1, with the mod on or off.
  - **Trips** (destinations): why queued cars are on the road (commuting, going home, shopping, leisure, other), and for
    shopping and leisure trips (all travel modes): trips per hour, how many different places are used, % of trips to
    the 5 busiest places, and distance from home. Shows whether live traffic makes people pick other places, and how
    much a "change destination mid-trip" feature (leisure only) could help at most.
  - **Re-route**: re-routes, % that found a faster route, time saved, trucks and held-back re-routes, by time of
    day or as a timeline.
  - **Cost**: simulation speed (game minutes per real second), frames per second, the mod's own CPU share (% of
    one core, all its systems) and memory, the whole game's memory, and re-routes per hour, run A vs run B with the
    Real? check. Only hours at one steady game speed count; compare runs at the same speed setting (ideally the
    fastest). The Now tab shows a one-line summary of the last game hour.
  - **Runs**: all runs with their settings; delete one run, **all Live runs (keeping the Native baseline)**, or
    everything for this city.
- **Log:** with "Write traffic report to log" on, one line per game hour in `Logs/LiveTrafficRouting.log`. The game
  starts that file afresh each time; the previous session's log is kept as `Logs/LiveTrafficRouting-prev.log`.
- **CSV:** open `captures.csv` in a spreadsheet and filter the `run_id` or `mode` column.

### Fair A/B test (recommended)

1. Save your city, and note the game date and time.
2. Options > For traffic geeks > **Statistics panel** on. Optionally Panel > Runs > delete old runs.
3. Set the mod to **Off**, load the save, and let it run for a couple of game days. Don't build or change roads.
4. Load **the same save** again, set the mod to **Balanced** (or the preset you want to test), and run for the same game time.
5. Panel > Compare > **Same game time**: every game hour of run A is paired with the same game hour of run B (for
   example 2029-01-08 07:00 in both), and only paired hours are averaged. History > By time of day shows the
   same comparison per hour of the day.
6. To test other settings, load the save again with the new settings: it becomes another run. Panel > Runs >
   **Delete all Live runs** clears the experiments and keeps the Native baseline.

If a run records the same game hour twice, the latest recording counts.

The mod adds nothing to save files. It can be removed at any time.

### Temporary roadworks (v1.1)

Panel > **Roadworks**: pick **Close road**, **Avoid** or **Prefer** and click roads on the map (click again or
right-click to remove). Closed roads get the route planner's maximum cost, avoided roads look at least 3 times as slow,
preferred roads ignore half of their congestion penalty. It is a route cost, not a barrier. **Marks are temporary:**
they are kept in memory only, cleared when you load a save, and every roadworks text in the panel says so.

## Feedback and bugs

Please use [GitHub issues](https://github.com/Yhanli/LiveTrafficRouting/issues). For "traffic got worse" or performance reports, the statistics panel's
Compare / Cost / Hotspots tabs and `Logs/LiveTrafficRouting.log` show exactly what happened (see the issue template).

## Build

```powershell
powershell -File tools\build.ps1
```

The script builds the in-game panel (`UI`, needs Node.js; runs `npm install` the first time) and then the mod.
Close the game first. The build deploys to `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\LiveTrafficRouting`.

## Testing in game

1. Turn on **Write traffic summary to log**, load a city with traffic problems and let it run at least one game hour.
2. Check `Logs/LiveTrafficRouting.log`: lines start with "Live traffic ON" and then
   "Live traffic: N lane updates, avg speed X% of limit, Y% at half speed or worse, Z% with live traffic, M lanes tracked".
3. Check `Logs/Modding.log` and `Player.log` for exceptions mentioning `LiveTrafficRouting`.
4. A/B test: block or jam a main road (for example remove a lane at a bottleneck). With the mod on, new trips should start
   using parallel roads within roughly 10–20 game minutes. With it off, it takes game days.
5. The traffic flow info view should look the same as without the mod.
6. Re-routing: the log also gets "Re-routing: ... car checks, ... re-routes ... found a faster route (avg X% of the remaining
   trip time saved), ... got no better route". Watch for cars braking or stopping mid-road right after a re-route; they should
   keep driving smoothly.

## License

MIT (see `LICENSE`). The `UI/types` declarations come from the game's own UI mod template. No game code is included:
decompiled game code used for research stays outside this repository.
