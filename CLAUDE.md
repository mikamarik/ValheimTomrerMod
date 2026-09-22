# ValheimTomrer, agent guide

Client-side BepInEx plugin for **Valheim 1.0** on **macOS / Apple Silicon**.
Everything below was verified against this machine's actual install (environment 2026-09-19,
file list 2026-09-21).

**Do not map the repo and do not hunt for a file.** Every file is listed with a one-line
description under [Project structure](#project-structure-every-file), plus a
[Where to change what](#where-to-change-what) table. Go straight to the file. Search only to
find a symbol inside a file this guide already pointed you at, or when the guide is wrong,
and then fix the guide.

**This file has the rules:** what to do, what never to break. The reasons, the measured numbers
and the full behaviour of each feature are in `.claude/design-decisions.md` (the § numbers below).
Read the section for an area before you change how a feature behaves or undo a rule. Skip it for
ordinary work.

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

### Never break

**The world-save rule.** The mod writes nothing of its own into the world save:

- no ZDO keys, no RPCs, no network messages, no ServerSync;
- pieces go up only through the game's `Player.PlacePiece`, one call per piece, and come down only
  through the game's own remove calls, the ones `Player.RemovePiece` makes (`WearNTear.Remove`, and
  the same fallbacks), one piece at a time (`SiteRemoval`);
- items leave only through the game's `Inventory` calls, on the player's or a chest's inventory;
- an unfinished build is a `.blueprint` file under `config/ValheimTomrer/sites/`, never in the world.

If a change seems to need a ZDO key, an RPC or a new prefab, the design went wrong: stop and ask.
The check, expected to print nothing (name the folders, plain `src/` lets the autotest's own lines
through, §6):

```bash
command grep -rn "ZDO().Set\|\.Set(ZDOVars\|InvokeRPC\|RoutedRPC\|Register<" src/Blueprints src/Editor src/Patches src/Plugin.cs
```

**The art rule.** The mod never writes a model, mesh, texture, icon, sprite, atlas or material to
disk, in any format, under any folder. The only files it writes are `.blueprint` text files: the
player's blueprints in `config/ValheimTomrer/blueprints/` and the unfinished builds in
`config/ValheimTomrer/sites/`. Everything it draws is made at runtime from what the game already
loaded. `editor_all` ends with a guard for it and `autotest.sh` walks the repo (§10). Do not weaken
either.

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

## Maintaining this file

It is loaded on every turn, so every line in it is paid for on every turn. It reached 13,000 words
because each finished phase added its own section. Before adding anything, apply three tests:

1. **What, not why.** A rule goes here in one line: what to do, or what never to break. The
   reason, the history, what was tried, the measured numbers and the full behaviour of a feature go
   in `.claude/design-decisions.md`, with at most a one-line rule here pointing to its section.
2. **Not already checked.** If the autotest, the compiler or a guard already fails on the mistake,
   say it in a clause, not a section.
3. **Not already said.** Search this file first. The pad combos were once explained in four places.

A new feature is not a new section here. Its plan goes to `.claude/plans/`, its log to
`.claude/handoff/`, its reasons to `.claude/design-decisions.md`, and this file gets only the lines a
later session would break something without. Removing a stale line is as welcome as adding a true
one.

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

### Apple Silicon: BepInEx fails silently

On native arm64 no plugin loads and no `LogOutput.log` is written; the game looks vanilla (§1).
`run_bepinex.sh` is patched locally to prefer x86_64 (a pristine copy is `run_bepinex.sh.orig`):

```sh
export ARCHPREFERENCE="x86_64,arm64"
```

**Any BepInEx reinstall or update reverts this patch.** After one, re-apply it (an outer
`arch -x86_64` does not help), then clear Gatekeeper quarantine on the new injector, even one
copied over the old file (else: "cannot be opened because the developer cannot be verified"):

```bash
xattr -d com.apple.quarantine libdoorstop.dylib
```

---

## Project structure, every file

Nothing here is a guess. Use this instead of searching. All 87 source files, and every other
file in the repo outside `bin/`, `obj/` and `.devtest/`.

**Build, scripts, data**

```
ValheimTomrer.csproj              netstandard2.1, local refs, publicizer, embeds the kits,
                                  auto-deploys the DLL into BepInEx/plugins after every build
Directory.Build.props             finds the install: ValheimInstall, ValheimManaged, BepInExDir
README.md                         the player-facing readme: features, install, keys, the
                                  controller tables
package.json                      npm run build (-c Debug) and build:release (-c Release)
.gitignore                        ignores bin, obj, .devtest and .claude/ except
                                  design-decisions.md (open the rest by exact path, the search
                                  tools skip them)
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
design-decisions.md               the reasons, the numbers and the full behaviour behind every
                                  rule here, one numbered section per area. Tracked in git
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
BuildUiOnLayoutChangedPatch.cs    while the editor is up, the build menu no longer clears the UI
                                  selection when the player switches keyboard and pad (it threw a
                                  typing box out of the keyboard)
EditorInputBlockPatches.cs        the whole input takeover, nine patches in one file on purpose,
                                  all gated on ModUi.Blocking
EnvManAwakePatch.cs               keeps the world's sun out of the editor's scene
FejdStartupAwakePatch.cs          main-menu smoke test, prints harmony=OK | publicizer=OK
GameCameraAwakePatch.cs           clears layer 30 from the player camera's culling mask
HudSetupPieceInfoPatch.cs         the vanilla build card shows the blueprint and its materials list,
                                  not the piece. Anything else hides the list
KeyHintsUpdateHintsPatch.cs       skips the game's hint row update while HintRow shows a set
                                  (blueprint mode, Continue, a capture)
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
MaterialTally.cs                  the numbers of a materials list (have and need per item, a row
                                  per station, the counts), from a resolved blueprint or a plain
                                  list of pieces (the editor). No preview needed
BlueprintPreview.cs               the prefab copies. Three users: the see-through hammer preview,
                                  an unfinished build's ghost (SetPart: hidden, light blue, red) and
                                  the editor's solid model. No copy is ever a crafting station
BlueprintInfoCard.cs              fills the vanilla build card: name, icon, piece count, and the
                                  materials list on the card's right in place of the six slots.
                                  Refreshes every 0.5 s and right after a click
HintRow.cs                        the controls of blueprint mode, Continue and the capture in the
                                  game's own hint row, keyboard and pad, made of copies of the
                                  game's own entries. The sets are in Hints
BlueprintMode.cs                  blueprint mode of the hammer: the key's cycle (unfinished builds
                                  first, as Continue), the turn, the click, Continue, Remove

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
                                  the way the hammer's Remove does, keeps what a refused piece
                                  needs to stand, and the message
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
                                  sprites, OverPicture for text drawn on the 3D pane, and TextBox,
                                  the text box that ignores the game UI's pad Submit/Cancel/Move
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
Ui/Dialogs.cs                     Blueprints (open, delete your own), save as, help, the
                                  questions. One at a time
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
| Write down why something works the way it does | `.claude/design-decisions.md`, the section for that area |

---

## Build

```bash
dotnet build                 # or: npm run build
dotnet build -c Release      # or: npm run build:release (no src/Dev, no autotest)
```

~1.6s. A successful build **auto-deploys** `ValheimTomrer.dll` + `.pdb` into
`BepInEx/plugins/ValheimTomrer/`. There is no separate install step, never copy by hand.

References resolve out of the **local install**, not NuGet. `VALHEIM_INSTALL` overrides the path.
`assembly_valheim` and `assembly_utils` are publicized by Krafs.Publicizer, so private and internal
members are directly accessible (`fireplace.m_fuel`, not `AccessTools`). Publicized copies land in
`obj/Debug/PublicizedAssemblies/`; the game's own DLLs are never modified.

---

## Run and verify

```bash
./scripts/dev.sh          # build, launch, stream ValheimTomrer + error lines
./scripts/dev.sh --debug  # same, plus Mono soft debugger on 127.0.0.1:10000
cd "$GAME" && ./run_bepinex.sh    # or manually
```

**Verification signal.** `src/Patches/FejdStartupAwakePatch.cs` hooks `FejdStartup.Awake`, which
runs on the **main menu**, so the whole pipeline is provable in ~13s without loading a world:

```
[Info   :ValheimTomrer] ValheimTomrer 0.1.0 loaded.
[Info   :ValheimTomrer] main menu reached | harmony=OK | publicizer=OK
```

That line confirms plugin load, Harmony patching, *and* publicization (it reads
`FejdStartup.m_instance`, a private static). Prefer main-menu hooks for smoke tests; load a world
only for features that need one.

- Steam does not need to be running: `steam_appid.txt` lets the game launch standalone.
- Stop a test instance: `pkill -f "valheim.app/Contents/MacOS/Valheim"`.
- In game, **F5** opens the console, then `devcommands` unlocks `god`, `fly`, `spawn`, `tod`. The
  console is **off by default** (since 0.221.4): enable it once in Settings > Gameplay > Enable
  console (saved), or launch with `-console` (this session only). While it is off, F5 does nothing.

---

## Debug

`./scripts/dev.sh --debug`, then in VS Code **Run and Debug -> "Attach to Valheim"**
(`.vscode/launch.json`, the `vstuc` type, §1). When it won't attach, the game was usually not
started with `--debug`. Confirm the endpoint is live before blaming the editor:
`lsof -nP -iTCP:10000 -sTCP:LISTEN` (expect `Valheim ... (LISTEN)`).

**A `--doorstop-*` flag takes a value.** Passing one bare aborts `run_bepinex.sh` silently, empty
output, no game, no log:

```bash
./run_bepinex.sh --doorstop-mono-debug-enabled true    # correct
./run_bepinex.sh --doorstop-mono-debug-enabled         # WRONG, script dies
```

- `--doorstop-mono-debug-suspend true` freezes the game at startup until a debugger attaches (for
  breakpoints in `Awake` or plugin load). `--doorstop-mono-debug-address <host:port>` moves the
  endpoint (default `127.0.0.1:10000`).
- Logs: `BepInEx/LogOutput.log` (includes the Unity log, `WriteUnityLog = true`). Vanilla Unity log:
  `~/Library/Logs/IronGate/Valheim/Player.log`.

---

## Reading game code

Valheim has no modding API or docs. Finding a patch target means reading the game.

- **Names, signatures, enum values, private or not:** read the metadata with
  `System.Reflection.Metadata` from a throwaway `dotnet run` project in the scratchpad.
- **Method bodies and call flow:** `ilspycmd` is installed but wants .NET 8, so roll forward. One
  type at a time is quick:
  `DOTNET_ROLL_FORWARD=Major ~/.dotnet/tools/ilspycmd -t ZInput "$MANAGED/assembly_utils.dll"`.
  The whole assembly: `-p -o /tmp/valheim-src "$MANAGED/assembly_valheim.dll"`, then grep the tree.
- **Names that only exist as text:** scan the DLL's strings. macOS `strings` has no `-el`, so UTF-16
  text needs python: `re.finditer(rb'(?:[\x20-\x7e]\x00){3,}', data)`.

**Game 1.0 facts** (values and data versions: §1):

- The build menu sorts pieces by `Piece.UsageTagFlags` (`[Flags]`, read by `ByUsagePieceList`).
  `Piece.PieceCategory` is legacy. `Piece.ComfortGroup` is unchanged.
- `Managed/` ships `Newtonsoft.Json` 13.0: do not bundle it.

**After a game update**, check that every patched method still exists (a missing one is a
`HarmonyException` in the log). By name: the nine input targets of `EditorInputBlockPatches.cs`
(`Player.TakeInput`, `PlayerController.TakeInput`, `TextInput.IsVisible`, `InventoryGui.Show`,
`HotkeyBar.Update`, `Menu.Update`, `Minimap.Update`, `GameCamera.UpdateMouseCapture`,
`ZInput.Internal_GetMouseScrollWheel`), `ZInput.TryGetButtonState` (the world pad's hold-back),
`KeyHints.UpdateHints` (the controls row) and `BuildUi.OnLayoutChanged` (a typing box keeps the keyboard).

---

## Custom UI

For any UI task (HUD text, messages, hover text, map pins, popups, own windows, menu buttons),
**read the research before writing code** (checked against game 1.0.15):

| File | Read it for |
|---|---|
| `.claude/research/19-09-2026-custom-ui.md` | Start here. Which approach fits which need, own-window checklist, open scope questions |
| `.claude/research/19-09-2026-custom-ui-game-api.md` | Exact game methods, patch targets and code for every UI surface; fonts and sprite names |
| `.claude/research/19-09-2026-custom-ui-platform-and-mods.md` | Input, UI systems, asset bundles on macOS, how other mods do it, links |

- Build with uGUI + TextMeshPro in code. No `UnityEngine.UI.Text`, IMGUI only for debug.
- A new TMP text shows nothing until `.font` is set. Copy it from a vanilla text.
- **Do not share the vanilla text material**: at 12 to 16 point every label on it reads grey (§2).
  Use `UiTheme.FontMaterial` for text on a panel, `UiTheme.FontOutlined` for text over the 3D
  picture (`UiBuild.OverPicture`).
- An own window needs its own `Canvas` + `CanvasScaler` (reference pixels per unit 50) +
  `GuiScaler`, and the input-blocking patches listed in the research.
- Never use the UI calls that send network messages (listed in the research, e.g.
  `Chat.SendText`, `DamageText.ShowText`, map pins with `save: true`).

---

## The editor

F7 opens a window with a 3D view, the piece list and the game's snapping. Read
`.claude/handoff/editor.md` and `editor-refine.md` before changing it. Reasons and full behaviour: §3.

- **Layer 30.** Everything the editor draws is on layer 30, 8000 m under the world
  (`EditorScene.Depth`), and every physics query it makes is masked to layer 30.
- **Input.** While the window is up, `EditorInputBlockPatches.cs` holds the game's input, every
  patch gated on `ModUi.Blocking`.
- **The mouse.** The cursor is free the whole time. Only C gives it to the pane
  (`ViewportHost.Capture()`), Esc gives it back. A click must never call `Capture()`. There are no
  camera modes, the camera always flies.
- **The hint bar.** `ViewportHost.ShowHints` runs every frame: the bar rebuilds only when its set
  changes. Its words are `UiTheme.TextOnPicture` on `UiTheme.FontMaterial`, no edge, no shadow (the
  user had a white edge taken out).
- **The panel walk** (`FocusNav`) keeps the EventSystem's selection null for buttons, or a real pad
  fires the game's input module and ours in the same frame. Palette tiles, the "In blueprint" rows
  and the problem rows are plain Images, not Selectables.
- **A new dialog** ends its builder with `Start()` (it sets `Dialogs.FocusStart`), so the walk can
  enter it. `Dialogs.Tick` stands back on Enter while the walk is in the dialog, or one press fires
  twice.
- **Text boxes** are `UiBuild.InputField` (a `TextBox`), never a plain `TMP_InputField`: the game's UI
  sends the real pad's cross, circle and D-pad to a typing box, and a plain one saved Save as on cross.
  On the pad, circle or a D-pad step leaves a typing box; Save as starts typing only when
  `EditorInput.PadInUse` is false. Reasons: §3.
- **The Esc and circle ladder**, in this order: a text box gives the keyboard back
  (`EditorSession.Tick`, read through `ModUi.JustTyping`, so an Esc the box took first this frame
  cannot also close the dialog), then the dialog, the piece menu, what is
  in hand, release the pane, leave the walk, clear the selection, close the window.
- **A row chip that carries text** (`item_background`) is tinted `UiTheme.Slot`, and a `Button` on
  it uses `Selectable.Transition.None`. Icon-only tiles keep the sprite as it is.
- **Cancelling a dialog goes through `Dialogs.Dismiss`** (Esc, circle, the X, the backdrop, Cancel),
  so a question asked from the Blueprints list goes back to it. `Close()` alone skips that.
- **An empty blueprint is a real file.** `BlueprintFormat` and `DocumentStore` read and write one,
  `BlueprintLibrary.TryAdd` keeps it out of `All`, the problem list calls it a warning.
- **Closing keeps everything** (the document and its undo, the selection, the hand, the camera, the
  tabs and filters, a dialog, the walk); the scene sleeps (`ViewportHost.Sleep` / `Wake`). Only the
  mouse is not kept. `EditorSession.Forget()` is the full wipe.
- **The hand wins.** F7 with another blueprint in the build tool opens that one through
  `EditorCommands.Take`, which asks first over unsaved changes. The world capture opens the same
  way. The same file in hand comes back to the kept session.
- **A prefab copy is made with no parent**, then moved under its parent (`BlueprintPreview.Build`).
  Under a switched-off parent it becomes a real world piece 8000 m down, and the ghost vanishes two
  frames later.
- **Support** (`Placement/Support.cs`): the material numbers come from the game's own
  `GetMaterialProperties`, never typed in. The model may refuse what the game would allow, never the
  other way round. `Evaluate` takes the ground callback from the map, so always pass it a real map.
- **The materials list** in the editor: `MaterialSources.Around` at most once a second while the
  window is open, never while it is closed.

---

## The pad

Every key the mod reads with the window closed has a pad twin, read in `Input/WorldPad.cs` through
the game's own button names, so the game's layout decides. "L2" is the game's modifier `JoyAltKeys`
(L1 in the alternative layout). The player's tables are in `README.md`. Reasons and details: §4.

| Pad | Does | Key |
|---|---|---|
| square, hammer out | next blueprint (`BlueprintMode.Cycle`) | B |
| R2 / L2 + right stick, in blueprint mode | build / turn | click / wheel |
| R1, in Continue | Remove (the game's `JoyRemove`) | Remove |
| L2 + square | open the editor, the blueprint in hand rule included | F7 |
| L2 + square, in the editor | close it (`PadBindings.ClosePressed`); square alone still moves | F7 |
| L2 + triangle | start a capture, again: take it | F8 |
| D-pad, L2 + D-pad, circle | turn, both sides, width, depth; stop the capture | wheel, Esc |

- World combos read the game's buttons (`ZInput.instance.GetButtonDef`), never the pad itself. The
  editor reads the pad itself (`PadReader`) and maps the game's modifier with
  `WorldPad.ModifierButton`, so the same two buttons open and close it.
- They work only while `WorldPad.Live`: mod on, window closed, `EditorSession.CanOpen`, no game menu.
- A combo's buttons are held back from the game in `ZInputTryGetButtonStatePatch`, grouped by
  binding path (not by name), and stay held until let go (`WorldPad.Tick`).
- **A press that closes something must not reach the game** (circle is the jump, read in the next
  FixedUpdate): the capture holds it until let go, the remove window resets it
  (`SiteRemovePopup.SwallowPad`, through `ZInput.ResetButtonStatus`).
- **The editor's look is the game's** (`EditorCamera.TurnPad`): 110 degrees a second times
  `PlayerController.m_gamepadSens`, the game's invert settings, no setting of our own. `PadReader`
  reads the sticks with `ReadUnprocessedValue`, never `ReadValue()`: the game sets Unity's stick
  filter to 0.4 to 0.75, which made the look clunky (§4).
- **In the editor the pad works off the crosshair.** Square moves, triangle copies, R1 deletes, each
  on its own, all through `PadBindings.TakeAimed`. Keep the three the same.

---

## The capture

F8 (`Editor/CaptureKey`) or L2 + triangle, with the window closed, in `WorldCapture.cs`. The rules
for what it takes, its sizes and its glow: §5.

- It keeps `ModUi.Blocking` false, so the game keeps its input. The wheel patch reads 0 while
  `WorldCapture.Active`, and `Menu.Update` skips the frame the capture takes Esc
  (`WorldCapture.TakesEscape`), or Esc also opens the pause menu.
- Every way out calls `CaptureTint.Clear`: capture, Esc, mod off, window open, world change. A house
  that stays yellow or orange means one path missed it.
- Save as opens only once the captured blueprint is the open one: `OpenDocument(doc, opened)` and
  `EditorCommands.Take(..., taken)` run it after the "Discard?" question, never instead of it.
- The status line (`CaptureHud`) has no controls, and no message comes up when a capture starts.

---

## Hammer builds and unfinished builds

Built by `.claude/plans/21-09-2026-capture-and-partial-build-done.md`. Read its log,
`.claude/handoff/build-sites.md`, before changing anything here. Phase 8 (ItemDrawers) was skipped:
there is no drawer source and no `Build/UseDrawers` setting. Reasons and full behaviour: §6, §7.

- **A chest counts** (`MaterialSources.Around`) only when a player placed it, the player may open
  it, the ward lets them in, and **this client owns its ZDO** (else what was taken comes back after
  a reload). Find holds with `GetComponentInChildren<Container>`: a cart's or karve's hold has no
  `Piece`.
- **`PartialBuild.Plan` reads the world and changes nothing.** Each chosen piece is paid just before
  it goes down; one that can no longer be paid is skipped, never placed free.
- **No copy is a crafting station.** `BlueprintPreview.StripToVisuals` removes every
  `CraftingStation` from a copy (else a piece that needs a workbench builds with no real bench
  near). On a station that may be a ghost read `m_buildRange` / `m_rangeBuild`, never
  `GetStationBuildRange()`: it throws a `NullReferenceException` in `CraftingStation.GetExtensions`.
- **A site's file** is the blueprint as it was at the click, plus `#Site:` and `#SiteSource:`
  headers, read in `SiteStore` only, never in `BlueprintFormat`. What is built is never written, it is
  read from the world. Tests point `SiteStore.RootOverride` at `.devtest/sites`.
- **A site's ghost** is filled with each renderer's `forceRenderingOff` and the root unturned, then
  moved (`MeasureBounds` skips inactive renderers: filled switched off, the ghost gets a zero or wrong
  size). Its look is `SetPart`: built hidden, ready light blue (`ReadyTint`, without it a ready part
  looks like built wood), the rest red (`RedTint`), through `MaterialMan`. Only
  `PreviewStyle.Site()` is tinted, and a click never tints it.
- **In blueprint mode (Continue too) the game's `UpdatePlacement` does not run**, so Remove never
  takes down the aimed piece there.
- **The remove window** (`SiteRemovePopup`) is a `UnifiedPopup` of its own `PopupType` (100). It
  blocks through the game's `Menu.IsVisible()`, so it needs no input patch. Whenever it is not the
  popup showing, the panel's width and default button go back and its copies hide.
- **Whole structure** (`SiteRemoval.TakeDown`) gives each piece `Player.RemovePiece`'s own checks and
  calls, top to bottom (the reverse of `PartialBuild.BuildOrder`), and leaves a refused piece and
  every piece it needs to stand.

---

## The build card and the controls row

Layout, numbers and the full sets: §8, §9.

- **The card** (`BlueprintInfoCard`). The game sets its six slots every frame, so the postfix only
  hides the list and there is nothing to undo. The list never grows up over the card (the game puts
  the stamina and eitr bars there in build mode). No "From:" line and no controls on the card: the
  user took both out.
- **A plan over 5 ms** waits until the preview holds still for one refresh, so moving a big blueprint
  never stutters.
- **The controls row** (`HintRow`). Every entry is a copy of the game's own (`Place`, `key_bkg`,
  `+`, `mousew_icon`, `Text - Place`), never a look-alike. Pad icons come from
  `Localization.GetBoundKeyString`, keys from the game's names and `ZInput.KeyCodeToDisplayName`, so
  both follow the player's layout. When the mode ends the game's own update runs again: nothing to
  undo.
