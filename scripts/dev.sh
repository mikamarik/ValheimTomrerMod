#!/usr/bin/env bash
# Build ValheimTomrer, deploy it, launch Valheim, and stream our log lines.
#   ./scripts/dev.sh           normal run
#   ./scripts/dev.sh --debug   also open the Mono soft debugger on 127.0.0.1:10000
set -euo pipefail

VALHEIM="${VALHEIM_INSTALL:-$HOME/Library/Application Support/Steam/steamapps/common/Valheim}"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

EXTRA=()
if [[ "${1:-}" == "--debug" ]]; then
  # NOTE: this doorstop flag takes a value; passing it bare aborts run_bepinex.sh
  EXTRA+=(--doorstop-mono-debug-enabled true)
  echo "==> Mono debugger will listen on 127.0.0.1:10000"
fi

echo "==> building"
dotnet build "$REPO/ValheimTomrer.csproj" -v minimal

echo "==> launching Valheim"
cd "$VALHEIM"
rm -f BepInEx/LogOutput.log
./run_bepinex.sh "${EXTRA[@]}" >/tmp/valheimtomrer-run.log 2>&1 &
GAME_PID=$!

# run_bepinex.sh execs the game, so trap cleanup on Ctrl-C
trap 'pkill -f "valheim.app/Contents/MacOS/Valheim" 2>/dev/null || true' EXIT

echo "==> waiting for BepInEx"
for _ in $(seq 1 90); do
  [[ -f BepInEx/LogOutput.log ]] && break
  sleep 1
done

if [[ ! -f BepInEx/LogOutput.log ]]; then
  echo "!! BepInEx never wrote a log."
  echo "!! Check ARCHPREFERENCE in run_bepinex.sh is x86_64 (arm64 fails silently)."
  exit 1
fi

echo "==> streaming ValheimTomrer + errors (Ctrl-C to quit)"
tail -f BepInEx/LogOutput.log | grep --line-buffered -E "ValheimTomrer|Error|Exception|Harmony"
