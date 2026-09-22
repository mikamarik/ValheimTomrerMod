#!/usr/bin/env bash
# Builds the Release DLL and packs the Thunderstore zip: thunderstore/build/ValheimTomrer.zip.
# Every run makes the zip from scratch. This build does not copy the DLL into the game.
#   npm run zip   (or: ./scripts/zip.sh)
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$REPO/thunderstore/build"
STAGE="$OUT/package"
ZIP="$OUT/ValheimTomrer.zip"
DLL="$REPO/bin/Release/ValheimTomrer.dll"
MANIFEST="$REPO/thunderstore/manifest.json"
ICON="$REPO/thunderstore/icon.png"

fail() { echo "!! $*"; exit 1; }

echo "==> building Release"
dotnet build "$REPO/ValheimTomrer.csproj" -c Release -v minimal -nologo -p:DeployToGame=false

echo "==> checking"
for f in "$MANIFEST" "$ICON" "$REPO/README.md" "$REPO/CHANGELOG.md" "$REPO/LICENSE" "$DLL"; do
  [[ -f "$f" ]] || fail "missing $f"
done

# Thunderstore's rules (wiki.thunderstore.io/mods/creating-a-package) and one version everywhere.
MANIFEST_VERSION=$(node -e '
  const m = require(process.argv[1]);
  for (const k of ["name", "version_number", "website_url", "description", "dependencies"])
    if (!(k in m)) { console.error("manifest.json has no " + k); process.exit(1); }
  if (!/^[A-Za-z0-9_]{1,128}$/.test(m.name)) { console.error("bad name: " + m.name); process.exit(1); }
  if (!/^\d+\.\d+\.\d+$/.test(m.version_number)) { console.error("bad version: " + m.version_number); process.exit(1); }
  if (m.description.length > 250) { console.error("description is " + m.description.length + " characters, 250 at most"); process.exit(1); }
  console.log(m.version_number);
' "$MANIFEST") || fail "manifest.json"
CSPROJ_VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$REPO/ValheimTomrer.csproj")
PLUGIN_VERSION=$(sed -n 's/.*PluginVersion = "\(.*\)".*/\1/p' "$REPO/src/Plugin.cs")
[[ "$MANIFEST_VERSION" == "$CSPROJ_VERSION" && "$MANIFEST_VERSION" == "$PLUGIN_VERSION" ]] \
  || fail "versions differ: manifest $MANIFEST_VERSION, csproj $CSPROJ_VERSION, Plugin.cs $PLUGIN_VERSION"

SIZE=$(sips -g pixelWidth -g pixelHeight "$ICON" | awk '/pixel/ { printf "%s ", $2 }')
[[ "$SIZE" == "256 256 " ]] || fail "icon.png is ${SIZE% }, it must be 256 256"

# A Release build leaves the autotest out (src/Dev is Debug only).
if grep -q "AutoTestPeace" "$DLL"; then fail "the Release DLL still has the autotest in it"; fi

echo "==> packing $MANIFEST_VERSION"
rm -rf "$STAGE" "$ZIP"
mkdir -p "$STAGE"
cp "$MANIFEST" "$ICON" "$REPO/README.md" "$REPO/CHANGELOG.md" "$REPO/LICENSE" "$DLL" "$STAGE/"
# The files go at the zip's root, not in a folder. -X leaves out macOS extra attributes.
(cd "$STAGE" && zip -q -X "$ZIP" ./*)
# The staged copy of icon.png would trip the art guard's walk of the repo (scripts/autotest.sh).
rm -rf "$STAGE"
unzip -l "$ZIP"
echo "==> $ZIP"
