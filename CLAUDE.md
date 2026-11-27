# Live Traffic Routing: notes for Claude

Cities: Skylines II **code mod**, published on Paradox Mods as **ModId 168683** (C#, Unity ECS). Goal: congestion-aware routing based on live traffic
("navigation app" behaviour). Don't use "Google" or "Google Maps" in the mod name or description (trademark).

## Working with the user (keep this current)

This section, the project skills and `docs/decisions.md` carry what we agreed, so the user doesn't have to repeat it.
When the user corrects or prefers something new that will matter again, add it here or to the right skill in the
same turn, briefly.

- **Project skills** (`.claude/skills/`): `panel-and-overlay-ui` before changing panel text, layout, colours or the map
  overlay; `review-test-session` before analysing logs / CSVs or planning a test. Extend them as we learn.
- **The loop:** the user tests in game and sends screenshots. While the game runs, only compile to a scratch folder
  (see Build and test); deploy with `tools\build.ps1` when the user says the game is closed or asks to deploy, then
  say what to check in game. Never touch the user's saves.
- **Answer from evidence:** check the decompiled code, the game data or the logs before explaining behaviour, and say
  plainly when an earlier explanation was wrong. Lead with the finding that changes conclusions.
- **Plain language** in the UI and in replies; observations separate from recommendations; key signals visible
  (badges, colours), not buried in text. Details in the `panel-and-overlay-ui` skill.
- **Don't flood the player:** thresholds, rankings over time, and suggestions tied to a real problem (they go away
  when it's fixed). Show when a fix worked.
- **Record decisions** in `docs/decisions.md` (by topic, with date or version) and game facts in `docs/research.md`.
- **Versions:** everything after 1.0.0 is 1.1.0 until it is published. Commit only when asked; never push (the user
  pushes from their own terminal); publish only when asked.

## Game data gotchas (each one cost a wrong result once)

- Bicycles are `PersonalCar` + `Bicycle`: exclude `Bicycle` in every car query; count riders apart.
- Group lanes: where cars can change lanes, edges and nodes also hold a `MasterLane` (union of its group's turns, own
  end node). Leave it out of every lane count; routes may name it, map it to its first real lane (`LaneDiagnosis.RealLane`).
- Road speed limits are stored at about **twice the shown value** (`CarLane.m_SpeedLimit`): local street = stored
  <= 27.8 m/s.
- Lanes that merge inside a road piece end at the same path node (`Lane.m_EndNode`); count lanes at a junction by
  distinct end node.
- A car stopped for a pedestrian has the person as `Blocker` with type `Temporary`; a stopped bus at a stop is itself
  the head of a queue chain.
- The route planner also chooses the travel mode (car / transit / walk), so car-lane costs change how people travel.
- Waiting at a stop is `ResidentFlags.WaitingTransport`; the game rewrites log files on start (the mod keeps -prev).
- More in `docs/research.md` sections 8-9.

## Change checklist

- Version: `Mod.Version` must match `ModVersion` in PublishConfiguration.xml (`check.ps1` checks); changelog entry
  and mod page text in the same file.
- New capture column: `TrafficCapture` field (default -1 = not measured), `Columns`, JSON key, log text, panel type /
  metric, `UI/preview/make-sample-history.py`. Old CSVs upgrade by header name.
- `bottlenecks.csv` / `links.csv` header change: the old file is renamed `.old.csv` automatically.
- Overlay style defaults changed: bump `kStyleVersion`.
- Panel: `npx tsc --noEmit -p .` + `npm run build` in `UI/`; update `UI/preview/sample-data.js`; check in the preview.
- Edits by script: write the Python script to the scratchpad and run it (inline heredocs with quotes break); assert
  each find string matches exactly once.
- Run `tools\check.ps1`; update README / docs / decisions when behaviour changes.

## Layout

- `LiveTrafficRouting/` is the mod project (net48, imports the toolchain's `Mod.props`/`Mod.targets` via `%CSII_TOOLPATH%`).
  Same structure and conventions as the sibling `..\MouseLight` (Better Cursor Light) project.
- `docs/research.md` holds the findings about vanilla pathfinding and traffic flow, plus the implementation plan. Read it first.
- `docs/decisions.md` is the decision log: what we chose and why (testing method, metrics, bottleneck causes and
  advice rules, overlay design, open items). Add to it when a design decision is made or a test changes our view.
- `tools/build.ps1` builds and deploys. It refuses to run while `Cities2` is running.
- `UI/` is the in-game statistics panel: a UI module (React + TypeScript, from the game's own template in
  `Cities2_Data/Content/Game/.ModdingToolchain/npx-create-csii-ui-mod`). `npm run build` writes `UI/build`
  (webpack output changed from the Mods folder to `build`), and the csproj target `DeployUIModule` copies it into
  the mod folder after the toolchain's deploy (which deletes the folder first). `build.ps1` runs both.
  **Game UI (Coherent Gameface) gotchas:** every element is a flex box, so text pieces mixed with `{values}` in one
  element stack on separate lines: build each line as ONE template string. The HTML `title` attribute does nothing;
  use `Tooltip` from `cs2/ui`. Overflowing content is clipped, not scrolled: keep each tab short.
  A row of items side by side needs `display: flex` in its class, not only `flex-direction: row` (chips and the
  map legend stacked one per line in game without it; the browser preview does not hide this any more).
  C# side: `TrafficUISystem` (bindings group `liveTrafficRouting`: `current`, `history`, `averages`, JSON strings).
- Statistics (only while the "Statistics panel" setting is on): `TrafficReportSystem` samples every 1024 frames and
  records one `TrafficCapture` per game hour, tagged with a run (new run on load, delete, or any settings change;
  the partial hour is dropped), saved to `ModsData/LiveTrafficRouting/captures.csv`. The CSV is read by header name
  (add columns in `TrafficCapture.Columns`; old files upgrade once with a backup). Size cap 20k rows -> archive.
  All comparisons are computed in the panel (TypeScript) from the `history` and `runs` bindings.
- Hotspots: the sampling jobs map each queued vehicle / jam warning lane to a place (`Owner` -> `Aggregated.m_Aggregate`
  street, or the node for junction lanes); names via `NameSystem.GetRenderedLabelName`. Top 15 per hour go to
  `hotspots.csv` (keyed by place name). Deleting runs deletes their hotspot rows too.
- `PopupGuard` turns off `SharedSettings.instance.userInterface.blockingPopupsEnabled` while recording (option
  `KeepRunningOnPopups`) and restores the player's value when the option/panel goes off or the mod unloads.
- Preset values may change between versions: `ReapplyPickedPreset()` after loading keeps users on their preset.

## Previews without the game

- Panel: `cd UI; npm run preview` builds `UI/preview/dist` (the game's `cs2/api` and `cs2/ui` replaced by stand-ins in
  `UI/preview/`, sample data in `preview/sample-data.js`). Serve `UI/preview` (e.g. `python -m http.server 8765
  --directory LiveTrafficRouting/UI/preview`, launch config "panel-preview") and open `index.html#Causes` (any tab
  label after `#`). Flex-box layout and 1rem = 1px imitate Coherent; check the real thing in game before release.
- Map overlay: `overlay.html?file=overlay-preview.json` draws the shapes the game exported (copy the file from
  `ModsData/LiveTrafficRouting`); `overlay-sample.json` is a made-up junction. Style tweaks go in
  `ModsData/LiveTrafficRouting/overlay-style.txt` and apply in game within a second (no restart).

## Decompiled game code

- `..\_decompiled\Game-1.6.4f1\` is Game.dll decompiled with `ilspycmd` (installed as a dotnet global tool, v9.1, which works with the .NET 8 SDK).
  Reference only: **never copy large chunks into this repo, never commit or publish it.**
- After a game update, decompile again into a new versioned folder and diff `TrafficFlowSystem`, `PathUtils`, `CarNavigationSystem` and `PathfindQueueSystem`.

## Build and test

- **Never run `dotnet build` directly while the game is running**, not even "just to compile": `Mod.targets` deletes the
  deployed Mods folder first and fails on the locked DLL, which leaves the installed mod broken for the next game start.
  To only check that it compiles while the game is open, redirect the deploy to a scratch folder:
  `dotnet build LiveTrafficRouting\LiveTrafficRouting.csproj -c Release -p:LocalModsPath=<scratch dir>`
  (`DeployDir` is `$(LocalModsPath)\$(TargetName)`, and a command-line property can't be overridden by the targets).
  Deploy for real with `tools\build.ps1` once the game is closed.
- Line endings: git has `core.autocrlf=true`, so working files can be CRLF or LF; scripted find/replace must handle both
  (normalise to `\n`, edit, write back the original style).
- `dotnet` is not on PATH; use `C:\Program Files\dotnet\dotnet.exe` (the build script does).
- No automated tests. Verification happens in game: logs at `...\Cities Skylines II\Logs\LiveTrafficRouting.log`
  (overwritten on each game start; the mod copies the previous one to `LiveTrafficRouting-prev.log` first).
- Bicycles are `PersonalCar` entities with a `Bicycle` tag (`PersonalCarAISystem` drives them): exclude `Bicycle` in
  every car query and count their riders apart.
- Run `tools\check.ps1` before committing (publish config, image sizes, a text for every option and dropdown value,
  no "Google" anywhere).
- Publishing: only when the user asks; it uploads under their Paradox account. `tools\publish.ps1` runs the checks,
  refuses uncommitted work or an existing vX.Y.Z tag, uses PublishNewMod while ModId is empty (then put the new id in
  PublishConfiguration.xml) and PublishNewVersion afterwards, and tags the commit. AccessLevel is Public (released).
- Images: `tools\make-thumbnail.ps1` and `tools\make-explainers.ps1` (run with `powershell.exe -STA`), and
  `tools\convert-screenshots.ps1` for in-game screenshots (to JPG).

## Git

- `origin` = https://github.com/Yhanli/LiveTrafficRouting (branch `main`). Feedback and bugs go to its issues
  (linked from the mod page description and the README; ForumLink only accepts a Paradox forum url).
- This repo's `user.email` is the GitHub noreply address; GitHub rejects pushes that expose the private email, so don't
  change it. The local branch `backup/before-email-rewrite` still has the old address: never push it (push `main` only).
- `git push` from the agent shell can't prompt for credentials; the user pushes from a terminal where Git Credential
  Manager is signed in.
- Commit only when the user asks.
