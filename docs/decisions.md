# Decision log

What we chose to build (or not), why, and what changed our view. One place for decisions from all versions; add to it
whenever a design decision is made or a test changes our understanding, and keep "Open / next" current.

**Where things live**
- `README.md`: what the mod does, settings, panel metrics (for players and contributors).
- `CLAUDE.md`: how to work on the repo (layout, build, preview, UI gotchas, git, publishing).
- `docs/research.md`: how the game works (decompiled code, data we read), and how each system is implemented.
- `docs/decisions.md` (this file): the choices and the reasons, by topic, with the version or date they were made.
- `LiveTrafficRouting/Properties/PublishConfiguration.xml`: mod page text and changelog.
- `docs/releases/`: release notes drafts and the checklist before publishing.
- `.claude/skills/`: how we build the panel and overlay, and how we review a test session (read by Claude).

## Routing cost and presets

- **Congestion goes into travel time, not speed** (v1.0). Mixing the live speed with the usual speed capped a standing
  jam at about 4x slower (1/(1 - live share)), so jams barely changed routes. Mixing travel times lets a jam cost what
  it really costs. Details in research.md section 5A.
- **Replace vanilla's flow system instead of running alongside it** (v1.0): vanilla rewrites `FlowOffset` whenever its
  value differs, so two writers would fight. Our system keeps everything vanilla did (info view, city stats).
- **Penalty range: strength up to 1000%, cap up to 100x** (v1.0). At 300%, Eager still felt too weak in game; the A/B
  tests then showed the extra range is used (Eager = max).
- **Preset values come from A/B tests** on one 100k save against Native (v1.0): old Balanced had little effect (became
  Relaxed); old Eager gave queued -28%, speed +3%, jam warnings -87%, local streets +10% (became Balanced); max gave
  queued -38%, speed +6%, local streets +33% (became Eager). `ReapplyPickedPreset()` moves players to new values.
  Local-street numbers here predate the speed-limit fix (see Measurement), so they were measured on almost no roads.
- **A forecast of planned traffic instead of lane occupancy** (v1.1): an avoided lane gets no speed samples and resets
  to free flow, so every driver flees to the same detour until it jams too. Planned demand sees the herd coming.
- **Not built: alternative destinations** (v1.0). Trip purposes of queued cars: 78% going home, 14% commuting, 0%
  leisure or shopping, so almost no one in a queue could switch destination. Destination metrics stay in the panel.
- **The mod only helps where another route exists** (v1.0): a cargo yard with every truck at one entrance was a road
  network problem (no east-west link), and the mod page says so. That case led to the bottleneck finder (v1.1).

## Re-routing

- **Who re-routes:** cars, delivery trucks and taxis (v1.0); emergency vehicles on a run, with their own budget (v1.1).
  Public transport keeps its lines (fixed stops, and it ignores flow anyway). Garbage, post, hearse and maintenance
  vehicles are not covered (routes through several stops).
- **Re-route budget follows the route planner** (v1.1): 8-128 per run from how many new paths come back within one run.
  It stayed at 128 in the 2026-11 tests, so the planner is not overloaded.
- **Wait for a junction with a real choice** (v1.1: first for "route got worse", then also for "standing jam" after the
  2026-11-26 test), and
  never re-route inside a junction or first in line at it. A car in a jam with no choice ahead can't use a new route;
  in the first test half of the jam re-routes found nothing faster. After the change: ~80% of re-routes still come
  from the jam rule, ~52% of those find a faster route. **This rule is where tuning pays off next.**

## Measurement and statistics

- **CSV files, read by column name** (v1.0). Rejected SQLite (a native library to ship and keep working with the game's
  runtime) and storing in the save (principle: nothing in the save, so the mod can be removed any time). Players choose
  how long to keep statistics (1 game month to unlimited; 10 years and unlimited show a warning); the newest Native run
  per city and pinned runs are never trimmed. Nothing is recorded while the panel is off.
- **A run = mode + full settings** (v1.0); a settings change starts a new run and the partial hour is dropped, so runs
  compare like with like. The significance check (paired / Welch t-test) shrinks the sample size for lag-1
  autocorrelation, because neighbouring game hours are alike: plain t-tests called noise "real".
- **Cost baseline** (v1.0, 100k city, game speed 4): simulation speed -0.0%, FPS -0.4%, the mod about 1% of one CPU
  core and about 5 MB. Game memory from Unity's `Profiler.GetTotalReservedMemoryLong` (the process API returns 0 in the
  game's runtime). In the 2026-11 tests: Balanced ~5 ms per run (~2% of a core), Eager ~10 ms (~3.4%).
