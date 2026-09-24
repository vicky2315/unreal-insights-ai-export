# InsightsExport — native Windows GUI

Single-window WinForms app: headless Unreal Insights export (same as `export.sh`, no Git Bash needed) plus
analysis of the resulting `timerstats.csv` — run history, capture sanity checks, run-vs-run diff, widget cost tree.

## Build

Requires the .NET 9 SDK (only to build; the published exe is self-contained).

```bash
cd gui
dotnet publish -c Release
# -> bin/Release/net9.0-windows/win-x64/publish/InsightsExport.exe

cd ../gui.Tests
dotnet test
```

Quick dev run: `dotnet run` in `gui/`.

## Tabs

### Export

Pick a `.utrace`, an output folder, click **Export**. Same rules as `export.sh`:

1. Force-closes any running `UnrealInsights.exe` (overlapping instances silently break the export; the GUI asks first).
2. Writes `cmds.rsp` into the output folder (forward-slash paths).
3. Runs `UnrealInsights.exe -OpenTraceFile=… -ABSLOG=… -NoUI -AutoQuit -ExecOnAnalysisCompleteCmd=@=cmds.rsp -log`.
4. Succeeds once every requested CSV exists and its size is unchanged for 3 s. Otherwise kills and retries
   (default 5 attempts, default 30 min timeout per attempt — raise it for very large traces).

Differences from `export.sh`: deletes target CSVs before each attempt (a stale CSV can't pass as success);
doesn't depend on `-AutoQuit` actually quitting; Cancel kills the child process.

After a successful export you name the run (plus optional notes) and it is registered for the other tabs.

### Runs

Every registered export folder, with capture checks for the selected one. **Import folder(s)** registers an
existing export folder, or every immediate subfolder containing `timerstats.csv` (e.g. point it at a parent
`insights_out`). Removing a run only removes it from the list — files are never deleted.

Capture checks:

| Check | Rule | Why |
|---|---|---|
| No frame count / CPU channel off | `Slate::Prepass` missing, or count ≤ 1 | With the cpuprofiler channel off, Slate scopes export `count=1` — numbers unusable |
| Short capture | < 300 frames | Averages too thin |
| Background throttle | `FEngineLoop_UpdateTimeAndHandleMaxTickRate` max ≥ 250 ms and avg ≥ 50× median | Unfocused PIE ("Use Less CPU in Background") injects ~333 ms sleeps. Recapture focused + `t.MaxFPS 0` |
| Hitch scopes (info) | a call ≥ 16.6 ms, and the scope is rare (≤ 10 calls) or that call is ≥ 20× its median; scopes called > 2×/frame (render-graph passes, task waits) and the throttle timer are excluded | One-off spikes vs steady per-frame cost |

Thresholds are constants at the top of `Analysis/CaptureChecks.cs`.

### Diff

Pick run A (base) and B (new). Metrics: per-frame exclusive (default), per-frame inclusive, per-call exclusive
avg, per-call inclusive avg, call count. Per-frame = session total ÷ that run's frame count, so runs of
different length compare fairly. Filter by name, hide tiny scopes, show only-in-A / only-in-B, sort any column,
**Copy as Markdown**.

A yellow "directional only" banner appears when frame counts differ by > 20% or either run fails a check.
Per-call averages compare well across runs; whole-frame totals are noisy unless the scene is held fixed.

Widget scopes are matched across runs by instance path with the engine/game-instance prefix dropped
(`BP_GameInstanceBase_C_0` vs `_C_2` changes between PIE sessions).

### Widget Tree

Rebuilds UMG instance paths (`…:BP_GameInstanceBase_C_2.WBP_MainHUD_C_0.WidgetTree_0.WBP_TopBar…_Paint`)
into a tree. Each node shows subtree cost and its own cost split by scope suffix (`Paint`, `Tick`, …).
**Exclusive time only** — inclusive would count a child's cost again in every parent. "Merge instances" groups
`WBP_Card_C_0`, `WBP_Card_C_4`, … as `WBP_Card_C_*`. Widget scopes without a path (e.g.
`WBP_List [InvalidationBox_0]`) are listed under "(no instance path)".

## Git commit per run (opt-in)

Off by default — no git command ever runs until you click **Link...** on the Export tab and accept the consent
prompt. When linked, after each successful export the app runs, read-only, in that folder:

```
git rev-parse --short HEAD
git status --porcelain --untracked-files=no
```

with `GIT_OPTIONAL_LOCKS=0` (so `git status` doesn't refresh the index). Nothing is written to the repo. The
result (e.g. `b40221b4c1+uncommitted`) is logged and stored only in the run list. **Unlink** stops it
immediately; existing runs keep their recorded commit. Imported runs have no commit (not knowable after the fact).

## Files

Stored in `%APPDATA%\InsightsExportGui\`:

- `settings.json` — paths, export options, linked repo.
- `runs.json` — run list. If it's ever unreadable it is moved aside (`runs.json.corrupt-<time>`), never overwritten.

`UnrealInsights.exe` is auto-detected from the newest `C:\Program Files\Epic Games\UE_5.*` install if not set.

## Export pre-flight checks

- Trace path missing / not found / not `.utrace`.
- Trace still open for writing (UE session still recording) — warns, lets you continue.
- `UnrealInsights.exe` not found — prompts once to browse, then remembers it.
- Output folder missing (offers to create), not writable, or already containing the target CSVs (confirm overwrite).
- Paths with spaces are quoted UE-style (`-Key="C:\a b\x"`); non-ASCII paths write the rsp as UTF-8 with BOM.

## Tests

`gui.Tests/` (xUnit) covers the Analysis layer: CSV parsing, frame count, every capture check, widget path
parsing and tree sums, diff normalization/matching, run registry persistence. Fixtures are synthetic,
format-accurate CSV rows with anonymized names. UI is not tested.
