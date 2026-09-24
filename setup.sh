#!/usr/bin/env bash
# One-time interactive setup. Writes .insights-config so you never have to
# pass --insights-exe / trace-store-dir by hand again.
set -euo pipefail

CONFIG_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/.insights-config"

echo "== unreal-insights-ai-export setup =="
echo ""

# --- UnrealInsights.exe ---
DEFAULT_EXE=""
CANDIDATE=$(find "/c/Program Files/Epic Games" -maxdepth 1 -iname 'UE_5*' 2>/dev/null | sort -V | tail -n1)
if [[ -n "$CANDIDATE" ]]; then
  DEFAULT_EXE="$CANDIDATE/Engine/Binaries/Win64/UnrealInsights.exe"
fi

if [[ -n "$DEFAULT_EXE" && -f "$DEFAULT_EXE" ]]; then
  echo "Found UnrealInsights.exe at:"
  echo "  $DEFAULT_EXE"
  read -rp "Use this? [Y/n] " ans
  if [[ "$ans" =~ ^[Nn] ]]; then
    read -rp "Enter path to UnrealInsights.exe: " INSIGHTS_EXE
  else
    INSIGHTS_EXE="$DEFAULT_EXE"
  fi
else
  echo "Could not auto-detect UnrealInsights.exe."
  read -rp "Enter full path to UnrealInsights.exe: " INSIGHTS_EXE
fi

if [[ ! -f "$INSIGHTS_EXE" ]]; then
  echo "WARNING: '$INSIGHTS_EXE' does not exist. Saving anyway -- fix it later by re-running setup.sh." >&2
fi

# --- trace store dir ---
DEFAULT_STORE="$HOME/AppData/Local/UnrealEngine/Common/UnrealTrace/Store/001"
echo ""
echo "Default trace store: $DEFAULT_STORE"
read -rp "Use this trace folder? [Y/n] " ans2
if [[ "$ans2" =~ ^[Nn] ]]; then
  read -rp "Enter path to your .utrace folder: " STORE_DIR
else
  STORE_DIR="$DEFAULT_STORE"
fi

cat > "$CONFIG_FILE" <<EOF
UE_INSIGHTS_EXE="$INSIGHTS_EXE"
TRACE_STORE_DIR="$STORE_DIR"
EOF

echo ""
echo "Saved config to $CONFIG_FILE"
echo "Run ./menu.sh to export or drill a trace interactively, or use export.sh / frame-drill.sh directly."