- **Performance review before 1.1** (2026-11-27). Routing runs for everyone; everything else only with the statistics
  panel on. Measured: mod 0.8-3.4% of one core, simulation speed within noise of Native (Eager about -2%), bottleneck
  finder 0.8-2.8 ms per sample (every 1024 frames). Changed: the queue walk remembers each car's head, so a sample walks
  every queue once (was once per queued car, up to cars x 256 steps in gridlock); the overlay rebuilds its shapes only
  on a change of view, style or zoom (3%+), or every 0.5 s, not every frame. Watch: the panel's history JSON (up to
  1,440 game hours, roughly 2 MB) is rebuilt and sent to the UI once per game hour while statistics are on.
- **Travel mode: count everyone on the move** (2026-11-27). Eager had 30% fewer cars than Native already in the first
  game hour, while people on public transport and shopping trips stayed flat; faster trips explain ~10%, not 30-50%.
  The route planner also picks the travel mode (research.md section 9), so higher car-lane costs make people leave the
  car. Added: car share of travellers, people walking, cycling, waiting for public transport or a taxi, waiting for a
  route. Panel notes no longer say fewer cars = faster trips.
- **Bicycles are not cars** (2026-11-27): the game builds them as personal cars, so every car query excludes them and
  their riders are counted apart. Passenger and despawn counts in earlier test builds included cyclists.
- **Local streets = shown limit 50 km/h or less = stored 100 km/h** (2026-11-27). Speed limits are stored at about
  twice the shown value; the cutoff was 13.9 m/s, so "On local streets" was ~1% and **"Keep out of neighbourhoods"
  never fired in any test** (v1.1 included). It is now 27.8 m/s. The neighbourhood penalty is untested and untuned.
  The hourly speed-limit breakdown in the detailed log stays as a check.
- **Despawns: count them, and where** (2026-11-26/27): per hour (stuck / no route / done) and per place; lots and paths
  only as a total. The worst removal places were not the #1 bottleneck, so check both.
- **Neutral metrics** (2026-11-26): Local streets and Public transport share are not coloured good or bad, each with a
  note. More spreading isn't automatically better or worse.
- **Keep the previous log** (2026-11-27): the game rewrites log files on start, which lost a whole test session once.
  The mod copies it to `Logs/LiveTrafficRouting-prev.log` before logging starts.

## How we test

- **A/B on one save, one run per setting, ~49 game hours each** (2026-11-26): Native, then Balanced, then Eager, compare
  in the panel (Compare, "same game time"). The detailed log and the CSVs record everything per game hour, so one
  session tests several features. Results of the first round (save "Testing v1.1-1.2"): Balanced vs Native jam warnings
  -64%, queued -19%, speed +8%; Eager queued 7.3% vs 10.4%, stuck despawns 16 vs 31 per hour.
  - **Caveats found later:** run-to-run noise is large (public transport share 18-27% across four Balanced runs), so
    single-run differences under ~10% aren't conclusive; part of the gain was people leaving the car; and the
    neighbourhood rule was off. **Re-run Native / Balanced / Eager with the current build.**
