#!/usr/bin/env bash
# Runs a scripted test session in Valheim (src/Dev/AutoTest.cs, debug builds only) and prints
# the results. It uses its own character, world and save folder, never the player's saves.
#   ./scripts/autotest.sh              scenario "blueprints": build every kit and check it
#   ./scripts/autotest.sh dump         write every hammer piece's size to .devtest/pieces.txt
#   ./scripts/autotest.sh probe        measure layers, UI, input and pieces to .devtest/probe.txt
#   ./scripts/autotest.sh probe_build  measure chests, support, ground, glow and the build card to
#                                      .devtest/probe-build.txt (not part of editor_all)
#   ./scripts/autotest.sh build_sources  build a kit paid from the inventory and chests at 5, 15 and 30 m
#   ./scripts/autotest.sh build_partial  build what the materials pay for and what would stand, bottom to
#                                      top: half the wood, a two-storey house, nothing, everything, costs
#                                      off, and the planner's time on 400 pieces
#   ./scripts/autotest.sh build_sites  keep what a partial or empty click left as an unfinished build: its file,
#                                      its ghosts with the hammer out nearby, what is built read from the world
#   ./scripts/autotest.sh build_continue  continue an unfinished build: the key offers it first, the ghost stays put,
#                                      a click builds what the materials pay for, Remove and its window (keys, mouse, pad)
#   ./scripts/autotest.sh card_materials  the hammer card's materials list: a row per item and station, have / need,
#                                      colours, the footer, Continue, 12 items in two columns, a normal piece
#   ./scripts/autotest.sh hint_row     the controls of blueprint mode, Continue and a capture in the game's own hint
#                                      row: exactly each set, keyboard and pad, the game's look, the game's row back after
#   ./scripts/autotest.sh editor_open  open the editor window with its key, check the input takeover
#   ./scripts/autotest.sh editor_view  fill the 3D pane with a kit and drive the camera
#   ./scripts/autotest.sh editor_files round-trip every blueprint and run the file commands
#   ./scripts/autotest.sh editor_palette build the piece catalog and check the palette panel
#   ./scripts/autotest.sh editor_snap  run the placing and snapping engine against a table of rays
#   ./scripts/autotest.sh editor_edit  place, select, copy, turn, nudge and undo, then draw it
#   ./scripts/autotest.sh editor_panels check the build card, the selection fields and the problem list
#   ./scripts/autotest.sh editor_keys  drive every key, the wheel and the mouse, the top bar and the dialogs
#   ./scripts/autotest.sh editor_pad   drive every controller button through a made-up pad, and the piece menu
#   ./scripts/autotest.sh editor_focus walk the top bar and both panels with the pad, and press what it finds
#   ./scripts/autotest.sh editor_keep  close the editor on a changed blueprint, open it again, find it all kept
#   ./scripts/autotest.sh editor_build build a blueprint made in the editor, in the world, then edit it again
#   ./scripts/autotest.sh editor_capture build a kit turned 45 degrees, capture it with the turned rectangle, compare it to the file
#   ./scripts/autotest.sh editor_support build test structures, hold the editor's support rule against the game's
#   ./scripts/autotest.sh editor_all   every scenario above but probe_build in one game (about 10 minutes),
#                                      then the "no game art" guard, sites folder included
#   ./scripts/autotest.sh readme_gifs  no test: records the README's GIF frames (editor, build, capture) into
#                                      .devtest/gifs/<name>/. Not part of editor_all. scripts/make-gifs.sh
#                                      runs it and turns the frames into docs/media/*.gif
# Output (log, screenshots, test saves) goes to .devtest/ in the repo.
# VT_CHAIN="editor_build,blueprints" ./scripts/autotest.sh editor_all  runs only those, in that order.
# VT_SUPPORT_EDITOR_ONLY=1 ./scripts/autotest.sh editor_support  skips building in the world (3 min less).
set -euo pipefail

SCENARIO="${1:-blueprints}"
# editor_all chains every scenario, so it needs far longer than a single one.
if [[ "$SCENARIO" == "editor_all" ]]; then
  DEFAULT_TIMEOUT=1800
else
  DEFAULT_TIMEOUT=600
fi
TIMEOUT="${AUTOTEST_TIMEOUT:-$DEFAULT_TIMEOUT}"
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

# The mod writes blueprints, nothing else. The in-game guard walks the folders the mod writes
# to; this walks the repo, where only the autotest's own screenshots may be images, plus the
# Thunderstore package's icon (thunderstore/icon.png, that one path only).
if [[ "$SCENARIO" == "editor_all" ]]; then
  ART=$(find "$REPO" -path "$REPO/.git" -prune -o -path "$REPO/.devtest" -prune -o \
    -path "$REPO/thunderstore/icon.png" -prune -o \
    \( -name '*.png' -o -name '*.jpg' -o -name '*.jpeg' -o -name '*.tga' -o -name '*.glb' \
    -o -name '*.fbx' -o -name '*.obj' -o -name '*.mat' -o -name '*.mesh' -o -name '*.bundle' \
    -o -name '*.asset' -o -name '*.prefab' \) -print)
  if [[ -n "$ART" ]]; then
    echo "!! game art in the repo:"
    echo "$ART"
    exit 1
  fi
  echo "art guard: no image, mesh, material or bundle file in the repo"
fi

grep -q "fail=0" "$OUT/result.txt"
