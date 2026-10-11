#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fail() { echo "ERROR: $*" >&2; exit 1; }

# Works beside the csproj, or at the top of the previously supplied source bundle.
REPO=""
for candidate in "${TOMRER_SOURCE:-$SCRIPT_DIR}" "$SCRIPT_DIR/ValheimTomrerMod" "$SCRIPT_DIR/source/ValheimTomrerMod"; do
    if [[ -f "$candidate/ValheimTomrer.csproj" ]]; then
        REPO="$(cd "$candidate" && pwd)"
        break
    fi
done
[[ -n "$REPO" ]] || fail "Put these scripts beside ValheimTomrer.csproj, or set TOMRER_SOURCE to that folder."
if [[ "${1:-}" == "--print-source" ]]; then
    printf '%s\n' "$REPO"
    exit 0
fi
cd "$REPO"
command -v python3 >/dev/null || fail "python3 is required."
command -v dotnet >/dev/null || fail "A .NET SDK is required; dotnet was not found."

DEFAULT_VERSION="$(python3 - "$REPO/ValheimTomrer.csproj" <<'PY'
import sys, xml.etree.ElementTree as ET
print(ET.parse(sys.argv[1]).getroot().findtext("./PropertyGroup/Version"))
PY
)"
if [[ -f "$SCRIPT_DIR/VERSION" ]]; then
    DEFAULT_VERSION="$(tr -d '[:space:]' < "$SCRIPT_DIR/VERSION")"
fi
VERSION="${1:-$DEFAULT_VERSION}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "VERSION must be Major.Minor.Patch."
CONFIGURATION="${CONFIGURATION:-Debug}"
[[ "$CONFIGURATION" == Debug || "$CONFIGURATION" == Release ]] || fail "CONFIGURATION must be Debug or Release."
python3 - "$REPO" "$VERSION" <<'PY'
import pathlib, re, sys, xml.etree.ElementTree as ET
root, wanted = pathlib.Path(sys.argv[1]), sys.argv[2]
project = ET.parse(root / "ValheimTomrer.csproj").getroot().findtext("./PropertyGroup/Version")
match = re.search(r'PluginVersion\s*=\s*"([^"]+)"', (root / "src/Plugin.cs").read_text())
plugin = match.group(1) if match else None
if project != wanted or plugin != wanted:
    sys.exit(f"ERROR: requested version {wanted}, csproj {project}, plugin {plugin}. "
             "Update VERSION, csproj Version and PluginVersion together before changing the version.")
PY

STEAM_ROOTS=(
    "$HOME/.local/share/Steam"
    "$HOME/.steam/steam"
    "$HOME/.steam/root"
    "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
)
GAME=""
MANAGED=""
check_game() {
    local game="$1"
    if [[ -f "$game/valheim_Data/Managed/assembly_valheim.dll" ]]; then
        GAME="$game"; MANAGED="$game/valheim_Data/Managed"; return 0
    fi
    if [[ -f "$game/valheim.app/Contents/Resources/Data/Managed/assembly_valheim.dll" ]]; then
        GAME="$game"; MANAGED="$game/valheim.app/Contents/Resources/Data/Managed"; return 0
    fi
    return 1
}
if [[ -n "${VALHEIM_MANAGED:-}" ]]; then
    [[ -f "$VALHEIM_MANAGED/assembly_valheim.dll" ]] || fail "VALHEIM_MANAGED does not contain assembly_valheim.dll."
    MANAGED="$VALHEIM_MANAGED"
    GAME="${VALHEIM_INSTALL:-$(cd "$MANAGED/../.." && pwd)}"
elif [[ -n "${VALHEIM_INSTALL:-}" ]]; then
    check_game "$VALHEIM_INSTALL" || fail "VALHEIM_INSTALL has no Valheim managed assemblies."