- A control of blueprint mode, Continue or the capture is listed in `HintRow` (keyboard and pad),
  never on the card or the capture's status line.

---

## Autotest

`./scripts/autotest.sh <scenario>`, Debug builds only. What each scenario checks, in full: §10.

| Scenario | What it proves |
|---|---|
| `probe`, `dump` | measures layers, UI, input and pieces into `.devtest/*.txt`. No feature |
| `probe_build` | measures chests, support speed, ground, glow and the card. Not in `editor_all` |
| `build_sources` | materials from the bag and chests: order, exact amounts, range, access |
| `build_partial` | a click builds what is paid for and would stand, bottom to top |
| `build_sites` | unfinished builds: the file, the ghosts and their colours, finishing one |
| `build_continue` | Continue, Remove and the remove window, keyboard and pad |
| `card_materials` | the hammer card's materials list |
| `hint_row` | the controls in the game's hint row, keyboard and pad |
| `blueprints` | every shipped kit built with the hammer, B and square cycle the same |
| `editor_open` | F7 and L2 + square open and close the window |
| `editor_view` | the 3D pane and its camera, nothing leaks on close |
| `editor_files` | the writer round-trips every blueprint, the file commands |
| `editor_palette` | the catalog, the palette counts, the filters |
| `editor_snap` | placing and snapping against a table of rays, no UI |
| `editor_edit` | place, select, copy, turn, nudge, undo |
| `editor_panels` | the card, the selection fields, the problem list, the materials list |
| `editor_keys` | every key, the wheel, the mouse, the top bar, the dialogs, deleting a blueprint |
| `editor_pad` | every pad button through a made-up pad, the piece menu, the look against the game's |
| `editor_focus` | the panel walk with the pad and Tab, text boxes on the pad (a real pad's cross and circle too), deleting a blueprint with the pad |
| `editor_keep` | close and open again finds everything as it was |
| `editor_build` | a blueprint made in the editor, built in the world, edited again |
| `editor_capture` | the capture, keyboard and pad |
| `editor_support` | the support rule against the game's numbers. `VT_SUPPORT_EDITOR_ONLY=1` skips the world half |
| `editor_all` | all of the above except `probe_build`, then the art guard. **This is the one to run.** |

- **The release check** is `editor_all`: `DONE pass=N fail=0` plus `art guard:` in the output. It
  takes about 11 minutes. `VT_CHAIN="editor_build,blueprints" ./scripts/autotest.sh editor_all` runs
  only those two, in that order.
- Every run of `autotest.sh` deletes `.devtest/*.png` first. To look at one scenario's screenshots,
  run it alone or last.
- Every world-building scenario builds at `AutoTest.MoveToBuildSpot` (levelled flat, §10), and
  `AutoTestPeace` quiets the world at the start of every scenario.
- `AutoTest.Reset` runs between scenarios: the editor forgotten, every unfinished build and
  `.devtest/sites` gone, every setting back to its default (`AutoTest.ResetSettings`), so no run
  changes the player's config file.
- **Not covered:** a real pad pressing cross while the panel walk is on (the fake pad is invisible to
  `InputSystemUIInputModule`). Check it by hand with a pad plugged in.

---

## Tomrer, the desktop editor (separate repo)

The in-game editor does everything Tomrer does, so `../Tomrer` may be removed. Do not start work
there, and do not change anything in it (§11).

- **Keep `src/Blueprints/Blueprint.cs` and `BlueprintFormat.cs` free of other game code**, whether
  Tomrer stays or goes: its check compiles them on their own against `UnityEngine.CoreModule`.
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

Symptoms whose cause is not already a rule above. The rest are named next to their rule.

| Symptom | Cause |
|---|---|
| No `LogOutput.log` at all, game runs fine | arm64, `ARCHPREFERENCE` reverted (see Apple Silicon) |
| `0 plugins to load` | DLL not deployed; re-run `dotnet build` |
| Plugin loads, patch never fires | wrong method name/overload, grep log for `HarmonyException` |
| Compile error reaching a private member | assembly missing from `<Publicize>` in the csproj |
| `DllNotFoundException: AppleCoreNativeMac` | **benign**, vanilla Apple GameKit probe failing under Rosetta, unrelated to mods |
| Editor window opens but the 3D pane is black | the pane camera's culling mask lost layer 30, or its `RenderTexture` was released (`PreviewCamera.Resize`) |
| The 3D pane looks frozen with a pad in hand | focus is in a panel, press circle to go back to the pane |
| A piece shows as a plain box in the pane | the prefab has no `MeshFilter`, or it draws through `InstanceRenderer`, which a mesh-only clone cannot copy |
| Blueprint mode never starts in a test run, B does nothing | the test character's hammer broke (the character is saved on quit, run after run). `AutoTest.EquipHammer` repairs it |
| B says "No blueprints" right after a teleport | the player landed in water: swimming puts the hammer away. Pick dry land and equip again |
| Remove in Continue does nothing | the game reads Remove on release (`GetButtonUp`). On this Mac it is Left Command, not the middle mouse |
| The remove window's circle icon reads `MISSING BUTTON DEF "ButtonB"` down the screen | a copied button's hint kept the prefab's text. `SiteRemovePopup.ShowHints` sets it from `$KEY_JoyButtonB`; the game re-translates only its own texts |
| A test's mouse click on a button does nothing, the button stays pressed | `MouseState.WithButton` changes the struct it is called on, so the "release" state still had the button down. Build the press state on its own (`AutoTest.ClickScreen`) |
| The art guard fails with "the walk saw 0 unfinished-build files" | the guard's folder list (`AutoTest.WalkWrittenFiles`) lost the sites folder, or no scenario in the chain kept a site until `ClearSites` |
| The pad's L2 + triangle opens the inventory, or the D-pad moves the hotbar during a capture | `ZInputTryGetButtonStatePatch` did not apply (`ZInput.TryGetButtonState` renamed or inlined), or `WorldPad.Live` is false (a game menu is up) |
| In blueprint mode or a capture the bottom row still shows the game's snapping and copy hints | `KeyHintsUpdateHintsPatch` did not apply (`KeyHints.UpdateHints` renamed), or the log says "hint row: the game's build hints do not look as expected" (the game renamed its `Place`, `key_bkg`, `Text - Place` entries or the wheel sprite) |
| A test says the game's pad entry reads `MISSING BUTTON DEF "Place"` | normal while the keyboard is in use: the game localizes its hidden pad row with keyboard names. Compare pad icons only while `ZInput.IsGamepadActive()` |
| Save as saves its first name and closes on the pad's cross | a text box built as a plain `TMP_InputField` instead of `UiBuild.InputField` |
| A name box stops typing on the first pad or mouse press after the other device | `BuildUiOnLayoutChangedPatch` did not apply (`BuildUi.OnLayoutChanged` renamed) |
| A pad test presses a button and nothing happens | the button went to a real pad: test presses go to `AutoTest`'s own "AutoTestPad DualSense" device, never `InputSystem.FindControl`, which can pick the real DualSense |