- **Look at the UI without the game** (2026-11-26): panel and overlay previews (see CLAUDE.md, "Previews without the
  game"). The real check is still in game.
- **Saves are never changed by the mod**; tests that need a different road network use a copy of the save.

## Bottleneck finder (v1.1)

How it reads the game is in research.md section 8. The choices:

### What counts as the problem

- **The head of the queue, not where cars stand**: fix the start of a queue. Stop-and-go is one queue (the car ahead
  counts while under max(3 m/s, 40% of its limit)); before that, crawling queues broke into short chains with heads
  mid-street.
- **Road joints are not places**, and queues from several streets that end at one junction are one problem, named after
  the junction (ranking).
- **Ongoing ranking over 24 game hours, with thresholds** (2026-11-26): at least 5 stuck cars per hour on average, in at
  least a quarter of the hours. One-hour flashes and normal queuing don't show.
- **Status: cleared / getting better / getting worse** (2026-11-27). A fix used to stay listed for up to 24 hours.
  Cleared = no queue for 3 game hours (moved to the bottom, "fixed?"); better / worse = last 3 hours vs the average.

### Causes

- From the head's blocker: traffic lights, turning across oncoming traffic, cars crossing their path, cars entering
  from buildings; give way / stop sign, busy junction (3+ roads), parking.
- **People crossing** (2026-11-27), at a crosswalk or where there is none (jaywalking). Before, these queues showed as
  busy junction or slow traffic.
- **Lanes merge / cars merging onto the highway** (2026-11-27). Before, only two road pieces with different lane counts
  counted, so a 3-to-2 merge right before the Ashbourne junction showed as "slow traffic 90%" for days.
- **Buses or trams at a stop, service vehicles stopping in the lane** (2026-11-27).
- **"Slow traffic" means nothing else matched**: a high share points at something not detected yet, not an answer.

### Lanes and junction advice

- **Leave group ("master") lanes out of every lane count** (2026-11-27): the game adds one per group of lanes cars can
  change between, with all their turns, so it read as an extra lane ("3 lanes; lane 2 can turn left or go straight or
  make a U-turn" on a 2-lane approach where lane 2 can only go straight or U-turn). Routes that name a group lane map
  to its first real lane.
- **Count lanes where they reach the junction** (2026-11-27), not road lanes: a 2-lane approach (3 merging into 2) read
  "3 lanes, 2 can turn left, 3 can go straight". **List lanes from the left as the driver sees it**, with what each can
  do, so players can check them against the road. Left/right are always the driver's view; the queue line has arrows.
- Advice is a problem sentence, then "Try: ...", at most 4 per place; specific advice replaces the general one for its
  cause. Rules: few lanes for the main turn; one lane holds most of the queue; turning is slow (per-lane throughput,
  turns vs straight; found when the Ashbourne queue cleared once both lanes could turn); lanes merge right before the
  junction; shared turn-and-straight lane blocked by cars turning across oncoming traffic; turn lane too short; the
  turn runs into fewer lanes after the junction, or the next road loses a lane within 200 m; the queue reaches back
  into the previous junction (how gridlock starts); mostly trucks (50%+), naming where they come from.
- Per-hour turn counts are small (10-50 per lane in a jam): a game hour is only ~3 minutes of driving.

### Fix ideas (new roads, missing turns, turn-arounds)

- **A fix must let drivers skip a listed bottleneck** (2026-11-27). Before, any queued car's detour anywhere on its route
  counted, and ideas stayed listed with every bottleneck cleared (a winding cargo-yard road was suggested). Now the long
  way round must pass within 80 m of the head of the driver's own queue; each idea belongs to its bottleneck, shows in
  that place's details under "What you can do", and goes when the place is cleared or off the list.
- Detours: two route points <= 400 m apart that the route takes >= 1 km and 3x to connect; at least 10 drivers per hour;
  seen in a quarter of the window; merged within 200 m; at most 2 per place.
- Turn-around only when the route comes back the opposite way on the same street and uses no highway (a highway loop
  ramp also faces back). Missing turn when the ends are within 3 road pieces; otherwise a new road (ramp if it meets a
  highway). Same street at both ends reads "shortcut across a bend of X". "A to B" and "B to A" are one idea.

## Map overlay

- **Long coming-from / going-to arcs** (2026-11-26, user preference): they show clearly where traffic comes from and
  goes to. Short direction stubs were tried and dropped. Arcs end at the ring, at the ring's height.
- **The ring marks where the queue enters the junction**, not the junction's centre point (far from the problem on a
  wide junction) (2026-11-27).
- **Only the turns the queue uses**, within 120 m of the ring; unused turns made busy junctions unreadable.
- **Widths follow the camera** (2026-11-27): thinner up close (down to 12%), wider far out (up to 12x) so lines keep
  their size on screen.
- **Outlines in two passes** (all outlines, then all fills): piece by piece, every joint of a route showed a dark dot.
  Lines along a lane are hidden where cars and labels sit on them (the overlay is painted on the road); accepted.
- **Style file** `ModsData/LiveTrafficRouting/overlay-style.txt`, re-read while the game runs. Changing defaults bumps
  `kStyleVersion`, which replaces the player's file once (the old one is kept as `overlay-style.old.txt`).
- **Clicking a name moves the camera, it doesn't select**: selecting made the pin hop between a road's label positions.

## Panel wording

- Plain words; **"What we see"** (observations, problems in yellow) separate from **"What you can do"** (bullets).
  Testers couldn't tell an observation from a recommendation.
- Every street or place name in the details is a clickable chip; a colour legend matches the map.

## Publishing and feedback

- **Feedback goes to GitHub issues** (v1.0). Paradox Mods only accepts a Paradox forum address as ForumLink; GitHub is an
  `ExternalLink`.
- **The mod page says "navigation app"**, never the trademarked name; `tools/check.ps1` enforces it.
- **Publish and commit only when asked**: both act under the user's own accounts.
- **Everything after 1.0.0 is one release, 1.1.0** (2026-11-27): the planned 1.2 (bottleneck finder) was folded into
  1.1, since none of it was published. Draft and checklist: `docs/releases/v1.1.0.md`.

## Open / next

- Re-run the A/B (Native, Balanced, Eager) on the current build: car share, people walking, local streets, queued %.
- Decide whether the mode shift is acceptable (it is realistic) or the congestion cost should steer only route choice,
  not mode choice. Needs the Eager numbers first.
- Tune "Keep out of neighbourhoods" now that it is active.
- Check the new causes and advice in game (people crossing, lane and highway merges, buses / service vehicles, junction
  rules). Confirm that cargo trucks are `DeliveryTruck` (the truck share relies on it).
- Ideas not built: weaving between close ramps (only the merge itself is detected); drawing the route at the lane edge
  so cars don't hide it; re-routing garbage / post / maintenance vehicles.
