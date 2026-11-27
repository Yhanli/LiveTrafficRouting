---
name: review-test-session
description: How to review a Live Traffic Routing test session from the game's logs and the ModsData CSV files (captures, hotspots, bottlenecks, links), and how to plan A/B runs. Use when the user asks how a run looks, to analyse results, to look for gaps or odd numbers, or to plan the next test.
---

# Reviewing a test session

## Where the data is

`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\`
- `Logs/LiveTrafficRouting.log` (this session) and `Logs/LiveTrafficRouting-prev.log` (the previous one: the game
  rewrites log files on every start, so read the log **before** the user restarts, or use -prev).
- `Logs/Modding.log`, `Player.log`, `Player-prev.log` for exceptions from the game.
- `ModsData/LiveTrafficRouting/`: `captures.csv` (one row per game hour, columns by name), `hotspots.csv`,
  `bottlenecks.csv`, `links.csv` (append-only; a header change renames the old file to `.old.csv`),
  `overlay-style.txt`, `overlay-preview.json`, backups (`*-backup-*.csv`, 5 kept).

Write analysis scripts to the scratchpad as files (heredocs with quotes break), read CSVs with `csv.DictReader`
(`encoding='utf-8-sig'`), group by `run_id`, and treat `-1` / empty as "not measured".

## What to check, every time

1. Errors: grep the mod log and Player logs for exception / error / warn / "could not".
2. Runs: settings, hours, game time span, and whether runs of different settings start from the same save time.
3. Per run averages of the key metrics, and the first game hour separately (differences already in hour 1 can't come
   from routing getting better over time).
4. **Does it add up?** Cross-check metrics against each other: e.g. fewer cars on the road must match faster trips
   (Little's law: cars = trips x trip time); if not, look for people changing travel mode, trips not happening, or a
   counting bug. Flat columns (always 0 or one value) usually mean a broken metric.
5. The detailed log's extra lines: speed-limit breakdown (local-street rule), "Cars removed by the game ... by road",
   bottleneck hours and the ongoing ranking.
6. Bottlenecks: is the #1 place stable, is its cause a real one ("slow traffic" = nothing matched: something we don't
   detect yet), do fix ideas make sense, do removal places match the bottlenecks.

## Known pitfalls (from earlier sessions)

- Run-to-run noise is large; one run per setting can't show differences under ~10%. Prefer the Compare tab's
  "same game time" view and the significance badge.
- The route planner also picks the travel mode: a stronger congestion cost makes people walk instead of drive. Always
  compare car share of travellers and people walking, not only queues.
- Bicycles are personal cars in the game; group ("master") lanes are not real lanes; speed limits are stored at ~2x
  the shown value. See CLAUDE.md "Game data gotchas".
- Results recorded before a metric fix are not comparable with after (see `docs/decisions.md`).

## Reporting

Lead with what matters (a finding that changes conclusions first), with the numbers that show it. Separate "broken
metric" from "real effect" from "worth a look". Say plainly when an earlier explanation was wrong. Record lasting
findings in `docs/decisions.md` (choices, results) or `docs/research.md` (how the game works), and update its
"Open / next" list.

## Planning A/B runs

Same save, same game speed, ~49 game hours per setting (Native, Balanced, Eager), statistics panel and detailed log on.
Never change the save itself; use a copy for network changes. The game must be closed for deploys.