else
    for steam in "${STEAM_ROOTS[@]}" "$HOME/Library/Application Support/Steam"; do
        if check_game "$steam/steamapps/common/Valheim"; then break; fi
        [[ -f "$steam/steamapps/libraryfolders.vdf" ]] || continue
        while IFS= read -r library; do
            if check_game "$library/steamapps/common/Valheim"; then break; fi
        done < <(python3 - "$steam/steamapps/libraryfolders.vdf" <<'PY'
import pathlib, re, sys
text = pathlib.Path(sys.argv[1]).read_text(errors="replace")
for path in re.findall(r'"path"\s*"([^"]+)"', text):
    print(path.replace("\\\\", "\\"))
PY
        )
        [[ -z "$MANAGED" ]] || break
    done
fi
[[ -n "$MANAGED" ]] || fail "Could not find Valheim. Set VALHEIM_INSTALL=/path/to/Valheim (or VALHEIM_MANAGED=/path/to/Managed)."

valid_bepinex() { [[ -f "$1/core/BepInEx.dll" && -f "$1/core/0Harmony.dll" ]]; }
if [[ -n "${BEPINEX_PATH:-}" ]]; then
    valid_bepinex "$BEPINEX_PATH" || fail "BEPINEX_PATH needs core/BepInEx.dll and core/0Harmony.dll."
else
    BEPINEX_PATH=""
    candidates=(
        "$GAME/BepInEx"
        "$HOME/.local/share/Steam/steamapps/common/Valheim/BepInEx"
        "$HOME/.steam/steam/steamapps/common/Valheim/BepInEx"
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam/steamapps/common/Valheim/BepInEx"
        "$HOME/.var/app/com.kesomannen.gale/data/com.kesomannen.gale/valheim/profiles/Default/BepInEx"
    )
    for candidate in "${candidates[@]}"; do
        if valid_bepinex "$candidate"; then BEPINEX_PATH="$candidate"; break; fi
    done
    profile_roots=(
        "$HOME/.var/app/com.kesomannen.gale/data/com.kesomannen.gale/valheim/profiles"
        "$HOME/.local/share/com.kesomannen.gale/valheim/profiles"
        "$HOME/.config/r2modmanPlus-local/Valheim/profiles"
        "$HOME/.config/Thunderstore Mod Manager/DataFolder/Valheim/profiles"
    )
    for profiles in "${profile_roots[@]}"; do
        [[ -z "$BEPINEX_PATH" ]] || break
        [[ -d "$profiles" ]] || continue
        while IFS= read -r -d '' dll; do
            candidate="${dll%/core/BepInEx.dll}"
            if valid_bepinex "$candidate"; then BEPINEX_PATH="$candidate"; break; fi
        done < <(find "$profiles" -type f -path '*/BepInEx/core/BepInEx.dll' -print0)
    done
fi
[[ -n "$BEPINEX_PATH" ]] || fail "Could not locate BepInEx. Set BEPINEX_PATH=/path/to/your/profile/BepInEx."

echo "== Building ValheimTomrer v$VERSION ($CONFIGURATION) =="
echo "   Game assemblies: $MANAGED"
echo "   BepInEx compile path: $BEPINEX_PATH"
dotnet build "$REPO/ValheimTomrer.csproj" \
    "-p:Configuration=$CONFIGURATION" "-p:Version=$VERSION" \
    "-p:ValheimInstall=$GAME" "-p:ValheimManaged=$MANAGED" \
    "-p:BepInExDir=$BEPINEX_PATH" -p:DeployToGame=false -v:m

DLL="$REPO/bin/$CONFIGURATION/ValheimTomrer.dll"
[[ -f "$DLL" ]] || fail "Build succeeded but $DLL was not produced."
echo "== Refreshing Package/plugins staging =="
mkdir -p "$REPO/Package/plugins"
cp -f "$DLL" "$REPO/Package/plugins/ValheimTomrer.dll"
printf '%s\n' "$VERSION" > "$REPO/Package/.build-version"
printf '%s\n' "$CONFIGURATION" > "$REPO/Package/.build-configuration"
echo "== DONE: ValheimTomrer v$VERSION =="
echo "DLL: $REPO/Package/plugins/ValheimTomrer.dll"
