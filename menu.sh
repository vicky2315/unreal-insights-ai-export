#!/usr/bin/env bash
# Interactive front end: pick a trace, pick what to export, no flags to remember.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG_FILE="$DIR/.insights-config"

if [[ ! -f "$CONFIG_FILE" ]]; then
  echo "No config found -- running first-time setup."
  "$DIR/setup.sh"
fi
# shellcheck disable=SC1090
source "$CONFIG_FILE"
export UE_INSIGHTS_EXE

if [[ ! -d "$TRACE_STORE_DIR" ]]; then
  echo "ERROR: trace store dir '$TRACE_STORE_DIR' not found. Re-run ./setup.sh to fix it." >&2
  exit 1
fi

mapfile -t TRACES < <(ls -t "$TRACE_STORE_DIR"/*.utrace 2>/dev/null)
if [[ ${#TRACES[@]} -eq 0 ]]; then
  echo "No .utrace files found in $TRACE_STORE_DIR" >&2
  exit 1
fi

echo "== Traces (newest first) =="
for i in "${!TRACES[@]}"; do
  printf "  %d) %s\n" "$((i+1))" "$(basename "${TRACES[$i]}")"
done
echo ""
read -rp "Pick a trace [1-${#TRACES[@]}]: " tidx
TRACE="${TRACES[$((tidx-1))]}"
if [[ -z "$TRACE" ]]; then
  echo "Invalid selection." >&2
  exit 1
fi

BASENAME=$(basename "$TRACE" .utrace)
DEFAULT_OUT="$DIR/out/$BASENAME"

echo ""
echo "== What do you want to do? =="
echo "  1) Export summary CSVs (timerstats + threads) -- start here"
echo "  2) Export summary CSVs + counters"
echo "  3) Drill a specific hitch/frame window"
read -rp "Choice [1]: " choice
choice="${choice:-1}"

case "$choice" in
  1)
    "$DIR/export.sh" --trace "$TRACE" --out "$DEFAULT_OUT" --export threads,timerstats
    ;;
  2)
    "$DIR/export.sh" --trace "$TRACE" --out "$DEFAULT_OUT" --export threads,timerstats,counters
    ;;
  3)
    HITCH_OUT="$DEFAULT_OUT-hitch"
    "$DIR/frame-drill.sh" map --trace "$TRACE" --out "$HITCH_OUT"
    echo ""
    echo "Inspect $HITCH_OUT/prepass.csv, then enter a time window to drill."
    read -rp "Start time (sec): " st
    read -rp "End time (sec): " en
    "$DIR/frame-drill.sh" window --trace "$TRACE" --out "$HITCH_OUT" --start "$st" --end "$en"
    ;;
  *)
    echo "Invalid choice." >&2
    exit 1
    ;;
esac

echo ""
echo "Done. See ANALYSIS_GUIDE.md for how to read the output, or hand the CSVs to an AI agent."
