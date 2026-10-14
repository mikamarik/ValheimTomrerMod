#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fail() { echo "ERROR: $*" >&2; exit 1; }
command -v python3 >/dev/null || fail "python3 is required."
REPO="$(bash "$SCRIPT_DIR/build.sh" --print-source)"
cd "$REPO"

# Packages always use Release; build.sh alone defaults to Debug for development.
CONFIGURATION=Release bash "$SCRIPT_DIR/build.sh" "${1:-}"
VERSION="$(tr -d '[:space:]' < "$REPO/Package/.build-version")"
PKG="$REPO/Package"
STAGE="$REPO/dist/ValheimTomrer-$VERSION"
ZIP="$REPO/dist/ValheimTomrer-$VERSION.zip"
for file in "$REPO/thunderstore/manifest.json" "$REPO/thunderstore/icon.png" "$REPO/README.md" "$REPO/LICENSE"; do
    [[ -f "$file" ]] || fail "Missing $file"
done

echo "== Staging ValheimTomrer v$VERSION =="
mkdir -p "$PKG"
cp -f "$REPO/README.md" "$REPO/LICENSE" "$PKG/"
cp -f "$REPO/thunderstore/icon.png" "$PKG/icon.png"
if [[ -f "$REPO/CHANGELOG.md" ]]; then cp -f "$REPO/CHANGELOG.md" "$PKG/CHANGELOG.md"; fi
python3 - "$REPO/thunderstore/manifest.json" "$PKG/manifest.json" "$VERSION" <<'PY'
import json, pathlib, sys
source, output, version = sys.argv[1:]
manifest = json.loads(pathlib.Path(source).read_text(encoding="utf-8-sig"))
manifest["version_number"] = version
pathlib.Path(output).write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")
PY

# Recreate only this version's staging folder and archive.
rm -rf "$STAGE"
mkdir -p "$STAGE/plugins"
cp -f "$PKG/manifest.json" "$PKG/README.md" "$PKG/LICENSE" "$PKG/icon.png" "$STAGE/"
cp -f "$PKG/plugins/ValheimTomrer.dll" "$STAGE/plugins/"
if [[ -f "$PKG/CHANGELOG.md" ]]; then cp -f "$PKG/CHANGELOG.md" "$STAGE/"; fi
echo "== Creating ZIP =="
python3 - "$STAGE" "$ZIP" <<'PY'
import pathlib, sys, zipfile
stage, output = map(pathlib.Path, sys.argv[1:])
with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED) as archive:
    for file in sorted(stage.rglob("*")):
        if file.is_file():
            archive.write(file, file.relative_to(stage))
with zipfile.ZipFile(output) as archive:
    bad = archive.testzip()
    if bad:
        sys.exit("ERROR: Archive integrity failed: " + bad)
PY
echo "== DONE =="
echo "Package: $ZIP"
