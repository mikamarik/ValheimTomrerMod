#!/usr/bin/env bash
# Runs a scripted test session in Valheim (src/Dev/AutoTest.cs, debug builds only) and prints
# the results. It uses its own character, world and save folder, never the player's saves.
#   ./scripts/autotest.sh              scenario "blueprints": build every kit and check it
#   ./scripts/autotest.sh dump         write every hammer piece's size to .devtest/pieces.txt
#   ./scripts/autotest.sh probe        measure layers, UI, input and pieces to .devtest/probe.txt
# Output (log, screenshots, test saves) goes to .devtest/ in the repo.
set -euo pipefail

SCENARIO="${1:-blueprints}"
TIMEOUT="${AUTOTEST_TIMEOUT:-600}"
VALHEIM="${VALHEIM_INSTALL:-$HOME/Library/Application Support/Steam/steamapps/common/Valheim}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$REPO/.devtest"
LOG="$VALHEIM/BepInEx/LogOutput.log"
GAME_PROCESS="valheim.app/Contents/MacOS/Valheim"

if pgrep -f "$GAME_PROCESS" >/dev/null; then
  echo "!! Valheim is already running. Close it first."
  exit 1
fi

echo "==> building"
dotnet build "$REPO/ValheimTomrer.csproj" -c Debug -v minimal -nologo

# Keep the test saves: generating the test world is slow the first time.
mkdir -p "$OUT"
rm -f "$OUT"/*.png "$OUT/result.txt" "$OUT/LogOutput.log"

echo "==> launching Valheim (scenario: $SCENARIO, timeout ${TIMEOUT}s)"
cd "$VALHEIM"
rm -f "$LOG"
VT_AUTOTEST="$SCENARIO" VT_OUTDIR="$OUT" ./run_bepinex.sh >"$OUT/run.log" 2>&1 &
trap 'pkill -f "$GAME_PROCESS" 2>/dev/null || true' EXIT

for ((i = 0; i < TIMEOUT; i++)); do
  [[ -f "$OUT/result.txt" ]] && break
  if ((i > 30)) && ! pgrep -f "$GAME_PROCESS" >/dev/null; then
    echo "!! the game closed before the test finished"
    break
  fi
  sleep 1
done

# Give the game a moment to quit and flush the log.
sleep 3
cp "$LOG" "$OUT/LogOutput.log" 2>/dev/null || true
grep -E "AUTOTEST|Exception" "$OUT/LogOutput.log" 2>/dev/null || echo "!! no BepInEx log (see CLAUDE.md, Apple Silicon)"

if [[ ! -f "$OUT/result.txt" ]]; then
  echo "!! no result: timeout or crash. Full log: $OUT/LogOutput.log"
  exit 1
fi

cat "$OUT/result.txt"
grep -q "fail=0" "$OUT/result.txt"
