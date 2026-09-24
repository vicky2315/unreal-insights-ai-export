# Analysis Guide

How to read the CSV files this tool makes, and how to use them with an AI agent.

## timerstats.csv

One row per timer or scope. All times are in seconds.

| col | meaning |
|-----|---------|
| 1 | Timer name. For widget scopes this is the full path, not just the class name (see below) |
| 2 | Count: how many times this scope ran |
| 3 | Count again, as a float |
| 4 | Inclusive total time |
| 5 | Inclusive min time |
| 6 | Inclusive max time |
| 7 | **Inclusive average**: cost per call, including children. Most useful column |
| 8 | Inclusive median |
| 9 | Exclusive total time |
| 10 | Exclusive min time |
| 11 | Exclusive max time |
| 12 | **Exclusive average**: cost of just this scope, not its children |
| 13 | Exclusive median |

To get milliseconds, multiply by 1000. To tell if a scope runs once per frame, compare its count to a known per-frame scope's count (for example `Slate::Prepass` in a UMG/Slate UI). If the counts are close, it runs every frame. If much lower, it runs on events only.

**Widget name gotcha:** for UMG widget scopes, column 1 is not just the class name like `WBP_Foo_C`. It is the full instance path, for example:
`WBP_Lord_C /Engine/Transient...:BP_GameInstanceBase_C_2.WBP_MainHUD_C_0.WidgetTree_0.WBP_Foo_0..._Tick`

If you search for `WBP_Foo` and grab the first word, you will double count and mislabel things. Instead, match the start of the line (`^Name_C `, the class name plus a space). This path also shows you the widget hierarchy for free.

## threads.csv

Maps thread ID to thread name. You need this because `frame-drill.sh` filters by thread **name** (like "GameThread"), not by number.

## counters.csv

Named counters, if you asked for them (memory usage, custom stats, etc).

## Finding one slow frame

`timerstats.csv` only shows totals. It cannot tell you what happened in one specific frame. For that, use `frame-drill.sh`:

```bash
./frame-drill.sh map --trace mytrace.utrace --out ./out/hitch
# read out/hitch/prepass.csv, find the frame with a big gap or big duration
./frame-drill.sh window --trace mytrace.utrace --out ./out/hitch --start 329.78 --end 330.05 --min-ms 1.0
# read out/hitch/window_events.txt, slowest events first
```

The slowest, deepest rows show you the call chain. Names like `SAssetView [SContentBrowser.cpp(3407)]` tell you exactly which file and whether it is your game code (`WBP_...`) or engine/editor code.

## Using this with an AI agent

This tool works well with an AI coding agent like Claude Code, but you can also read the CSVs yourself.

Steps:

1. Run `export.sh` (or `menu.sh`) to get `timerstats.csv` and `threads.csv`.
2. Give the agent this file plus the CSVs. Ask it to:
   - sort by exclusive average or exclusive total, to find the most expensive scopes
   - compare counts to a known per-frame scope, to separate per-frame costs from one-time costs
   - if something looks like a spike, run `frame-drill.sh` and give it `window_events.txt`
3. To compare before and after a fix: run `export.sh` twice into two folders, then ask the agent to diff the two `timerstats.csv` files on matching timer names.

The CSV format is plain text on purpose, so an LLM can read it directly with `awk` or just by looking at it. No custom parser needed. See `CASE_STUDY.md` for a full example of this workflow.

## Limits

- **Export can fail silently.** The `-AutoQuit` flag can quit before the CSV finishes writing on large traces. `export.sh` retries this for you. If it still fails, check the log, or run with `--retries` set higher.
- **Two copies of UnrealInsights.exe running at once break the export.** Both scripts close stray copies before running. If you run `UnrealInsights.exe` by hand elsewhere, close it first.
- **No full timeline by default.** `timerstats.csv` gives totals only. Use `frame-drill.sh` for one window at a time; it is slow on big traces.
- **No cause, only cost.** The CSV tells you what is slow, not why it was called. You (or the agent) still need to read the source code.
- **Column layout is reverse engineered.** Epic does not document it. Check it still matches this guide if you move to a new engine version.
