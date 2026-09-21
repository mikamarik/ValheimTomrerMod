# ValheimTomrer, agent guide

Client-side BepInEx plugin for **Valheim 1.0** on **macOS / Apple Silicon**.
Everything below was verified against this machine's actual install (environment 2026-09-19,
file list 2026-09-21).

**Do not map the repo and do not hunt for a file.** Every file is listed with a one-line
description under [Project structure](#project-structure-every-file), plus a
[Where to change what](#where-to-change-what) table. Go straight to the file. Search only to
find a symbol inside a file this guide already pointed you at, or when the guide is wrong,
and then fix the guide.

---

## Scope, do not exceed without asking

The user set these constraints explicitly:

- **Single-player / client-side only.** No RPCs, no ZDO sync, no server-side logic.
- **No custom prefabs or pieces.** Patch existing game behaviour instead.
- **No Jötunn dependency.** It is only needed for registering custom content. Plain
  BepInEx + HarmonyX covers this project.
- Old worlds/characters are explicitly **out of scope**, no save-migration handling.
- **Every feature works on a controller. No exceptions.** The user plays on a pad. Anything a
  key, the mouse or the wheel can do, a pad button (or combo) must do too, in the same phase,
  with its own autotest check through the fake pad and its line in the README's controller
  tables. A plan must never mark pad support "out of scope" or "later". A feature without it
  is not done.

If a task seems to require crossing one of these lines, stop and ask.

---

## How to write for this user

Applies to replies, docs, plans and commit messages.

- **Short.** Lead with the answer. One or two sentences is often enough.
- **Simple words.** Everyday English. No jargon, no literary register, no metaphors.
- **Structure over prose.** Two or more facts, options, steps or files go in a list or table, never a paragraph.
- **No em-dashes.** Use a comma, a period, parentheses, or reword. They read as AI-written.
- **No figurative "buys".** Only for real purchases. Say "gives you", "gets you", or "the benefit is".
- **Cut** preambles, restating the task, layered qualifications, and explaining my own reasoning.
- Translate code jargon into plain words. Keep an identifier only when they need to find it.
- Resolve ambiguous targets yourself from the code. Do not ask the user to name a file or method. For open questions, offer 2-3 concrete options in one pass.

The user is a developer and a non-native English speaker who judges by the running result, not by reading diffs. Verify by actually building and launching, never report "should work".

---

## Environment (verified, not assumed)

| | |
|---|---|
| Game version | 1.0 (`CFBundleShortVersionString`) |
| Unity | `6000.0.75f1` |
| Runtime | **Mono** (`MonoBleedingEdge`), *not* IL2CPP |
| Game code | `assembly_valheim.dll`, 1311 types |
| BepInEx | 5.4.23.5 (denikson BepInExPack_Valheim 5.4.2350) |
| doorstop | 4.5.0, universal (x86_64 + arm64) |
| Plugin TFM | `netstandard2.1` |
| .NET SDK | 9.0.303 (no Mono, no msbuild, not needed) |
| Network version | `c_networkVersion = 40` |

**Paths** (note: macOS layout differs from Windows/Linux):

```
GAME     ~/Library/Application Support/Steam/steamapps/common/Valheim
MANAGED  $GAME/valheim.app/Contents/Resources/Data/Managed     # NOT valheim_Data/Managed
PLUGINS  $GAME/BepInEx/plugins/ValheimTomrer/
CONFIG   $GAME/BepInEx/config/com.mikamarik.valheimtomrer.cfg
LOG      $GAME/BepInEx/LogOutput.log
```

---

## CRITICAL: Apple Silicon fails silently

Valheim 1.0 ships a **universal binary**. On native arm64, BepInEx 5's MonoMod detours
die in `DetourHelper.GetIdentifiable()` inside `HarmonyInteropFix.Apply()`, and
**no plugins load and no `LogOutput.log` is written at all**. It looks exactly like a
normal vanilla launch. (BepInEx issue #1303; fix PR #1402 still unmerged.)

`run_bepinex.sh` ships with `ARCHPREFERENCE="arm64,x86_64"`, which *causes* this. It has
been patched locally to:

```sh
export ARCHPREFERENCE="x86_64,arm64"
```

An outer `arch -x86_64` wrapper does **not** help. The script overrides it internally.
A pristine copy is kept at `run_bepinex.sh.orig`.

**Any BepInEx reinstall or update reverts this patch.** After one, re-apply it and clear
Gatekeeper quarantine on the freshly downloaded injector:

```bash
xattr -d com.apple.quarantine libdoorstop.dylib
```

macOS keeps xattrs on a file you `cp` over, so a stale quarantine flag can survive a
replacement and silently block injection.

---

## Project structure, every file

Nothing here is a guess. Use this instead of searching. All 86 source files, and every other
file in the repo outside `bin/`, `obj/` and `.devtest/`.

**Build, scripts, data**

```
ValheimTomrer.csproj              netstandard2.1, local refs, publicizer, embeds the kits,
                                  auto-deploys the DLL into BepInEx/plugins after every build
Directory.Build.props             finds the install: ValheimInstall, ValheimManaged, BepInExDir
README.md                         the player-facing readme: features, install, keys
package.json                      npm run build (-c Debug) and build:release (-c Release)
.gitignore                        ignores bin, obj, .devtest and .claude/ (open files there by
                                  exact path, the search tools skip them)
scripts/dev.sh                    build, deploy, launch, tail our log lines (--debug adds the debugger)
scripts/autotest.sh               run one AutoTest scenario, output lands in .devtest/
blueprints/workshop.blueprint     the only shipped kit, embedded in the DLL as ValheimTomrer.Kits.*
tests/fixtures/                   workshop.canonical.blueprint, exactly what the writer must
                                  produce. Embedded in Debug only, the autotest reads it in game
thunderstore/manifest.json        packaging stub, not wired to anything yet
.vscode/tasks.json                tasks "build" and "run: game (debug)"
.vscode/launch.json               "Attach to Valheim", vstuc, 127.0.0.1:10000
```

**Docs in `.claude/`** (read these before touching the area they cover)

```
handoff/editor.md                 the editor's build record, phases 0 to 12. Read before any
                                  editor change: it says why things are the way they are
handoff/editor-refine.md          the second pass on top of that: readability, one camera, the
                                  panel walk, the hint bar. Newer than editor.md, it wins
handoff/build-sites.md            the log of the capture and partial-build plan, one block per
                                  phase: files, measured numbers, gotchas
plans/20-09-2026-in-game-editor-done.md      the editor plan that was run. Done
plans/20-09-2026-editor-refine-done.md       the second pass on it. Done, with two decisions
                                             overturned in use. Read its Done block first
plans/19-09-2026-blueprints-research-done.md what the blueprint formats are. Done
plans/21-09-2026-capture-and-partial-build-done.md  Homestead-style capture, building from
                                             chests, part builds, the materials list. Done (Phase 8,
                                             ItemDrawers, skipped). Read its Done block first
research/19-09-2026-custom-ui.md             UI: start here, which approach fits which need
research/19-09-2026-custom-ui-game-api.md    UI: exact game methods, patch targets, fonts, sprites
research/19-09-2026-custom-ui-platform-and-mods.md  UI: input, asset bundles on macOS, other mods
```

**`src/`, the mod**

```
Plugin.cs                         BepInEx entry. Binds ModEnabled and BlueprintKey, starts
                                  Harmony, and its Update drives EditorSession.Tick and SiteTracker.Tick
```

**`src/Patches/`**, one class per target, file named `<Type><Method>Patch.cs`

```
EditorInputBlockPatches.cs        the whole input takeover, nine patches in one file on purpose,
                                  all gated on ModUi.Blocking
EnvManAwakePatch.cs               keeps the world's sun out of the editor's scene
FejdStartupAwakePatch.cs          main-menu smoke test, prints harmony=OK | publicizer=OK
GameCameraAwakePatch.cs           clears layer 30 from the player camera's culling mask
HudSetupPieceInfoPatch.cs         the vanilla build card shows the blueprint and its materials list,
                                  not the piece. Anything else hides the list
KeyHintsUpdateHintsPatch.cs       the game's hint row along the bottom: while blueprint mode, Continue
                                  or a capture is up, HintRow shows its set and the game's update
                                  is skipped. Else the game runs as always
PlayerCanRotatePiecePatch.cs      stops the wheel zooming the camera in blueprint mode
PlayerInRepairModePatch.cs        same thing, the repair-mode branch of the camera zoom
PlayerSetSelectedPiecePatch.cs    picking any piece leaves blueprint mode
PlayerUpdateAvailablePiecesListPatch.cs  unlocks changed, flag the catalog to read them again
PlayerUpdatePlacementGhostPatch.cs       show the blueprint preview instead of one piece
PlayerUpdatePlacementPatch.cs     build-mode input: the blueprint key (or square on the pad), the
                                  click, the wheel
ZInputTryGetButtonStatePatch.cs   holds back from the game the pad buttons the world combos use
                                  (WorldPad): the D-pad and circle during a capture, square and
                                  triangle while L2 is held. Every GetButton* of the game comes here
```

**`src/Blueprints/`**, the file format and the hammer

```
Blueprint.cs                      the data: Blueprint + BlueprintPiece. Keep free of other game
                                  code, Tomrer compiles this file on its own
BlueprintFormat.cs                reads and writes .blueprint, reads .vbuild. Same rule as above
BlueprintLibrary.cs               kits from the DLL plus the player's files in
                                  BepInEx/config/ValheimTomrer/blueprints
ResolvedBlueprint.cs              a blueprint matched to live prefabs, total cost worked out once
BlueprintRules.cs                 the hammer's rules for a whole blueprint: unlocked, station in
                                  range (materials never refuse), and paying for one piece
BuildConfig.cs                    the build settings: UseChests, ChestRange. Bound in Plugin.Awake
MaterialSources.cs                where materials come from: the inventory, then every chest (cart,
                                  ship hold) in range that the player may open, nearest first
PartialBuild.cs                   which parts a click builds: paid for, station near, would stand,
                                  bottom to top. Plan, Without (a plan minus parts someone stands
                                  in), and Missing for the message
MaterialTally.cs                  the numbers of a materials list: a row per item the unbuilt parts
                                  need (have, need), a row per station, the counts. From a resolved
                                  blueprint or a plain list of pieces (the editor). No preview needed
BlueprintPreview.cs               the prefab copies. Three users: the see-through hammer preview,
                                  an unfinished build's ghost (SetPart: hidden, light blue, red) and
                                  the editor's solid model. No copy is ever a crafting station
BlueprintInfoCard.cs              fills the vanilla build card: name, icon, piece count (no
                                  controls, those are in HintRow), and the materials list on the
                                  card's right in place of the six slots. Refreshes every 0.5 s and
                                  right after a click
HintRow.cs                        the controls of blueprint mode, Continue and the capture in the
                                  game's own hint row, keyboard and pad, made of copies of the
                                  game's own entries (label, key cap, "+", wheel, pad entry)
BlueprintMode.cs                  blueprint mode of the hammer: the key cycles (unfinished builds
                                  within 40 m first, as "Continue: X (6/16)"), the wheel or L2 +
                                  right stick turns (the game's own rule), a click builds what
                                  the materials pay for (all of it when they pay for all) and
                                  keeps the rest as an unfinished build. Continue: the site's
                                  ghost is the preview, Remove removes it (at once when nothing
                                  stands, else through the remove window)

Sites/Site.cs                     one unfinished build: its own copy of the blueprint, the root
                                  pose, the file, and the built flags (read from the world)
Sites/SiteStore.cs                the per-world folder config/ValheimTomrer/sites/<world>_<uid>:
                                  Load, Add, Save, Delete. #Site: and #SiteSource: headers.
                                  RootOverride for the tests
Sites/SiteTracker.cs              once a second: what is built, finished builds, the ghosts with
                                  the hammer out within 64 m, and the next click's parts (cached).
                                  BuiltPieces: the world piece of each built part
Sites/SiteRemovePopup.cs          the remove window: the game's own popup (UnifiedPopup) with
                                  three copies of its No button: Cancel, Unbuilt parts, Whole
                                  structure. Mouse, Esc, and the pad (D-pad, cross, circle)
Sites/SiteRemoval.cs              "Whole structure": takes a site's built pieces down top to bottom
                                  the way the hammer's Remove does (Player.RemovePiece's checks and
                                  calls), keeps what a refused piece needs to stand, and the message
```

**`src/Editor/`**, the in-game editor (its own section below for the rules)

```
EditorConfig.cs                   every editor setting, and the layer number
EditorSession.cs                  open, close (keeps everything), Forget, the one per-frame tick
EditorState.cs                    selection, what is in hand, every action that changes the
                                  blueprint. No UI, no scene, the autotest drives it alone
EditorCommands.cs                 the top-bar verbs. Every one reports through a toast
WorldCapture.cs                   F8: the turned rectangle under the aim, the wheel, and the
                                  blueprint it makes
Checks.cs                         everything that can be wrong with a blueprint, worst first
BlueprintCard.cs                  what the game's build card would show for the open document:
                                  name, icon, text, and the materials list's numbers (Materials)

Catalog/PieceCatalog.cs           all 398 hammer pieces read once per world: All, Unlocked, Visible
Catalog/PieceEntry.cs             one piece as the editor needs it: cost, size, colliders,
                                  snap points, icon, its WearNTear support numbers

Doc/BlueprintDocument.cs          the open blueprint with undo and redo. Never changed in place
Doc/DocumentStore.cs              open, save, save as, rename, delete. Temp file, then rename

Input/EditorInput.cs              raw layer: keys through ZInput, the pad read once a frame
Input/Bindings.cs                 the one keyboard dispatcher, with the help table next to it
Input/PadBindings.cs              the one pad dispatcher: dialog, then piece menu, then editor
Input/PadReader.cs                reads the first pad, dead zones, and Fake for the autotest
Input/Glyphs.cs                   button names for on-screen hints, PlayStation or Xbox wording
Input/Repeater.cs                 a held direction that repeats
Input/WorldPad.cs                 the pad in the world, through the game's own button names: square
                                  (next blueprint), L2 + square (editor), L2 + triangle, the D-pad
                                  and circle (capture), which buttons the game must not see

Placement/Placement.cs            Placer, the game's placing rule in plain C#: ray in, landing
                                  spot and snap out. No GameObject, no physics call
Placement/SceneIndex.cs           the pieces that stay put, in world space, with a 0.5 m hash
Placement/MovingSet.cs            what is in hand, laid out around a pivot
Placement/Shapes.cs               colliders as plain structs, and the ray tests on them
Placement/Support.cs              the game's support rule in plain C#: how well each piece is
                                  held, which ones would fall, and the hammer's colours. The
                                  ground is y = 0, or the terrain through a callback

Ui/EditorWindow.cs                the canvas and its five regions, rebuilt after a world load. The
                                  blueprint region grows with a long materials list (FitBlueprint)
Ui/ModUi.cs                       the Blocking flag every input patch reads
Ui/UiTheme.cs                     the theme: colours, the TMP font, two own copies of its
                                  material (plain and outlined) and the chrome sprites, all taken
                                  off the running game, nothing on disk. Keyed on the live Hud
                                  object, which dies on every world load
Ui/UiBuild.cs                     small widget builders: TMP text with the HUD font, sliced
                                  sprites, and OverPicture for text drawn on the 3D pane
Ui/PadGlyphs.cs                   the game's own controller icons, out of its gamepad_glyphs TMP
                                  sprite asset, handed out as Sprites
Ui/HintBar.cs                     the row of controls along the bottom of the pane: pad icons and
                                  keyboard key caps, rebuilt only when the set changes
Ui/TopBar.cs                      Build this, the file commands, undo and redo, view switches, help
Ui/ViewportHost.cs                the 3D pane, who has the mouse, and the once-a-frame work
                                  behind it
Ui/Palette.cs                     the Pieces tab: virtualised icon grid, search, tag chips, filter
Ui/PieceListPanel.cs              the second tab: one row per piece of the open blueprint
Ui/BlueprintPanel.cs              name, description, icon, and a copy of the build card with the
                                  materials list. Scrolls when even the grown region is too short
Ui/MaterialList.cs                the materials list widget: icon, name, have / need, a bar, station
                                  rows, one footer line at the bottom (the editor hides it). Two
                                  columns past 10 rows when the caller allows. Pooled rows
Ui/SelectionPanel.cs              what is selected, the position and rotation boxes, four buttons
Ui/ChecksPanel.cs                 the problem list. Click a row to select the pieces it is about
Ui/PiecePicker.cs                 the controller's piece menu
Ui/FocusNav.cs                    the panel walk behind Tab and L3: three regions, steps by
                                  screen position, and the orange ring
Ui/Dialogs.cs                     open, save as, help, unsaved changes. One at a time
Ui/Toasts.cs                      short messages over the bottom right

View/EditorScene.cs               the little world: ground, grid, origin ring, front marker, two
                                  lights. 8000 m under the player, all on layer 30
View/PreviewCamera.cs             the switched-off camera that renders the pane into a texture
View/EditorCamera.cs              one camera: fly, look, pan, zoom. No modes.
View/SceneModel.cs                one copy per piece, kept in step with the document, and the
                                  support tint on the piece under the aim
View/GhostRenderer.cs             the see-through copy of what is in hand, in the support colours,
                                  blinking red where a click does nothing
View/SelectionBoxes.cs            gold wire boxes round the selection, one mesh per colour
View/SnapDots.cs                  snap points: cyan nearby, yellow own, orange the pair that snapped
View/ViewportRaycast.cs           screen pixels to the pane's space and back, masked to layer 30
View/CaptureBox.cs                the capture's yellow outline on the ground, one LineRenderer
View/CaptureTint.cs               the capture's glow on world pieces: yellow taken, orange across
                                  the edge. Through MaterialMan, and every one taken off again
View/CaptureHud.cs                the capture's status line top left, under the HUD root. No
                                  controls, those are in HintRow
```

**`src/Dev/`**, Debug builds only, stripped from a Release build

```
AutoTest.cs                       the scripted session: 24 scenarios, PASS/FAIL lines, screenshots,
                                  and the art guard at the end of editor_all
AutoTestPeace.cs                  stops the AI, the spawns and the raids in the test world
```

### Where to change what

| Task | File |
|---|---|
| Colours, font, sprites | `src/Editor/Ui/UiTheme.cs` |
| Add or change a keyboard key | `src/Editor/Input/Bindings.cs` (the help table is in the same file) |
| Add or change a pad button | `src/Editor/Input/PadBindings.cs` |
| Add or change a pad combo in the world (hammer, open the editor, capture) | `src/Editor/Input/WorldPad.cs`, read where the key is read (`PlayerUpdatePlacementPatch`, `EditorSession.Tick`, `WorldCapture.Tick`) |
| Change what Tab and L3 walk | `src/Editor/Ui/FocusNav.cs` |
| Change the controls shown under the 3D pane | `ViewportHost.ShowHints`, widgets in `src/Editor/Ui/HintBar.cs` |
| Add an editor setting | `src/Editor/EditorConfig.cs` (the config file only, there is no settings dialog) |
| Add a build setting | `src/Blueprints/BuildConfig.cs`, and put it back in `AutoTest.ResetSettings` |
| Change where a build takes materials from | `src/Blueprints/MaterialSources.cs` |
| Change which pieces a click builds, and in what order | `src/Blueprints/PartialBuild.cs` |
| Change the materials list: its numbers / its look / where it sits on the hammer card / in the editor | `src/Blueprints/MaterialTally.cs` / `src/Editor/Ui/MaterialList.cs` / `src/Blueprints/BlueprintInfoCard.cs` / `src/Editor/Ui/BlueprintPanel.cs` |
| Change how an unfinished build is kept, found or shown | `src/Blueprints/Sites/` (file: `SiteStore`, world and ghosts: `SiteTracker`) |
| Change an unfinished build's ghost colours | `BlueprintPreview.ReadyTint` / `RedTint` in `src/Blueprints/BlueprintPreview.cs` |
| Change Continue: the key's order, the click, what Remove does | `src/Blueprints/BlueprintMode.cs` |
| Change the remove window: its buttons, text, keys, pad | `src/Blueprints/Sites/SiteRemovePopup.cs` (the text is in `BlueprintMode.PressRemove`) |
| Change how "Whole structure" takes pieces down, or its message | `src/Blueprints/Sites/SiteRemoval.cs` |
| Change the capture: keys, sizes, the glow, the status line | `src/Editor/WorldCapture.cs`, `View/CaptureTint.cs`, `View/CaptureHud.cs` |
| Change the hints in blueprint mode or capture (the game's row along the bottom) | `src/Blueprints/HintRow.cs` (the sets are in `Hints`) |
| Add a top-bar button | `src/Editor/Ui/TopBar.cs` + the verb in `src/Editor/EditorCommands.cs` |
| Change what an edit does | `src/Editor/EditorState.cs`, the undo step in `Doc/BlueprintDocument.cs` |
| Change snapping | `src/Editor/Placement/Placement.cs` |
| Change the support rule or its colours | `src/Editor/Placement/Support.cs` |
| Change the file format | `src/Blueprints/BlueprintFormat.cs` (keep it free of other game code) |
| Add a problem to the list | `src/Editor/Checks.cs` |
| Add a game hook | a new `src/Patches/<Type><Method>Patch.cs` |
| Add a test | `src/Dev/AutoTest.cs`, then list the scenario in `scripts/autotest.sh` |

References resolve out of the **local install**, not NuGet, so compile-time and runtime
versions can never drift. `VALHEIM_INSTALL` overrides the path.

---

## Build

```bash
dotnet build                 # or: npm run build
dotnet build -c Release      # or: npm run build:release (no src/Dev, no autotest)
```

~1.6s. A successful build **auto-deploys** `ValheimTomrer.dll` + `.pdb` into
`BepInEx/plugins/ValheimTomrer/`. There is no separate install step, never copy by hand.

`assembly_valheim` and `assembly_utils` are publicized by Krafs.Publicizer, so private
and internal members are directly accessible (`fireplace.m_fuel`, not `AccessTools`).
Publicized copies land in `obj/Debug/PublicizedAssemblies/`; the game's own DLLs are
never modified.

---

## Run and verify

```bash
./scripts/dev.sh          # build, launch, stream ValheimTomrer + error lines
./scripts/dev.sh --debug  # same, plus Mono soft debugger on 127.0.0.1:10000
```

Or manually:

```bash
cd "$GAME" && ./run_bepinex.sh
```

**Verification signal.** `src/Patches/FejdStartupAwakePatch.cs` hooks `FejdStartup.Awake`,
which runs on the **main menu**, so the whole pipeline is provable in ~13s without
loading a world:

```
[Info   :ValheimTomrer] ValheimTomrer 0.1.0 loaded.
[Info   :ValheimTomrer] main menu reached | harmony=OK | publicizer=OK
```

That single line confirms plugin load, Harmony patching, *and* publicization (it reads
`FejdStartup.m_instance`, a private static). Prefer main-menu hooks for smoke tests;
reserve world loads for features that genuinely need them.

Steam does not need to be running, `steam_appid.txt` lets the game launch standalone.
Stop a test instance with:

```bash
pkill -f "valheim.app/Contents/MacOS/Valheim"
```

In-game: **F5** opens the console, then `devcommands` unlocks `god`, `fly`, `spawn`, `tod`.
The console is **off by default** (since 0.221.4). Enable it once in Settings > Gameplay >
Enable console (saved), or launch with `-console` (this session only). While it is off, F5
does nothing.

---

## Debug

UnityDoorstop can open a **Mono soft debugger**, giving real breakpoints, stepping and
locals inside both our code and Valheim's. Verified working on this machine.

```bash
./scripts/dev.sh --debug
```

Then in VS Code: **Run and Debug -> "Attach to Valheim"** (`.vscode/launch.json`).

The attach uses the **`vstuc`** debugger type from the already-installed *Visual Studio
Tools for Unity* extension. Its `endPoint` property accepts an arbitrary address, which is
what makes it work against a doorstop-hosted game rather than a Unity Editor instance.
The `ms-vscode.mono-debug` extension is **not** installed and is not required.

Portable PDBs are enabled in the csproj and deployed next to the DLL, so symbols resolve.

**The doorstop debug flag takes a value.** Passing it bare aborts `run_bepinex.sh`
silently, empty output, no game, no log:

```bash
./run_bepinex.sh --doorstop-mono-debug-enabled true    # correct
./run_bepinex.sh --doorstop-mono-debug-enabled         # WRONG, script dies
```

Related flags: `--doorstop-mono-debug-address <host:port>` (default `127.0.0.1:10000`) and
`--doorstop-mono-debug-suspend true` to freeze the game at startup until a debugger
attaches, useful for breakpoints in `Awake`/plugin load, which otherwise run too early.

Confirm the endpoint is live before blaming the editor:

```bash
lsof -nP -iTCP:10000 -sTCP:LISTEN     # expect: Valheim ... (LISTEN)
```

Logs: `BepInEx/LogOutput.log` (includes Unity log, `WriteUnityLog = true`).
Vanilla Unity log: `~/Library/Logs/IronGate/Valheim/Player.log`.

---

## Reading game code

Valheim has no modding API or docs. Finding patch targets means reading the game.

**Quick structural questions** (does a type exist? what are an enum's values? is this
field private?), read the metadata directly with `System.Reflection.Metadata` from a
throwaway `dotnet run` project in the scratchpad. This is fast and needs no tooling. It is
how `Piece.UsageTagFlags` and the `Version` constants below were obtained.

**Full decompilation**, install ILSpy's CLI:

```bash
dotnet tool install -g ilspycmd
ilspycmd -p -o /tmp/valheim-src "$MANAGED/assembly_valheim.dll"
```

Then grep the output tree. Do this when you need method bodies and call flow rather than
just signatures.

**It is installed now** (`~/.dotnet/tools/ilspycmd`), but it wants .NET 8 and only 9 is here. Run it
with roll-forward; one type at a time is quick (this is how `ZInput`'s wheel scale was read):

```bash
DOTNET_ROLL_FORWARD=Major ~/.dotnet/tools/ilspycmd -t ZInput "$MANAGED/assembly_utils.dll"
```

A fresh install once failed with "Settings file 'DotnetToolSettings.xml' was not found in the
package". Two other ways round it, both used to find the controller glyph names:

- the metadata dumper above, for names and signatures;
- a scan for the strings inside the DLL, for names that only exist as literals. macOS `strings`
  has no `-el`, so UTF-16 text needs python:
  `re.finditer(rb'(?:[\x20-\x7e]\x00){3,}', data)`.

---

## Valheim 1.0 API notes

**The build menu was overhauled.** `Piece.PieceCategory` is legacy. Pieces are now sorted
by `Piece.UsageTagFlags`, a `[Flags]` enum consumed by `ByUsagePieceList`:

```
Misc=1, Crafting=2, Building=4, Floor=8, Wall=16, Roof=32, Architecture=64,
Furniture=128, Lighting=256, Decor=512, Storage=1024, Transport=2048,
Food=4096, Meads=8192, Feasts=16384, Defense=32768, Stacks=65536,
Stairs=131072, Doors=262144, Seasonal=524288
```

`Piece.ComfortGroup` is unchanged and relevant to camp mechanics:
`Fire, Bed, Banner, Chair, Table, Carpet, Display, Decor, Garland, Lantern, Leisure`.

Data versions: `c_WorldVersion = 41`, `c_PlayerVersion = 46`, `c_ItemDataVersion = 109`.

Also newly shipped in `Managed/`: `Newtonsoft.Json` 13.0 (no need to bundle it),
`SoftReferenceableAssets`, `Splatform`/`PlayFabParty` (crossplay), `Unity.InputSystem` 1.19.

---

## Custom UI

For any UI task (HUD text, messages, hover text, map pins, popups, own windows, menu
buttons), **read the research before writing code**. It was checked against game 1.0.15.

| File | Read it for |
|---|---|
| `.claude/research/19-09-2026-custom-ui.md` | Start here. Which approach fits which need, own-window checklist, open scope questions |
| `.claude/research/19-09-2026-custom-ui-game-api.md` | Exact game methods, patch targets and code for every UI surface; fonts and sprite names |
| `.claude/research/19-09-2026-custom-ui-platform-and-mods.md` | Input, UI systems, asset bundles on macOS, how other mods do it, links |

Rules that are easy to get wrong:

- Build with uGUI + TextMeshPro in code. No `UnityEngine.UI.Text`, IMGUI only for debug.
- A new TMP text shows nothing until `.font` is set. Copy it from a vanilla text.
- **Do not share the vanilla text material.** TMP multiplies the label's colour by the material's
  face colour and then draws the material's outline and shadow over the glyph. The HUD's
  hover-name material is made for big white names over a dark world, so at 12 to 16 point every
  label reads grey whatever colour it is given. Four rounds of "the text is grey" were this, not
  the colour constant. `UiTheme` keeps two copies of it instead, both white-faced:
  `FontMaterial` plain for text on a panel, `FontOutlined` with a black edge for text over the
  3D picture (`UiBuild.OverPicture`). A copy of a loaded material is fine, nothing is on disk.
- An own window needs its own `Canvas` + `CanvasScaler` (reference pixels per unit 50) +
  `GuiScaler`, and the input-blocking patches listed in the research.
- Never use the UI calls that send network messages (listed in the research, e.g.
  `Chat.SendText`, `DamageText.ShowText`, map pins with `save: true`).
- After a game update, check that the patched methods still exist before relying on it.

---

## The in-game blueprint editor

F7 opens a window with a 3D view, the piece list and the game's snapping. It is the mod's biggest
feature, built in Phases 0 to 12 (`.claude/handoff/editor.md` has the full record, read it before
changing anything here).

**File map.** Every file of `src/Editor/` is listed in **Project structure** above, one line
each. The rules below are what that list cannot say.

**Layer 30.** Everything the editor draws lives on layer 30, 8000 m under the world
(`EditorScene.Depth`). `GameCameraAwakePatch` clears bit 30 on the player's camera, the pane's
camera draws only bit 30, and every physics query the editor makes is masked to it. The two views
can never bleed into each other.

**Input takeover.** While the window is up, `EditorInputBlockPatches.cs` holds these, all gated on
`ModUi.Blocking`: `Player.TakeInput`, `PlayerController.TakeInput`, `TextInput.IsVisible`,
`InventoryGui.Show`, `HotkeyBar.Update`, `Menu.Update`, `Minimap.Update`,
`GameCamera.UpdateMouseCapture`, `ZInput.Internal_GetMouseScrollWheel`. After a game update, check
all nine still exist, and `ZInput.TryGetButtonState` (the world pad's hold-back, below).

**Who has the mouse.** The cursor is free the whole time, so every panel and the pane itself are
clickable. On the pane a left click selects or places, right drag looks around, middle drag pans
and the wheel zooms, all with the cursor where the player left it. **C** hands the mouse to the
pane for game-style looking (`ViewportHost.Captured` / `Capture()` / `Release()`), Esc gives it
back. A click must never call `Capture()`: it used to, and clicking a piece then hid the cursor
and swung the view instead of selecting. There are no camera modes, the camera always flies.

**The hint bar.** `Ui/HintBar.cs` draws the controls along the bottom of the pane as the game does:
controller icons from the game's own `gamepad_glyphs` TMP sprite asset (`Ui/PadGlyphs.cs`), and
keyboard keys as caps cut from the same wooden button sprite as the rest of the window. Four sets:
free mouse, mouse held, pad flying, pad walking the panels. `ViewportHost.ShowHints` picks one by a
key and the bar rebuilds only when that key changes, because it is called every frame. Its words are
dark and bold (`UiTheme.TextOnPicture`) on the plain material (`UiTheme.FontMaterial`), with no
edge and no shadow. A white edge was tried and taken out at the user's request. Only the key caps
stay white, they sit on their own wooden background.

**The pad in the world.** Every key the mod reads with the window closed has a pad twin, read in
`Input/WorldPad.cs` through the game's own button names (ZInput), so the game's layout decides:
"L2" is the game's modifier `JoyAltKeys` (L2 in the default layout, L1 in the alternative one).

| Pad | Does | Key |
|---|---|---|
| square, hammer out | next blueprint (`BlueprintMode.Cycle`) | B |
| L2 + square | open the editor, the blueprint in hand rule included | F7 |
| L2 + square, in the editor | close it (`PadBindings.ClosePressed`); square alone still moves | F7 |
| L2 + triangle | start a capture, again: take it | F8 |
| D-pad, L2 + D-pad, circle | turn, both sides, width, depth; stop the capture | wheel, Esc |

- The mod reads the game's button objects directly (`ZInput.instance.GetButtonDef`), so the D-pad's
  own repeat applies. The editor reads the pad itself (`PadReader`), so its close maps the game's
  modifier to a `PadButton` (`WorldPad.ModifierButton`): the same two buttons open and close.
- **Held back from the game** (`ZInputTryGetButtonStatePatch`, one private method every
  `GetButton`, `GetButtonDown` and `GetButtonUp` goes through, Update and FixedUpdate alike): every
  game button bound to the D-pad or circle while a capture is up (hotbar, forsaken power, camera and
  minimap zoom, jump, `JoySit` in the other layouts), and every one bound to square or triangle while
  L2 is held (the inventory's `JoyButtonY`). Grouped by binding path, not by name. A button stays held
  back until it is let go (`WorldPad.Tick`), so the jump read in the next FixedUpdate never sees the
  circle that stopped the capture.
- Only where the combos work (`WorldPad.Live`): mod on, window closed, `EditorSession.CanOpen`, and no
  game menu (inventory, build menu, map, radial, trader). There the game has every button as usual.
- Hints: in blueprint mode, Continue and a capture the game's own hint row along the bottom lists
  them (`HintRow`, see "The controls row" below): the game's key caps, and on a pad the game's own
  icons (`ZInput.GetBoundKeyString`), so they follow the game's layout and the pad in hand. The card
  and the capture's status line carry no controls.

**The pad works off the crosshair.** Square moves, triangle copies and R1 deletes, each on its
own with no second button and nothing selected first: `PadBindings.TakeAimed` selects what the
middle of the view is on, keeps the whole selection when that piece is part of it, and falls back
to the existing selection when the crosshair is on nothing. Keep the three the same. They used to
disagree (square and triangle needed R2 first, delete did not, copy was `L2 + R1`) and no one could
tell what they did.

**The panel walk.** `Ui/FocusNav.cs` walks three regions (top bar, left panel, right panel). Tab or
L3 enters, Shift+Tab and L1/R1 change region, Enter and cross press, Esc and circle leave. The right
stick scrolls the region (`FocusNav.Scroll`): the list round the focused widget when it has more
than it shows, else the tallest one in the region; the ring hides while its widget is scrolled out
of sight, and the next step scrolls it back (`ShowInList`). Tab
steps in hierarchy order (`Move`). The arrows, the D-pad and the left stick step by screen position
(`Step`): up and down go to the nearest row on that side, lined up by the left edge; left and right
stay on the row. At a region's edge the step goes on into the region on that side (the top bar sits
over both panels, the panels face each other across the pane), never out of a dialog, and nothing
wraps. The step straight back returns to the widget it came from. A widget scrolled out of its list
is only reached from inside that list. The top bar's buttons are right-aligned, so on a normal
screen down from any of them lands in the right panel, the closer one. It keeps the EventSystem's
selection null for buttons, or a real pad fires the game's input module and ours in the same
frame. Palette tiles, the "In blueprint" rows and the problem rows are plain Images, not
Selectables, so the walk cannot reach them and they stay mouse only.

**A dialog is the fourth region.** Open, Save as, the help and every question take the walk on
their own, so a controller can work them: `Dialogs` names the widget to start on
(`Dialogs.FocusStart`, set by `Start()` at the end of each builder), `FocusNav.EnterDialog` goes
in and `LeaveDialog` puts the walk back where it was. L1 and R1 cannot walk out of a dialog, and
`FocusNav.ShowInList` scrolls a row into view so a long file list can be walked. While one is up
`Bindings.DialogKey` is the whole keyboard map (Tab, the arrows, Enter) and `PadBindings` step 2
is the whole pad. `Dialogs.Tick` stands back on Enter while the walk is in the dialog, or one
press would fire twice.

**The Esc and circle ladder**, in this order: a text box in a dialog gives the keyboard back
(`EditorSession.Tick`, the pad's only way out of a box), then the dialog, the piece menu, what is
in hand, release the pane, leave the walk, clear the selection, close the window.

**Row chips are tinted dark.** `item_background` is a pale sprite, and every label in the window
is white, so a row that carries text has to be tinted `UiTheme.Slot` or the text is white on
white. That is what the open dialog, the "In blueprint" list and the problem list do. Icon-only
tiles (the palette, the piece menu, the materials list's icons) keep the sprite as it is. A `Button` on such a
row also needs `Selectable.Transition.None`, or its colour transition tints the chip a second time.

**Not covered by the autotest:** a real pad pressing cross while the walk is on. The fake pad is
invisible to `InputSystemUIInputModule`, so the test can only prove the rule that prevents the
double-fire (nothing of ours selected, 0 violations). Check it by hand with a pad plugged in.

**An empty blueprint is a real file.** New, then Save as, before a single piece is placed, is the
first thing the editor does, so `BlueprintFormat` writes and reads a blueprint with no pieces and
`DocumentStore` saves one. `BlueprintLibrary.TryAdd` keeps empty ones out of `All`, because the
build tool has nothing to build from one and `ResolvedBlueprint` has no piece to take an icon from.
The problem list calls it a warning, not an error.

**Closing keeps everything.** F7 or Esc hides the window and nothing else: the blueprint with its
unsaved changes and undo, the selection, what is in hand, the camera, the left tab, the palette
search and filters, the piece menu, a dialog that is up and the panel walk all wait for the next
open. The 3D scene stays too, switched off (`ViewportHost.Sleep`/`Wake`), so the next open is
instant; only its texture is let go. A world change kills the scene, and `Wake` builds it again
from the document with the camera where it was (`EditorCamera.TakePose`). The mouse is the one
thing not kept: the window always opens with the cursor free. `EditorSession.Forget()` is the old
full wipe, and the autotest's reset between scenarios calls it.

The hand wins over what was kept, the way it always did: F7 with a blueprint in the build tool
that is not the one left open opens that one, through `EditorCommands.Take`, which asks first
when the kept one has unsaved changes. The world capture opens the same way. The same file (or
the same kit) in hand just comes back to the kept session.

**Capture runs with the window closed.** Its key is `Editor/CaptureKey`, default **F8** (free in
vanilla, and next to F7). It works like Homestead's area save:

| Input | Pad | What it does |
|---|---|---|
| F8 | L2 + triangle | a rectangle (8 x 8 m at first) on the ground under the aim; it follows the aim |
| Wheel | D-pad left, right | turns it 22.5 degrees |
| Shift / Alt / Shift + Alt + wheel | L2 + D-pad left, right / L2 + D-pad up, down / D-pad up, down | width / depth / both sides, 2 m a step, 2 to 64 m |
| F8 again | L2 + triangle | captures: the editor opens on it with Save as up, "Captured build" in the box |
| Esc | circle | stops it. Every glow comes off |

- Taken: `IsPlacedByPlayer()`, pivot inside the turned rectangle, pivot from 8 m under to 64 m over
  the ground at the centre, and a piece the hammer has. The rest inside is skipped and counted.
- The blueprint is in the rectangle's own frame, so a house built at 45 degrees comes back square.
  The origin is `EditorState.BottomCentre`, the same rule as the Center origin button.
- `CaptureTint` glows the pieces yellow, and orange the ones whose colliders cross the edge with the
  pivot outside, through the game's `MaterialMan` (the calls `WearNTear.Highlight` makes). A refresh
  (0.3 s moving, 1 s still) touches only what changed, plus a piece aimed at in the last 2 s: the
  game's own hover highlight resets that one. At most 1600 glow. It is cleared on capture, Esc,
  mod off, window open and world change.
- Save as opens only once the captured blueprint is the open one: `OpenDocument(doc, opened)` and
  `EditorCommands.Take(..., taken)` run it after the "Discard?" question, never instead of it.
- `ModUi.Blocking` stays false, so the game keeps its input. Two patches above also look at the
  capture: the wheel reads 0 while `WorldCapture.Active` (the camera does not zoom, the capture reads
  `Mouse.current.scroll` itself with the game's scale), and `Menu.Update` skips the one frame the
  capture uses Esc (`WorldCapture.TakesEscape`), or Esc would also pause the game. The pad's D-pad and
  circle are held back from the game while it is up (see "The pad in the world").
- The status line (`CaptureHud`) says the size, the turn and the counts, nothing else. The controls
  are in the game's hint row along the bottom (`HintRow`, capture set), and no "press the key again"
  message comes up when a capture starts.
- The status line sits at (28, -170) HUD units, under the game's own top-left message line (124 to
  154 down, "Built ..."), not on it.

**Support colours.** `Placement/Support.cs` ports `WearNTear.UpdateSupport` the way `Placer` ports
the placing rule: plain maths, a mesh collider is its box, the ground is y = 0. The standing pieces
are solved once per index (`EditorState.Stability`), the pieces in hand are measured against that
each time the spot changes (`EditorState.Weigh`, into `PlaceResult.Support/Falls/WouldFall`). The
ghost wears `Support.ColorOf`, the game's own `WearNTear.Highlight` formula; `CommitPlacement`
refuses a drop that would fall; `Checks` lists pieces that would fall; the piece under the aim takes
the tint through a property block, as the game's `MaterialMan` does. The material numbers come from
the game's own `GetMaterialProperties`, never typed in.

A build in the world passes a ground callback to `Support.Solve` (`PartialBuild.GroundUnder`: the
terrain height under a point, in the blueprint's space). A collider touches the ground when its
lowest point is at or under the terrain right there. Null keeps the editor's y = 0. `Evaluate`
takes the callback from the map, so always pass it a real map.

What the numbers are: the steady state. Built one piece at a time, the game lands on exactly these
(pinned to 0.01 in `editor_support`). Built all at once, as a blueprint is, the game can keep a
piece *stronger* for good, because `UpdateSupport` keeps its old value while the pieces under it do
not change. Never weaker. So the editor errs on the careful side: it may refuse a piece a blueprint
would get away with (the ninth 2 m pole on a stack), never the other way round.

**A prefab copy is made with no parent.** `BlueprintPreview.Build` instantiates with no parent,
then moves the copy under its parent. Under a switched-off parent (the ghost is built hidden) the
copy's `ZNetView` woke up later, after `m_forceDisableInit` was off, and every piece put in hand
became a real piece saved in the world 8000 m under the player. The ghost also lost its model two
frames later. `editor_support` counts world objects under -1000 m before and after and fails on any
new one.

**The materials list in the editor.** The blueprint panel's card copy shows the same `MaterialList`
under the name and text, in place of the six squares it had. The text is the hammer card's own
(`BlueprintInfoCard.Description`): the description, then "16 pieces.", no controls. There is no 6-slot limit and no
"card shows only 6" problem any more. Numbers: `BlueprintCard.Materials(document, sources, costsOff,
at)`, the document's hammer pieces (others are left out, the problem list already flags them) through
`MaterialTally.For` on a plain list of pieces. Have = the bag and the chests in range of the player
where they stand in the world; a station is "in range" of the player, "in blueprint" when the
document holds one. No footer (`MaterialList.FooterShown = false`): the blueprint stands nowhere, so
"Can build now" has no spot, and the card text already counts the pieces. One column (two only from
516 wide, the hammer card's width). Worked out when the document changes and once a second while
the window is open; `MaterialSources.Around` at most once a second (its counts are live), never while
closed. The blueprint region grows with the list (`EditorWindow.FitBlueprint`): never under its old
368, never so far that the problem list keeps less than 160; past that it scrolls with the wheel,
or the right stick with the panel walk in the right panel. The selection region moves down with it.
The rows are plain images, so the panel walk never enters the list.

---

## Hammer builds: chests, part builds, unfinished builds

Built by `.claude/plans/21-09-2026-capture-and-partial-build-done.md`. Its log, with every measured
number and gotcha, is `.claude/handoff/build-sites.md`. Read the log before changing anything here.
Phase 8 (ItemDrawers) was skipped at the user's request: there is no drawer source and no
`Build/UseDrawers` setting.

**The world-save rule.** The mod writes nothing of its own into the world save:

- no ZDO keys, no RPCs, no network messages, no ServerSync;
- pieces go up only through the game's `Player.PlacePiece`, one call per piece, and come down only
  through the game's own remove calls, the ones `Player.RemovePiece` makes (`WearNTear.Remove`, and
  the same fallbacks), one piece at a time (`SiteRemoval`);
- items leave only through the game's `Inventory` calls, on the player's or a chest's inventory;
- an unfinished build is a `.blueprint` file under `config/ValheimTomrer/sites/`, never in the world.

If a change seems to need a ZDO key, an RPC or a new prefab, the design went wrong: stop and ask.
The check, expected to print nothing:

```bash
command grep -rn "ZDO().Set\|\.Set(ZDOVars\|InvokeRPC\|RoutedRPC\|Register<" src/Blueprints src/Editor src/Patches src/Plugin.cs
```

Name the folders. With plain `src/`, macOS grep prints `src//Dev/...`, and a `grep -v "src/Dev/"`
after it lets the autotest's own two lines through (`editor_support` marks its test pieces as
the player's).

**Where the materials come from** (`MaterialSources.Around(player)`, made once per click or refresh):
the bag, then every chest within `Build/ChestRange` (20 m, 0 to 100, measured in 3D from the player
to the chest's pivot), nearest first. `Build/UseChests` off means the bag only. Chests are found with
`Piece.GetAllPiecesInRadius`, then `GetComponentInChildren<Container>`: a cart's and a karve's hold is
a child object with no `Piece`. A chest counts when all of these hold:

- a player placed it;
- the player may open it (`Container.CheckAccess`; a hold with no `Piece` runs the same rule on the
  piece that was found);
- the ward lets the player in, when the chest checks wards;
- this client owns its ZDO. `Container` saves only on the owner, so a take from a chest someone else
  owns would come back on its next load.

Counts are read live, so one list serves a whole click. `Take` counts first (`RemoveItem` returns
nothing), then takes from the bag, then from the nearest chest on.

**Which parts a click builds** (`PartialBuild.Plan`). It reads the world and changes nothing.

1. Costs off: every unbuilt part, bottom to top.
2. The materials pay for every unbuilt part: all of them by pivot height, no support filter, as the
   old all-or-nothing build did.
3. Else: support is solved once for the whole blueprint. A part that falls even when all is built
   (the model is careful) is exempt from the support step below.
4. The unbuilt parts are sorted: pivot height, then distance from the footprint's centre, then file order.
5. Rounds, until one adds nothing. Each round solves support for built + chosen, then for each part:
   cost 0 when its free-build key is set; skipped, not stopped, when it costs more than is left;
   skipped when it needs a station that is neither in range at the part's own spot nor the
   blueprint's own (built or chosen, within its range); skipped when it would fall; else chosen and
   paid off the budget. A new round starts only while some part left is paid for and has its station.
6. Each chosen piece is paid just before it goes down. One that can no longer be paid is skipped,
   never placed free.

The ground is the terrain under each point (`PartialBuild.GroundUnder`, one read per 5 cm). 400
pieces plan in 15 to 60 ms. The messages: "Built 4 of 16 pieces of Workshop. Still missing: 20
Wood.", "Workshop planned, nothing built yet. Missing: ...".

**No copy is a crafting station.** `BlueprintPreview.StripToVisuals` switches off and destroys every
`CraftingStation` on a copy and hides its area marker. Before that, the hammer's preview of the
Workshop counted as a workbench (`HaveBuildStationInRange` found the ghost), so a piece that needs one
could be built with no real bench near. The game's own one-piece ghost still keeps its station, and
its first range call throws in `CraftingStation.GetExtensions`. So read `m_buildRange` /
`m_rangeBuild`, never `GetStationBuildRange()`, on a station that may be a ghost.

**Unfinished builds (sites).** A click that leaves parts unbuilt, even all of them, keeps a site
and goes straight to Continue on it (see "After a click" below).

- **The file:** `sites/<world name>_<world uid>/<name>_<yyyyMMdd-HHmmss>.blueprint` under
  `config/ValheimTomrer`, a folder `BlueprintLibrary` never reads. The blueprint as it was at the
  click, plus `#Site:x;y;z;yaw` (the root pose) and `#SiteSource:`. Those headers are read in
  `SiteStore`, never in `BlueprintFormat`. Written once (temp file, then rename), deleted when the
  last part stands or the plan is forgotten. What is built is never written. Tests point
  `SiteStore.RootOverride` at `.devtest/sites`.
- **The built match:** a world piece with a valid `ZNetView`, the same prefab, pivot within 5 cm,
  turn within 2 degrees. One world piece matches one part. `SiteTracker` reads it once a second and
  right after a click, only for sites within 128 m (farther pieces may not be loaded); a far site
  keeps its last flags. A piece built or broken by hand counts.
- **The ghosts:** shown with the hammer out (not the hoe or the cultivator) within 64 m of the site's
  box (the parts' pivots grown by 2 m). Built when first shown, a few pieces a frame, with each
  renderer's `forceRenderingOff` and the root unturned until done (`MeasureBounds` needs both), then
  moved. Kept hidden after, dropped on delete, Load and world change.
- **The plan cache:** `SiteTracker.Plan(site, sources, noCost)` runs `PartialBuild.Plan` again only
  when its key changes: built flags, item counts, stations of a needed kind whose range reaches the
  site, free-build keys, costs on or off. The ground is not in the key. `Site.Ready` and
  `ReadyOrder` are its answer.
- A world change is noticed through a new `ZNetScene`: the sites are read again.

**The ghost looks** (`SetPart`): a built part is hidden, a part the next click builds is **light
blue**, the rest is **red** (waiting for materials, a station or support). Both tints go through the
game's `MaterialMan` on `_Color` and `_EmissionColor` (the calls `Piece.SetInvalidPlacementHeightlight`
makes), with the glow at 0.7 of the colour, as the game's red. The colours sit side by side in
`BlueprintPreview`: `RedTint` (the game's `Color.red`) and `ReadyTint` (0.35, 0.7, 1). The blue is
needed: the game's ghost material is nearly opaque, and an untinted part looks like built wood. Only
a site's ghost is blue (`PreviewStyle.Site()`); the hammer's preview of a new blueprint keeps the
plain ghost look. A part that changes look loses its old tint (ready to built: none, ready to
waiting: red).

**Continue.** The blueprint key offers every site within 40 m of its box first, nearest first
("Continue: Workshop (6/16)", plus ", 6 m behind" when two sites share a name), then the normal
blueprints. In Continue `BlueprintMode.CurrentSite` is the site and `Current` its blueprint; the
preview is the tracker's own ghost (no second copy), so nothing follows the aim and the wheel and
the pad turn do nothing. The click reads the world again, takes the plan from the tracker's cache
(`Site.ReadyOrder`), refuses on a `BlockedReason`, then leaves out each part whose own box (grown
0.6 m) holds a character, and whatever would need it (`PartialBuild.Without`); a part left out stays
ready. The click that puts the last part up deletes the file and ends blueprint mode. While
blueprint mode is on (Continue too) the game's own `UpdatePlacement` does not run, so Remove never
takes down the aimed piece there. The site's ghost is never tinted by the click, so nothing stays
red after Continue ends. On the pad: square is the key, R2 the click, R1 (the game's `JoyRemove`)
is Remove.

**Remove in Continue** (`BlueprintMode.PressRemove`: the game's `Remove` on release, or `JoyRemove`,
never with L2 held). It reads the world first.

| What stands | What one press does |
|---|---|
| none of the build | the plan goes at once (file and ghost), blueprint mode ends, "Plan for Workshop removed." No window |
| some of it | the remove window: "Remove Workshop?", "6 of 16 pieces are built.", three buttons |

| Button | Does |
|---|---|
| Cancel (left) | nothing. Continue stays on. Also Esc, circle, or cross while Cancel is picked |
| Unbuilt parts | the plan goes, the built pieces stay. "Plan for Workshop removed. The 6 built pieces stay." |
| Whole structure | the plan goes and every built piece comes down (`SiteRemoval.TakeDown`). "Workshop removed: 6 pieces taken down." |

Either removal ends blueprint mode. The window (`SiteRemovePopup`):

- **The game's own popup.** `UnifiedPopup` has one row for two buttons (No, Yes), so this pushes a
  popup of its own `PopupType` (100, none of the game's). The game shows its panel, dark background
  and title for it, and none of its own buttons. Three copies of the game's No button (`buttonLeft`)
  go on the panel, which widens to fit (about 580 units, the game's is 400). The panel's
  width and the popup's default button go back, and the copies hide, whenever this popup is not
  the one showing (closed, or a game popup pushed over it). Copies are made once per popup object;
  a world load makes a new one.
- **It blocks like the game's popups**, through the game's own checks: `Menu.IsVisible()` is true
  while a `UnifiedPopup` shows, so the player cannot move, act, build, open the inventory, the map
  or the editor, and the cursor is free. No input patch was needed for it.
- **Pad.** Cancel is picked at first (the game's "are you sure" dialogs start on No). The D-pad and
  the left stick move along the row (explicit navigation, the game's UI module), cross presses the
  picked button, circle cancels (the Cancel copy keeps the game's `UIGamePad` on Esc and
  `JoyButtonB`). The icons sit where the game puts them, on the button's top right corner: circle on
  Cancel, cross on the picked button. Their text is set from the game's `$KEY_JoyButtonB` /
  `$KEY_JoyButtonA`: the copies are not in the game's list of texts it translates again, and the
  prefab's own hint text reads `MISSING BUTTON DEF "ButtonB"`.
- **The closing press stays out of the game.** Circle that closed it was a jump in the next physics
  step, when the popup no longer holds the player (1.4 m, measured), so every game button bound to
  cross and circle is reset (`ZInput.ResetButtonStatus`, as `InventoryGui` does). The Esc that cancelled
  would open the pause menu in the same frame, so `Menu.Update` skips that frame
  (`SiteRemovePopup.TakesEscape`).

**Whole structure** (`SiteRemoval.TakeDown`), the rules of the hammer's Remove (`Player.RemovePiece`):

- The world is read again (`SiteTracker.BuiltPieces`). Refused as a whole, with the plan kept and
  Continue on: more than 40 m from the site's box, no stamina for one swing.
- Each piece gets the game's checks, silently: the tool can remove it (`m_canRemovePieces`, feasts
  apart), `m_canBeRemoved`, not in a no-build zone, `PrivateArea.CheckAccess` (no flash), the station
  rule of `CheckCanRemovePiece` (the station near the **player**, unless costs are off or
  `NoWorkbench`), a `ZNetView`, `Piece.CanBeRemoved()` (a chest must be empty, a ship empty).
- Then the body of `RemovePiece`, call for call: `IRemoved.OnRemoved`, `WearNTear.Remove()` (which
  drops the materials and plays the break), else the same fallbacks. So the materials come back
  exactly as the hammer's Remove gives them: dropped where each piece stood, picked up as usual.
  Per piece the skill's remove debt and the game's "pieces removed" count, as `UpdatePlacement` does.
  One swing for the whole take-down (stamina, durability, noise), as for a build click.
- Top to bottom: the reverse of the build order (`PartialBuild.BuildOrder`), all in one frame.
- A refused piece stays, and so does every piece it needs to stand (the support model, tried top to
  bottom near it), so nothing is left to fall. The message counts them: "Workshop removed: 12 of 15
  pieces taken down. Left standing: 1 can't be removed, 2 hold it up.", "... 5 need a workbench
  nearby." The plan is removed all the same.
- The station check reads the range the station worked out last (`m_buildRange`, `m_rangeBuild`) and
  counts only real network stations, never a ghost (see "No copy is a crafting station").

**After a click** (`BlueprintMode.Build`, the mouse and R2 alike):

| The click | Then |
|---|---|
| builds all of it | blueprint mode ends (`Exit`): the hammer is back on its own piece, as after picking one from the build menu |
| leaves parts, even all of them | keeps a site and goes to Continue on it (`ContinueAfterClick`), with the key's label ("Continue: Workshop (6/16)"). No second message |
| Continue's last part | "Workshop finished.", the file goes, blueprint mode ends. No normal blueprint is left in hand |

The site's ghost is built over the next few frames (8 pieces a frame, 0.05 s at most for the
Workshop), so for those frames nothing shows the missing parts. If the site file cannot be written, the blueprint stays in hand as before.
The card's title reads "Continue: Workshop" in Continue, from the key or from a click.

**The materials list on the hammer card.** In blueprint mode `BlueprintInfoCard` hides the card's six
requirement slots and puts a `MaterialList` on the card's right, bottom edges level, never lower than
the card. A row per item: its icon, its name, have / need (have green when enough, red when short)
and a bar under it; then a row per station: "in range", "not in range", "in blueprint" or "not
needed". Past 10 rows it takes two columns, nothing is hidden. The text under the name is the
blueprint's own description and "16 pieces." (Continue: the description only). No controls: those
are in the game's hint row (`HintRow`). The footer is one line: "Can build now: 6 of 16 pieces", in Continue "Built 6 of 16. Can
build now: 3 more." Where the materials come from (bag, chests) is not shown: the user took that line
out. When the list is shorter than the card, the footer still sits at the bottom
(`MaterialList.MinHeight`) and the spare room goes above its line, so nothing is empty under it.
It is a child of the card (`SelectedInfo`, scale 1.25), so it hides with the HUD (Ctrl+F3). It
does not grow up over the card: the game moves the stamina and eitr bars to y 320 and 285 above the
card in build mode. Its background is the card's own `Bkg2` sprite, at 70 % black instead of 50 % so
red numbers read over bright grass. The game sets its slots every frame, so a normal piece gets them
back with nothing to undo; the postfix only hides the list. Numbers: `MaterialTally.For` every 0.5 s
and at once after a click (`BlueprintMode.TryBuild` calls `RefreshSoon`). In Continue the rows are the
missing parts' cost and "Can build now" is the tracker's cached `site.ReadyCount`. For a normal
blueprint the card keeps its own plan cache: item counts, stations within 64 m, free-build keys,
costs on or off, and the preview's spot (again after 0.5 m or 7.5 degrees). A plan over 5 ms waits
until the preview holds still for one refresh, so moving a 400-piece blueprint (15 ms) never
stutters. With no aim it keeps the last spot the preview stood on. `MaterialList` knows nothing of
the card (parent, width, most columns and least height come from the caller).

**The controls row.** `HintRow` puts the controls of blueprint mode, Continue and the capture in the
game's own hint row along the bottom of the screen (`KeyHints`), in its look:

| Set | Keyboard | Pad (default layout, Xbox icons as the game shows them) |
|---|---|---|
| Blueprint | Build Mouse-1, Next blueprint B, Edit F7, Build Menu Mouse-2, Rotate wheel | RT, X, LT + X, A, LT + RS |
| Continue | Build what you can Mouse-1, Remove (the game's Remove key), Next B, Edit F7, Build Menu Mouse-2 | RT, RB, X, LT + X, A |
| Capture | Capture F8, Turn wheel, Width Shift + wheel, Depth Alt + wheel, Both sides Shift + Alt + wheel, Stop Esc | LT + Y, D-pad left / right, LT + left / right, LT + up / down, up / down, B |

- **How.** A group of our own under `KeyHints`, next to the game's `BuildHints`, with the same rect
  and a copy of its `Keyboard` and `Gamepad` rows (right-aligned layouts, spacing 36 and 40), emptied.
  Each entry is a copy of the game's own: the keyboard `Place` entry (label, `key_bkg` cap), the `+`
  of its Copy entry, the `mousew_icon` of its Rotate entry, the pad `Text - Place` entry. So font,
  size, colour, material and caps are the game's, never look-alikes. The labels' auto-size is
  switched off at their largest size (18), which is what the game's own show at.
- **Which set, which row.** Capture wins over blueprint mode. The pad row shows while
  `ZInput.IsGamepadActive()`, the keyboard row while not and `IsMouseActive()` (the game's own
  `UIInputHint` rule), checked every frame, so it switches when the game's does.
- **Taking over.** `KeyHintsUpdateHintsPatch` is a prefix on `KeyHints.UpdateHints`. While a set
  shows, the game's groups go off once and its update is skipped, so its build group is not switched
  on and off every frame (that re-ran its `UIInputHint.OnEnable` and a layout rebuild each frame).
  When the mode ends the game's update runs again and sets every group as always: the vanilla row
  is back with nothing to undo. After a game update, check `KeyHints.UpdateHints` still exists.
- **When not.** Only where the game would show its row: its key hints setting on, the player alive,
  no chat, no pause, no skills or trophies panel, no inventory, radial, build menu or barber. Those
  keep the game's row. The editor window open: the game's row too, as before.
- **Texts.** Pad icons: `Localization.GetBoundKeyString("Joy...")`, the same call the game's own
  entries go through, so the same family (xbox, ps5, switch2) and layout. Keys: the game's names for
  its own buttons (`Attack`, `Remove`, `BuildMenu`), `ZInput.KeyCodeToDisplayName` for the mod's
  config keys and Esc; Shift and Alt are plain "Shift" and "Alt" (either side works). The pad's build
  menu button is `JoyUse`, or `JoyBuildMenu` in the alternative layouts (`Player.UpdateBuildGuiInput`);
  Rotate is `JoyRotate` + `JoyRStick` in the default layout, `JoyRotate / JoyRotateRight` in the
  others. Written again only when a text changes; a rebound key shows within a second.
- Our copies are not in the game's localization table, so its re-localizing never touches them.
- Rotate is the last blueprint entry, where the game's own row has it: the wheel is taller than a
  key cap, and further left it sat right under the materials list.

---

## Autotest

**Scenarios** (`./scripts/autotest.sh <name>`, Debug builds only):

| Scenario | What it proves |
|---|---|
| `probe`, `dump` | measures layers, UI, input and pieces into `.devtest/*.txt`. No feature. |
| `probe_build` | measures chests, support speed, ground height, glow and the build card into `.devtest/probe-build.txt`. Places and removes its own chests and floors. Not in `editor_all` |
| `build_sources` | the kit paid from the inventory and chests at 5, 15 and 30 m: counts, take order (a cart between them), the exact amounts left, the chests' saved items, `ChestRange` and `UseChests`, and that a chest no player placed, another player's private chest and a chest behind another player's ward do not count |
| `build_partial` | a click builds what the materials pay for and what would stand, bottom to top: half the kit's wood (paid exactly, stands in the game and the model, lower part first), a made-up two-storey house with wood for one and a half, nothing affordable, everything, costs off, `Plan` on 400 pieces within 3 x Phase 0's Solve time per round, and the preview's workbench copy is no station. Half the wood and nothing affordable go straight to Continue on the new site (the key's label, its ghost as the preview, the card's "Continue: Workshop"); everything and costs off leave blueprint mode with the game's own piece and card back |
| `build_sites` | a partial or empty click keeps the rest as an unfinished build: the file in `.devtest/sites/<world>_<uid>/` with the preview's pose, never read by the library; ghosts for the missing parts (red when not paid, light blue when the next click builds them, each part's `_Color` and glow read back from `MaterialMan`; red again, and no blue left, once the wood is gone; the hammer's normal preview has no tint), shown only with the hammer out within 64 m; the plan not run again when nothing changed; Load reads it back and the world says what is built; a destroyed piece's ghost comes back; a site built in full is finished and its file deleted; nothing left under -1000 m |
| `build_continue` | every set-up click goes straight to Continue on its site (one through the pad's R2); the key offers "Continue:" first, and square on a pad the game reads offers the same; the ghost stays put when the camera and the wheel turn; one click finishes the site from a chest (file gone, chest empty, nothing falls in 15 s, blueprint mode off, the game's own piece back); thirds finish in three clicks, the third through R2, also back to the game's piece; Remove with nothing built forgets the plan at once, no window; with some built Remove opens the window (the game's popup, `Menu.IsVisible`, three buttons in a row that fit, the press did nothing else), W, B, a click beside it, Tab, M and F7 do nothing, a click on Cancel and Esc change nothing and keep Continue (no pause menu), a click on Unbuilt parts removes the plan and keeps what stands; on the pad R1 opens it on Cancel with the game's circle icon, cross there and circle cancel with no jump, the stick, R2, square and triangle do nothing, the D-pad walks the row and stops at its ends, the cross icon follows, cross on Whole structure takes every built piece down with the materials back as `Piece.DropResources` gives them; an unremovable top piece stays with the 2 pieces under it and nothing falls in 5 s; 28 m from the only workbench the 5 walls stay and "5 need a workbench nearby"; two sites told apart; 60 m away only blueprints; picking a piece ends Continue; the player standing in a ready part leaves only that one out, the parts the click built lose their blue, the one left out keeps it |
| `card_materials` | the hammer card's list: a row per item and station with `MaterialTally`'s numbers and icons, short in Warn and full in Good, "in blueprint" for the kit's bench, the footer's count equals `PartialBuild.Plan`'s, no "From:" text anywhere (with a chest in range), the footer the last line with only the padding under it, slots hidden, own text material, on screen, hides with Ctrl+F3, no plan rerun while still, the list updated right after a click and the card on Continue at once ("Continue: Workshop"); Continue: need = the missing parts' cost, footer "Built 6 of 16. Can build now: 3 more."; the card lists no controls ("16 pieces." only, Continue the description only), with a press on a pad the game reads it still lists none and the game's hint row under it shows the pad set, back on the keyboard the keyboard set; 12 items in two columns, none cut or overlapping; a 400-piece plan waits while the preview moves and runs once when it stops; a normal piece brings the game's slots back. Screenshots `card-materials-1..3`, `card-materials-pad` |
| `hint_row` | the controls in the game's hint row: the game's own row first (keyboard, then a press on a pad the game reads); a blueprint in hand, Continue and a capture with the hammer out and with it away each show exactly their set (labels, key caps, "+", wheel; the game's icon tags on the pad, the build icon the same as the game's own Place), keyboard and pad, and switch live (a pad press, a mouse move); every game group off, and not switched on and off each frame; the game's font, size, colour, material, caps, wheel and spacing; in the game's row, on screen, left to right, the game's gaps, right-aligned, nothing drawn on the card or the materials list (the wheel's visible pixels read back); the card and the capture's status line carry no controls, no message when a capture starts; after each mode the game's row is back exactly (keyboard and pad), with the hammer away nothing, with a club the combat hints. Screenshots `hints-vanilla-keys/pad`, `hints-blueprint-keys/pad`, `hints-continue-keys/pad`, `hints-capture-hammer-keys/pad`, `hints-capture-nohammer-keys/pad` |
| `blueprints` | every shipped kit is built with the hammer: unlocks, cost, support, and turned by a real wheel notch and a made-up pad's L2 + right stick; the empty click goes to Continue, one Remove forgets it with no window; the build in full leaves blueprint mode; square cycles the same entries in the same order as B; L2 + square does not cycle, it opens the editor on the one in hand |
| `editor_open` | the key opens the window and the game stops taking input; L2 + square on a pad the game reads opens it on the blueprint in hand (no inventory, no sitting), L2 + square on the editor's fake pad closes it and does not start a move |
| `editor_view` | a kit stands in the 3D pane, the camera turns, zooms and flies, nothing leaks on close |
| `editor_files` | the writer round-trips every blueprint, and the file commands work |
| `editor_palette` | the catalog, the palette counts, the filters |
| `editor_snap` | the placing and snapping engine against a table of rays, no UI |
| `editor_edit` | place, select, copy, turn, nudge, undo |
| `editor_panels` | the build card (its text: the description and "N pieces.", no controls), the selection fields, the problem list; L3, R1 and the right stick scroll the right panel up and down, the ring shown only while its widget is in sight; the materials list: a row per item and station of the document, have = `MaterialSources.Around(player).Count`, stations by the player, no footer, one column, nothing the walk can reach, the region grows so a 4-row card needs no scroll, 12 items + the workbench in one card with none cut or overlapping, no card-slots problem, the bag followed within a second, 2 to 3 refreshes in 2.5 s. Screenshots `editor-panels-1-right-panel`, `editor-panels-card` |
| `editor_keys` | every key, the wheel, the mouse, the top bar, the dialogs |
| `editor_pad` | every controller button through a made-up pad, and the piece menu. The help table has 22 rows, the close row included |
| `editor_focus` | the pad and Tab walk the top bar and both panels, and press what they find |
| `editor_keep` | close and open again finds everything as it was, with F7 and with L2 + square, the hand blueprint asks over unsaved changes, a dead pane is built again on the same view, Forget leaves nothing |
| `editor_build` | a blueprint made in the editor, built in the world (blueprint mode off after), taken back with the key, then edited again |
| `editor_capture` | F8 on bare ground; a kit built turned 45 degrees, a wall across the edge and a cultivator piece inside; real wheel notches turn and size the rectangle (no camera zoom); yellow and orange glow counts; the capture holds the kit file unturned; Save as up, and after "Discard?" over a kept blueprint; Esc leaves no colour; the whole capture on a pad the game reads (L2 + triangle, each D-pad step, the status line one line with no controls and the game's hint row on the capture's pad set, taken into Save as, a second one stopped by circle) with the hotbar, the forsaken power, the camera and minimap zoom, the inventory and the jump untouched, then the same presses with no capture up reaching the game; the glow's cost on 400 floors. Screenshot `editor-capture-3-pad` |
| `editor_support` | the support rule against the game's own numbers on test structures and a kit, the ghost's colours on the picture, a refused drop, nothing left in the world. `VT_SUPPORT_EDITOR_ONLY=1` skips the world half |
| `editor_all` | all of the above except `probe_build`, in one game: the 17 editor ones and `blueprints`, then `build_sources`, `build_partial`, `build_sites`, `build_continue`, `card_materials`, `hint_row`. Then the art guard. **This is the one to run.** |

`editor_all` is the release check: `DONE pass=N fail=0` plus `art guard:` in the output. It takes
about 11 minutes (23 scenarios, 1308 checks on 21-09-2026, with the pad checks). Every
world-building scenario shares one build spot (`AutoTest.MoveToBuildSpot`): searched from the
middle of the world, found once a run, always faced the same way, and **levelled flat with a
terrain op** before anything is built. All three matter. Valheim's meadows roll by 1 to 2 m over a
16 m square, and on a slope a kit piece loses its support and the check is a coin flip.

Between two scenarios the chain resets (`AutoTest.Reset`): the editor closes, the capture stops,
the blueprint leaves the hammer, the fake pad is dropped, the library reloads, every unfinished
build is forgotten and `.devtest/sites` deleted, and every editor and build setting goes back to its
default, so no run changes the player's config file. `VT_CHAIN="editor_build,blueprints"
./scripts/autotest.sh editor_all` runs only those two, in that order, which is how to reproduce a
scenario that leaves something behind without sitting through all twenty-three.

Every run of `autotest.sh` deletes `.devtest/*.png` first. To look at one scenario's screenshots,
run it alone or last.

**The test world is quiet.** `src/Dev/AutoTestPeace.cs` runs at the start of every scenario, before
the first check. It stops every AI (a prefix on `BaseAI.UpdateAI`), stops new spawns
(`SpawnSystem.m_nospawn`), sets the raid chance to zero, and despawns whatever is already standing
around (17 creatures at the spawn point on one run). Without it a greydwarf could wander in, hit
the structure under test, and a support check failed for a reason that had nothing to do with the
code. That was the real cause of the flaky runs. Debug builds only, like `AutoTest` itself.

**The art rule.** The mod never writes a model, mesh, texture, icon, sprite, atlas or material to
disk, in any format, under any folder. The only files it writes are `.blueprint` text files: the
player's blueprints in `config/ValheimTomrer/blueprints/` and the unfinished builds in
`config/ValheimTomrer/sites/`. Everything the mod draws is made at runtime from what the game already
loaded: prefab meshes are cloned, shaders come from `Shader.Find` among the loaded ones, icons come
from the piece's or the item's own sprite, line materials are made in code. `editor_all` ends with a
guard (`CheckNoArtWritten`) that walks every folder the mod can write to and fails on anything else.
The chain deletes the sites folder between scenarios, so `ClearSites` runs the same walk first and
keeps what it found, plus the files the store says it kept. The guard fails when the walks saw no site
file, or missed one the store kept, so a folder list that lost `sites/` cannot pass. A `VT_CHAIN` run
in which no scenario kept a site skips that part with a logged note. `autotest.sh` then walks the repo for image, mesh and bundle files and
prints `art guard:`. Do not weaken either.

---

## Tomrer, the desktop editor (separate repo)

**The mod does this itself now.** Since Phase 11 the in-game editor covers everything Tomrer does,
so `../Tomrer` may be removed. Do not start work there, and do not change anything in it.

What is still true while it exists:

- It reads and writes the same `blueprints/*.blueprint` files.
- Its blueprint-check compiles `src/Blueprints/Blueprint.cs` and `BlueprintFormat.cs` on their own,
  against `UnityEngine.CoreModule` alone. **Keep those two files free of other game code**, whether
  Tomrer stays or goes: it is a cheap way to keep the format readable outside the game.
- If the mod's reader changes and Tomrer is still around, `src/format/blueprint.ts` there has to
  change with it.

---

## Conventions

- One patch class per target, file named `<Type><Method>Patch.cs` in `src/Patches/`.
- Gate feature behaviour on `ValheimTomrerPlugin.ModEnabled.Value`.
- Log through `ValheimTomrerPlugin.Log` (BepInEx `ManualLogSource`), never `Debug.Log`.
- Keep `OnDestroy` calling `_harmony.UnpatchSelf()`, required for ScriptEngine hot reload
  not to stack duplicate patches.
- **Do not** add `[BepInProcess("valheim.exe")]`. Common Valheim templates include it; on
  macOS the executable is `Valheim` and the attribute silently prevents loading.
- **Do not** switch BepInEx/game references to NuGet. Local refs guarantee version match.
- **Add or remove a file, update the tree in Project structure in the same commit.** That list
  is what keeps the next session from searching the repo.

---

## Failure modes and their signatures

| Symptom | Cause |
|---|---|
| No `LogOutput.log` at all, game runs fine | arm64, `ARCHPREFERENCE` reverted |
| "cannot be opened because the developer cannot be verified" | quarantine xattr on `libdoorstop.dylib` |
| `0 plugins to load` | DLL not deployed; re-run `dotnet build` |
| Plugin loads, patch never fires | wrong method name/overload, grep log for `HarmonyException` |
| Compile error reaching a private member | assembly missing from `<Publicize>` in the csproj |
| `DllNotFoundException: AppleCoreNativeMac` | **benign**, vanilla Apple GameKit probe failing under Rosetta, unrelated to mods |
| Launcher exits instantly, no output at all | a `--doorstop-*` flag passed without its value |
| Debugger won't attach | game not started with `--debug`; check `lsof -nP -iTCP:10000` |
| Editor window opens but the 3D pane is black | the pane camera's culling mask lost layer 30, or its `RenderTexture` was released (`PreviewCamera.Resize`) |
| The 3D pane looks frozen with a pad in hand | focus is in a panel, press circle to go back to the pane |
| Editor text reads grey however light the colour | a label is on the shared hover-name material, not `UiTheme.FontMaterial` |
| The editor's ghost vanishes after two frames, the world gains objects 8000 m down | a prefab copy was instantiated under a switched-off parent, see "A prefab copy is made with no parent" |
| A piece shows as a plain box in the pane | the prefab has no `MeshFilter`, or it draws through `InstanceRenderer`, which a mesh-only clone cannot copy |
| Blueprint mode never starts in a test run, B does nothing | the test character's hammer broke (the character is saved on quit, run after run). `AutoTest.EquipHammer` repairs it |
| B says "No blueprints" right after a teleport | the player landed in water: swimming puts the hammer away. Pick dry land and equip again |
| A piece that needs a workbench is built with no real bench near | a preview copy kept its `CraftingStation`, see "No copy is a crafting station" |
| `NullReferenceException` in `CraftingStation.GetExtensions` | `GetStationBuildRange()` on a ghost station, which has no area marker. The game's one-piece ghost does it once; read `m_buildRange` instead |
| Materials taken from a chest come back after a reload | the chest's ZDO belongs to another client, which saves over the take. `MaterialSources` skips such chests |
| A house stays yellow or orange after a capture | an exit path missed `CaptureTint.Clear` (capture, Esc, mod off, window open, world change) |
| Esc during a capture also opens the pause menu | `Menu.Update` did not skip that frame (`WorldCapture.TakesEscape`) |
| Remove in Continue does nothing | the game reads Remove on release (`GetButtonUp`). On this Mac it is Left Command, not the middle mouse |
| The remove window's circle icon reads `MISSING BUTTON DEF "ButtonB"` down the screen | a copied button's hint kept the prefab's text. `SiteRemovePopup.ShowHints` sets it from `$KEY_JoyButtonB`; the game re-translates only its own texts |
| A test's mouse click on a button does nothing, the button stays pressed | `MouseState.WithButton` changes the struct it is called on, so the "release" state still had the button down. Build the press state on its own (`AutoTest.ClickScreen`) |
| Circle that closed the remove window also jumps | circle is the jump in the default layout, read in FixedUpdate after the popup is gone (1.4 m measured). `SiteRemovePopup.SwallowPad` resets every game button bound to cross and circle |
| A site's ready parts look like built wood | they lost the blue `ReadyTint`: the ghost material is nearly opaque, so without a tint nothing shows |
| An unfinished build's ghost has a zero or wrong size | it was filled with its root switched off: `MeasureBounds` skips inactive renderers. Fill with `forceRenderingOff` |
| The art guard fails with "the walk saw 0 unfinished-build files" | the guard's folder list (`AutoTest.WalkWrittenFiles`) lost the sites folder, or no scenario in the chain kept a site until `ClearSites` |
| Screenshots of an earlier run are gone | every `autotest.sh` run deletes `.devtest/*.png` |
| The pad's L2 + triangle opens the inventory, or the D-pad moves the hotbar during a capture | `ZInputTryGetButtonStatePatch` did not apply (`ZInput.TryGetButtonState` renamed or inlined), or `WorldPad.Live` is false (a game menu is up) |
| In blueprint mode or a capture the bottom row still shows the game's snapping and copy hints | `KeyHintsUpdateHintsPatch` did not apply (`KeyHints.UpdateHints` renamed), or the log says "hint row: the game's build hints do not look as expected" (the game renamed its `Place`, `key_bkg`, `Text - Place` entries or the wheel sprite) |
| A test says the game's pad entry reads `MISSING BUTTON DEF "Place"` | normal while the keyboard is in use: the game localizes its hidden pad row with keyboard names. Compare pad icons only while `ZInput.IsGamepadActive()` |
| A pad test presses a button and nothing happens | the button went to a real pad: test presses go to `AutoTest`'s own "AutoTestPad DualSense" device, never `InputSystem.FindControl`, which can pick the real DualSense |
