# unreal-insights-ai-export

A tool that exports Unreal Engine `.utrace` performance captures to plain CSV files, with no GUI needed. The CSVs are easy to diff, search, and hand to an AI agent (like Claude Code) for analysis.

Built while debugging real perf issues on a UE 5.7 game's HUD. See `CASE_STUDY.md` for a real before/after example.

## Why use this

The Unreal Insights GUI is good for looking at one trace by eye. It is bad at comparing two runs, scripting "what got slower," or letting an AI agent drive it. This tool exports the same data as plain CSV, so all of that becomes easy.

**Works with or without an AI agent.** It was built for AI-agent workflows (tested with Claude Code). The CSV format and the guide are simple enough for an LLM to read directly. You can also just read the CSVs yourself with `awk` or Excel.

## What you need

- Unreal Engine installed (any 5.x version with Insights). `UnrealInsights.exe` ships inside `Engine/Binaries/Win64/`.
- Git Bash or WSL on Windows. The scripts are bash and need `awk`, `taskkill`, `cygpath` on PATH. Git Bash has these by default.
- A `.utrace` file. UE saves these to `%LOCALAPPDATA%\UnrealEngine\Common\UnrealTrace\Store\001\` whenever tracing is on (`-trace=default`, or the in-editor Trace menu).

No project dependency. Point it at any `.utrace` file from any UE project.

## Quick start (easiest way)

```bash
git clone <this-repo>
cd unreal-insights-ai-export
chmod +x *.sh

./menu.sh
```

First run walks you through a one-time setup: it finds `UnrealInsights.exe` and confirms your trace folder. Then it lists your `.utrace` files, newest first. Pick one, then pick an action: export, export with counters, or drill into a hitch. No flags to type.

## Quick start (manual / scripted)

```bash
export UE_INSIGHTS_EXE="/c/Program Files/Epic Games/UE_5.7/Engine/Binaries/Win64/UnrealInsights.exe"
./export.sh --trace "/c/Users/<you>/AppData/Local/UnrealEngine/Common/UnrealTrace/Store/001/mytrace.utrace" --out ./out/run1
```

This writes `./out/run1/timerstats.csv` and `./out/run1/threads.csv`. Read `ANALYSIS_GUIDE.md` to understand the columns, or give both files to an AI agent along with that guide.

## Drilling into one hitch

```bash
./frame-drill.sh map --trace mytrace.utrace --out ./out/hitch
# open out/hitch/prepass.csv, pick a frame or time window that looks slow
./frame-drill.sh window --trace mytrace.utrace --out ./out/hitch --start 329.78 --end 330.05
# out/hitch/window_events.txt lists what happened in that window, slowest first
```

## Files in this repo

| File | What it does |
|---|---|
| `menu.sh` | Easiest way to run this. Pick a trace, pick an action. No flags. |
| `setup.sh` | One-time setup. Finds `UnrealInsights.exe` and your trace folder, saves them. |
| `export.sh` | Runs the export. Kills stuck `UnrealInsights.exe` processes, retries if the export fails, checks the CSV is complete. |
| `frame-drill.sh` | Finds exactly what happened in one slow frame. |
| `ANALYSIS_GUIDE.md` | Explains the CSV columns and how to read them, plus how to use this with an AI agent. |
| `CASE_STUDY.md` | A real example: a UI perf bug found and fixed using this tool. |

## Limits (being honest)

- The CSV column layout is reverse engineered, not officially documented by Epic. It could change in a future engine version.
- By default you get totals per scope, not a full timeline. Use `frame-drill.sh` for one specific slow frame; it is slow on big traces.
- It tells you what is slow, not why. You still need to read the code.

More detail in `ANALYSIS_GUIDE.md`.

## License

MIT
