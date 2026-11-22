# Live Traffic Routing

A Cities: Skylines II code mod (work in progress). Drivers react to **live** congestion, the way a navigation app with
real-time traffic would route you: jammed roads get slower in the route planner, so new trips avoid them, and some
drivers already on the road switch to a faster way when their route jams up.

The game already makes roads with slow traffic less attractive, but it learns this very slowly (over game days) and
per time of day, like "typical traffic". This mod adds a live measurement of the last few game minutes and blends it in.

Background and design: [docs/research.md](docs/research.md).

## Status

- **Milestone A (done, needs in-game testing):** live congestion signal. Replaces the game's `TrafficFlowSystem` with
  `LiveTrafficFlowSystem`. The traffic flow info view and city statistics work as before.
- **Milestone B (built, needs in-game testing):** mid-trip re-routing (`RerouteSystem`). Drivers with "live navigation"
  re-plan when the rest of their route looks a set % slower than when they planned it, or when they are heading
  through a standing jam (route ahead slower than free flow by twice that %). Vehicles that find no better route wait
  longer before checking again. Covers cars, delivery trucks
  (including semi-trailers) and taxis, including through-traffic. Public transport keeps to its line; service vehicles
  are not covered yet.

## Settings (Options > Live Traffic Routing)

**How drivers react to traffic jams** (one dropdown):

| Choice | What you'll see |
|---|---|
| Off (game default) | The game's normal behaviour. |
| Relaxed | A light touch: drivers avoid the worst jams and stay on main roads. Fewer jam warnings, little else changes. |
| Balanced (default) | Drivers react quickly; 3 in 4 re-route mid-trip. Tested: queues -28%, speed +3%, jam warnings -87%, local streets +10%. |
| Eager | Strongest: a jammed road is treated almost as closed, every driver re-routes. Tested: queues -38%, speed +6%, but local streets +33%. |
| Custom | Your own mix. Chosen automatically when you change a slider. |

The sliders are always visible (greyed out when Off). Picking a preset fills them in with these values; changing any
slider switches the dropdown to Custom:

| Slider | Meaning | Relaxed | Balanced | Eager |
|---|---|---|---|---|
| Right now vs usual traffic | 100% = only current traffic, 0% = only the usual traffic for this time of day (vanilla) | 75% | 100% | 100% |
| How quickly jams are noticed | Higher = notices new and cleared jams sooner, but traffic may swing between roads | 50% | 80% | 100% |
| Jam penalty strength (25-1000%) | Multiplies the extra time a jam adds (200%: twice as slow looks three times as slow) | 100% | 250% | 1000% |
| Avoid jams up to (2-100×) | The worst a jammed road can look vs driving it at the limit (the pathfinder's own limit is 256×) | 10× | 50× | 100× |
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
| On local streets | Vehicles on roads with a limit of 50 km/h or less (detour traffic in neighbourhoods) |
| Traffic flow | The game's own Traffic Flow % (the info view chart) |
| With navigation vs others | How fast vehicles get closer to their destination, for the drivers with live navigation vs everyone else. Same city, same time, so it is a fair comparison. With the mod off both should be about equal. |
| Re-routing | Re-routes by cars / trucks / taxis, share that found a faster route, estimated time saved |
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
- **Log:** with "Write traffic report to log" on, one line per game hour in `Logs/LiveTrafficRouting.log`.
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
