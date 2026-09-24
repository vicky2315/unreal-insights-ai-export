#!/usr/bin/env bash
# Headless Unreal Insights -> CSV exporter.
# Requires: Git Bash / WSL on Windows, UnrealInsights.exe installed with the engine.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: ./export.sh --trace <path-to.utrace> --out <output-dir> [options]

Required:
  --trace <path>        Path to the .utrace file to analyze
  --out <dir>            Output directory for CSVs, logs, and the response file

Options:
  --insights-exe <path>  Path to UnrealInsights.exe (default: $UE_INSIGHTS_EXE env var,
                          else auto-detected under C:/Program Files/Epic Games/UE_*/)
  --export <list>        Comma-separated export set: threads,timerstats,counters (default: threads,timerstats)
  --retries <n>           Retry attempts for the -AutoQuit export race (default: 5)
  --keep-log              Don't delete the UnrealInsights log after a successful run

Examples:
  ./export.sh --trace "C:/Users/me/.../Store/001/20260924_120000.utrace" --out ./out/run1
  UE_INSIGHTS_EXE="C:/Program Files/Epic Games/UE_5.7/Engine/Binaries/Win64/UnrealInsights.exe" \
    ./export.sh --trace mytrace.utrace --out ./out/run2 --export threads,timerstats,counters
EOF
}

TRACE=""
OUT=""
INSIGHTS_EXE="${UE_INSIGHTS_EXE:-}"
EXPORTS="threads,timerstats"
RETRIES=5
KEEP_LOG=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --trace) TRACE="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --insights-exe) INSIGHTS_EXE="$2"; shift 2 ;;
    --export) EXPORTS="$2"; shift 2 ;;
    --retries) RETRIES="$2"; shift 2 ;;
    --keep-log) KEEP_LOG=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown arg: $1" >&2; usage; exit 1 ;;
  esac
done

if [[ -z "$TRACE" || -z "$OUT" ]]; then
  echo "ERROR: --trace and --out are required" >&2
  usage
  exit 1
fi

if [[ -z "$INSIGHTS_EXE" ]]; then
  # Best-effort auto-detect: newest UE_5.* install with UnrealInsights.exe present.
  CANDIDATE=$(find "/c/Program Files/Epic Games" -maxdepth 1 -iname 'UE_5*' 2>/dev/null \
    | sort -V | tail -n1)
  if [[ -n "$CANDIDATE" ]]; then
    INSIGHTS_EXE="$CANDIDATE/Engine/Binaries/Win64/UnrealInsights.exe"
  fi
fi

if [[ -z "$INSIGHTS_EXE" || ! -f "$INSIGHTS_EXE" ]]; then
  echo "ERROR: UnrealInsights.exe not found. Pass --insights-exe <path> or set UE_INSIGHTS_EXE." >&2
  exit 1
fi

mkdir -p "$OUT"
OUT_WIN=$(cd "$OUT" && pwd -W 2>/dev/null || echo "$OUT")

# Stray-process guard: overlapping UnrealInsights.exe instances sharing an -ABSLOG
# corrupt each other's logging and the export silently fails to fire.
echo "[1/4] Killing any stray UnrealInsights.exe processes..."
taskkill //F //IM UnrealInsights.exe >/dev/null 2>&1 || true
sleep 1

echo "[2/4] Writing response file..."
RSP="$OUT/cmds.rsp"
: > "$RSP"
IFS=',' read -ra EXPORT_ARR <<< "$EXPORTS"
for e in "${EXPORT_ARR[@]}"; do
  case "$e" in
    threads) echo "TimingInsights.ExportThreads $OUT_WIN/threads.csv" >> "$RSP" ;;
    timerstats) echo "TimingInsights.ExportTimerStatistics $OUT_WIN/timerstats.csv" >> "$RSP" ;;
    counters) echo "TimingInsights.ExportCounters $OUT_WIN/counters.csv" >> "$RSP" ;;
    *) echo "WARNING: unknown export '$e', skipping" >&2 ;;
  esac
done

TRACE_WIN=$(cygpath -w "$TRACE" 2>/dev/null || echo "$TRACE")
RSP_WIN=$(cygpath -w "$RSP" 2>/dev/null || echo "$RSP")
LOG_WIN="$OUT_WIN/insights.log"

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

echo "[3/4] Running UnrealInsights.exe (headless export, up to $RETRIES attempts)..."
SUCCESS=0
for attempt in $(seq 1 "$RETRIES"); do
  echo "  attempt $attempt/$RETRIES..."
  taskkill //F //IM UnrealInsights.exe >/dev/null 2>&1 || true
  sleep 1
  "$INSIGHTS_EXE" \
    "-OpenTraceFile=$TRACE_WIN" \
    "-ABSLOG=$LOG_WIN" \
    -NoUI \
    -AutoQuit \
    "-ExecOnAnalysisCompleteCmd=@=$RSP_WIN" \
    -log \
    >/dev/null 2>&1 || true

  # Give the process a moment to finish writing, then verify at least one
  # requested CSV exists and its size is stable (not still being written).
  sleep 2
  ALL_OK=1
  for e in "${EXPORT_ARR[@]}"; do
    f="$OUT/${e/timerstats/timerstats}.csv"
    case "$e" in
      threads) f="$OUT/threads.csv" ;;
      timerstats) f="$OUT/timerstats.csv" ;;
      counters) f="$OUT/counters.csv" ;;
    esac
    if [[ ! -f "$f" ]]; then ALL_OK=0; break; fi
    s1=$(stat -c%s "$f" 2>/dev/null || echo 0)
    sleep 1
    s2=$(stat -c%s "$f" 2>/dev/null || echo 0)
    if [[ "$s1" != "$s2" || "$s1" == "0" ]]; then ALL_OK=0; break; fi
  done

  if [[ "$ALL_OK" == "1" ]]; then
    SUCCESS=1
    break
  fi
  echo "  export not confirmed yet, retrying..."
done

taskkill //F //IM UnrealInsights.exe >/dev/null 2>&1 || true

if [[ "$SUCCESS" != "1" ]]; then
  echo "ERROR: export did not produce stable CSVs after $RETRIES attempts. Check $LOG_WIN" >&2
  exit 1
fi

[[ "$KEEP_LOG" == "1" ]] || rm -f "$OUT/insights.log"

echo "[4/4] Done. CSVs written to: $OUT"
ls -la "$OUT"
echo ""
echo "Next: see ANALYSIS_GUIDE.md for the CSV column map and how to hand this to an AI agent for triage."
