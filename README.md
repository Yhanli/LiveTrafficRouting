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
| Relaxed | Drivers only avoid bad jams and mostly stay on main roads. |
| Balanced (default) | Drivers starting a trip avoid jams within about 10–20 game minutes. |
| Eager | Drivers react fast and take side streets to dodge any jam. Can push traffic into residential streets. |
| Custom | Your own mix. Chosen automatically when you change a slider. |

The sliders are always visible (greyed out when Off). Picking a preset fills them in with these values; changing any
slider switches the dropdown to Custom:

| Slider | Meaning | Relaxed | Balanced | Eager |
|---|---|---|---|---|
| Right now vs usual traffic | 100% = only current traffic, 0% = only the usual traffic for this time of day (vanilla) | 50% | 75% | 100% |
| How quickly jams are noticed | Higher = notices new and cleared jams sooner, but traffic may swing between roads | 30% | 50% | 75% |
| Jam penalty strength | Multiplies the extra time a jam adds (200%: twice as slow looks three times as slow) | 75% | 100% | 150% |
| Avoid jams up to (times slower) | The worst a jammed road can look vs driving it at the limit | 4× | 10× | 20× |
| Drivers who re-route while driving | Share of drivers with live navigation (0% = no mid-trip re-routing) | 15% | 30% | 60% |
| Re-route when the way ahead gets slower by | How much worse the rest of the route must get vs when planned | 50% | 30% | 20% |

**Write traffic summary to log** (off by default) writes a summary to `Logs/LiveTrafficRouting.log` about every 45 game minutes.

## Statistics (with the mod on and off)

The mod measures traffic every game minute, **also when it is set to Off**, so you can compare the game's own routing
("Native") with live traffic ("Live"). Each game hour is saved as one capture, tagged with the mode, to
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

Where to see them:
- **In game:** the traffic icon in the top-left opens the **Live Traffic** panel: right now, Native vs Live averages
  for this city (all hours, or **same times of day only**, so a Native night isn't compared with a Live rush hour),
  a history chart as a **timeline** or **by time of day** (Native and Live side by side per hour), filterable by
  **All / Native / Live traffic** with a choice of metric, and re-routing.
  Close the panel to collapse it back to the icon.
- **Options > Live Traffic Routing > Statistics:** the same key numbers as text.
- **Log:** with "Write traffic report to log" on, one line per game hour in `Logs/LiveTrafficRouting.log`.
- **CSV:** open `captures.csv` in a spreadsheet and filter the `mode` column.

The mod adds nothing to save files. It can be removed at any time.

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
