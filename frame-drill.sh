#!/usr/bin/env bash
# Drill a single hitch/frame window from a .utrace using per-event export.
# Two-step workflow (see ANALYSIS_GUIDE.md section "Drilling a hitch"):
#   1. map    - export Slate::Prepass events (one per frame) to find a frame's time window
#   2. window - dump GameThread events and filter to a [start,end] time window, ranked by duration
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  ./frame-drill.sh map --trace <path> --out <dir> [--insights-exe <path>]
      Exports Slate::Prepass timing events (one row per frame; row N+1 = frame N).
      Use this to find the StartTime/EndTime of the frame you care about.

  ./frame-drill.sh window --trace <path> --out <dir> --start <sec> --end <sec> [--min-ms <n>] [--insights-exe <path>]
      Dumps the full GameThread event list (can be multi-GB on long traces),
      filters to [start,end], and prints events >= --min-ms sorted by duration.
      Deletes the large intermediate dump on exit unless --keep-dump is passed.

Notes:
  - Thread filter for ExportTimingEvents takes a NAME ("GameThread"), not a numeric id.
  - -startTime/-endTime are ignored by the export command itself; filtering happens
    here, in awk, after the export.
EOF
}

MODE="${1:-}"; shift || true
TRACE=""; OUT=""; START=""; END=""; MIN_MS="1.0"; INSIGHTS_EXE="${UE_INSIGHTS_EXE:-}"; KEEP_DUMP=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --trace) TRACE="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --start) START="$2"; shift 2 ;;
    --end) END="$2"; shift 2 ;;
    --min-ms) MIN_MS="$2"; shift 2 ;;
    --insights-exe) INSIGHTS_EXE="$2"; shift 2 ;;
    --keep-dump) KEEP_DUMP=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown arg: $1" >&2; usage; exit 1 ;;
  esac
done

if [[ "$MODE" != "map" && "$MODE" != "window" ]]; then
  usage; exit 1
fi
if [[ -z "$TRACE" || -z "$OUT" ]]; then
  echo "ERROR: --trace and --out are required" >&2; exit 1
fi
if [[ -z "$INSIGHTS_EXE" ]]; then
  CANDIDATE=$(find "/c/Program Files/Epic Games" -maxdepth 1 -iname 'UE_5*' 2>/dev/null | sort -V | tail -n1)
  [[ -n "$CANDIDATE" ]] && INSIGHTS_EXE="$CANDIDATE/Engine/Binaries/Win64/UnrealInsights.exe"
fi
if [[ -z "$INSIGHTS_EXE" || ! -f "$INSIGHTS_EXE" ]]; then
  echo "ERROR: UnrealInsights.exe not found. Pass --insights-exe or set UE_INSIGHTS_EXE." >&2; exit 1
fi

mkdir -p "$OUT"
OUT_WIN=$(cd "$OUT" && pwd -W 2>/dev/null || echo "$OUT")
TRACE_WIN=$(cygpath -w "$TRACE" 2>/dev/null || echo "$TRACE")
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

run_export() {
  local rsp="$1" log="$2"
  local rsp_win log_win
  rsp_win=$(cygpath -w "$rsp" 2>/dev/null || echo "$rsp")
  log_win=$(cygpath -w "$log" 2>/dev/null || echo "$log")
  taskkill //F //IM UnrealInsights.exe >/dev/null 2>&1 || true
  sleep 1
  "$INSIGHTS_EXE" \
    "-OpenTraceFile=$TRACE_WIN" \
    "-ABSLOG=$log_win" \
    -NoUI -AutoQuit \
    "-ExecOnAnalysisCompleteCmd=@=$rsp_win" \
    -log >/dev/null 2>&1 || true
  taskkill //F //IM UnrealInsights.exe >/dev/null 2>&1 || true
}

if [[ "$MODE" == "map" ]]; then
  RSP="$OUT/prepass.rsp"
  cat > "$RSP" <<EOF
TimingInsights.ExportTimingEvents $OUT_WIN/prepass.csv -timers=Slate::Prepass -columns=ThreadId,TimerName,StartTime,EndTime,Duration
EOF
  echo "Exporting Slate::Prepass events..."
  run_export "$RSP" "$OUT/prepass.log"
  if [[ ! -f "$OUT/prepass.csv" ]]; then
    echo "ERROR: export failed, check $OUT/prepass.log" >&2; exit 1
  fi
  echo "Done: $OUT/prepass.csv"
  echo "Row N+1 (row 1 is the header) = frame N. Read its StartTime/EndTime as your window."
  echo "Watch gaps between one row's EndTime and the next row's StartTime -- a large gap"
  echo "is a non-Slate game-thread stall, often where the hitch actually is."
  exit 0
fi

# MODE == window
if [[ -z "$START" || -z "$END" ]]; then
  echo "ERROR: --start and --end are required for 'window' mode" >&2; exit 1
fi

RSP="$OUT/gt.rsp"
cat > "$RSP" <<EOF
TimingInsights.ExportTimingEvents $OUT_WIN/gt_dump.csv -threads=GameThread -columns=ThreadId,TimerName,StartTime,EndTime,Duration
EOF
echo "Exporting full GameThread event dump (this can be large and slow on big traces)..."
run_export "$RSP" "$OUT/gt.log"
if [[ ! -f "$OUT/gt_dump.csv" ]]; then
  echo "ERROR: export failed, check $OUT/gt.log" >&2; exit 1
fi

echo ""
echo "Events in [$START, $END] with duration >= ${MIN_MS}ms, longest first:"
awk -F',' -v s="$START" -v e="$END" -v m="$MIN_MS" \
  'NR>1 && $3>=s && $3<=e && $5*1000>=m {printf "%.2fms  @%.4f  %s\n", $5*1000, $3, $2}' \
  "$OUT/gt_dump.csv" | sort -rn > "$OUT/window_events.txt"
cat "$OUT/window_events.txt"
echo ""
echo "Full ranked list saved to: $OUT/window_events.txt"

if [[ "$KEEP_DUMP" != "1" ]]; then
  echo "Deleting large intermediate dump ($OUT/gt_dump.csv). Pass --keep-dump to retain it."
  rm -f "$OUT/gt_dump.csv"
fi
