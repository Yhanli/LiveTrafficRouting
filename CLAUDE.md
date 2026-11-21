# Live Traffic Routing: notes for Claude

Cities: Skylines II **code mod** (C#, Unity ECS). Goal: congestion-aware routing based on live traffic
("navigation app" behaviour). Don't use "Google" or "Google Maps" in the mod name or description (trademark).

## Layout

- `LiveTrafficRouting/` is the mod project (net48, imports the toolchain's `Mod.props`/`Mod.targets` via `%CSII_TOOLPATH%`).
  Same structure and conventions as the sibling `..\MouseLight` (Better Cursor Light) project.
- `docs/research.md` holds the findings about vanilla pathfinding and traffic flow, plus the implementation plan. Read it first.
- `tools/build.ps1` builds and deploys. It refuses to run while `Cities2` is running.
- `UI/` is the in-game statistics panel: a UI module (React + TypeScript, from the game's own template in
  `Cities2_Data/Content/Game/.ModdingToolchain/npx-create-csii-ui-mod`). `npm run build` writes `UI/build`
  (webpack output changed from the Mods folder to `build`), and the csproj target `DeployUIModule` copies it into
  the mod folder after the toolchain's deploy (which deletes the folder first). `build.ps1` runs both.
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
- Source files use CRLF line endings; scripted find/replace must account for `\r\n`.
- `dotnet` is not on PATH; use `C:\Program Files\dotnet\dotnet.exe` (the build script does).
- No automated tests. Verification happens in game: logs at `...\Cities Skylines II\Logs\LiveTrafficRouting.log`.
- Only publish when the user asks.
