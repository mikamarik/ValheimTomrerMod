# ValheimTomrer: design decisions and rationale

Why the mod works the way it does, the full behaviour behind each rule in `CLAUDE.md`, the
measured numbers, and what was tried and dropped.

`CLAUDE.md` has the rules: what to do, what never to break. It is loaded on every turn. This file
has the reasons and the detail, and it is not. Read the section for an area before you change how
that feature behaves or undo a decision recorded here. Skip it for ordinary work.

**New reasons go here.** When a session settles how something works, the reason goes in the right
section below and the rule, in one line, goes to `CLAUDE.md`. See "Maintaining this file" there.

Other records:

| File | Holds |
|---|---|
| `.claude/handoff/editor.md`, `editor-refine.md` | the editor's build log, phase by phase (`editor-refine.md` is newer and wins) |
| `.claude/handoff/build-sites.md` | the log of the capture, chests, part builds and unfinished builds, with every measured number |
| `.claude/plans/*-done.md` | the plans that were run |
| `.claude/research/` | the UI research: game methods, patch targets, fonts, sprites |

Sections:

1. The machine and the tools
2. Custom UI
3. The editor
4. The pad
5. The capture
6. Hammer builds
7. Unfinished builds (sites)
8. The materials list on the hammer card
9. The controls row
10. The autotest
11. Tomrer

---

## 1. The machine and the tools

### Apple Silicon

Valheim 1.0 ships a **universal binary**. On native arm64, BepInEx 5's MonoMod detours die in
`DetourHelper.GetIdentifiable()` inside `HarmonyInteropFix.Apply()`, and **no plugins load and no
`LogOutput.log` is written at all**. It looks exactly like a normal vanilla launch. (BepInEx issue
#1303; fix PR #1402 still unmerged.)

`run_bepinex.sh` ships with `ARCHPREFERENCE="arm64,x86_64"`, which *causes* this. That is why it is
patched to `"x86_64,arm64"`. An outer `arch -x86_64` wrapper does **not** help: the script overrides
it internally.

The quarantine is cleared after every reinstall, even when the new injector was copied over the old
one: macOS keeps xattrs on a file you `cp` over, so a stale quarantine flag can survive a
replacement and silently block injection.

### References come from the install

References resolve out of the local install, not NuGet, so compile-time and runtime versions can
never drift.

### The debugger

UnityDoorstop can open a **Mono soft debugger**, giving real breakpoints, stepping and locals inside
both our code and Valheim's. Verified working on this machine.

The attach uses the **`vstuc`** debugger type from the already-installed *Visual Studio Tools for
Unity* extension. Its `endPoint` property accepts an arbitrary address, which is what makes it work
against a doorstop-hosted game rather than a Unity Editor instance. The `ms-vscode.mono-debug`
extension is **not** installed and is not required.

Portable PDBs are enabled in the csproj and deployed next to the DLL, so symbols resolve.

`--doorstop-mono-debug-suspend true` exists for breakpoints in `Awake` and plugin load, which
otherwise run too early to catch.

### Reading the game

- The metadata reader (`System.Reflection.Metadata` in a throwaway `dotnet run` project) is how
  `Piece.UsageTagFlags` and the `Version` constants below were obtained.
- `ilspycmd` was installed with `dotnet tool install -g ilspycmd`. It wants .NET 8 and only 9 is
  here, hence `DOTNET_ROLL_FORWARD=Major`. That is how `ZInput`'s wheel scale was read.
- A fresh install of it once failed with "Settings file 'DotnetToolSettings.xml' was not found in
  the package". The metadata reader and the scan for UTF-16 strings in the DLL are the two ways
  round it, and both were used to find the controller glyph names.

### Game 1.0 facts

**The build menu was overhauled.** `Piece.PieceCategory` is legacy. Pieces are now sorted by
`Piece.UsageTagFlags`, a `[Flags]` enum consumed by `ByUsagePieceList`:

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

## 2. Custom UI

The research in `.claude/research/` was checked against game 1.0.15.

### Why the text read grey

TMP multiplies the label's colour by the material's face colour and then draws the material's
outline and shadow over the glyph. The HUD's hover-name material is made for big white names over a
dark world, so at 12 to 16 point every label reads grey whatever colour it is given. Four rounds of
"the text is grey" were this, not the colour constant.

`UiTheme` keeps two copies of it instead, both white-faced: `FontMaterial` plain for text on a
panel, `FontOutlined` with a black edge for text over the 3D picture (`UiBuild.OverPicture`). A copy
of a loaded material is fine, nothing is on disk.

---

## 3. The editor

F7 opens a window with a 3D view, the piece list and the game's snapping. It is the mod's biggest
feature, built in Phases 0 to 12. `.claude/handoff/editor.md` has the full record,
`editor-refine.md` the second pass.

### Layer 30

Everything the editor draws lives on layer 30, 8000 m under the world (`EditorScene.Depth`).
`GameCameraAwakePatch` clears bit 30 on the player's camera, the pane's camera draws only bit 30,
and every physics query the editor makes is masked to it. The two views can never bleed into each
other.

### Who has the mouse

The cursor is free the whole time, so every panel and the pane itself are clickable. On the pane a
left click selects or places, right drag looks around, middle drag pans and the wheel zooms, all
with the cursor where the player left it. **C** hands the mouse to the pane for game-style looking
(`ViewportHost.Captured` / `Capture()` / `Release()`), Esc gives it back.

A click must never call `Capture()`: it used to, and clicking a piece then hid the cursor and swung
the view instead of selecting. There are no camera modes, the camera always flies.

The look while the pane has the mouse is the game's own (`PlayerController.LateUpdate`): 0.05
degrees a pixel (ZInput's mouse delta scale) times the game's Mouse sensitivity
(`PlayerController.m_mouseSens`, `m_switchMouseSens` when the game says a pad's mouse is in use),
with its invert mouse setting. It was 0.14 degrees a pixel times a `LookSensitivity` of our own,
removed like `PadLookSensitivity` (§4). The 300 px clip on one frame's move stays: it stops the jump
when the cursor is taken, which the game handles by skipping frames instead. Right drag is not a
game look and keeps its own speed (a drag over the pane's height is 0.6 of a turn).

### The hint bar

`Ui/HintBar.cs` draws the controls along the bottom of the pane as the game does: controller icons
from the game's own `gamepad_glyphs` TMP sprite asset (`Ui/PadGlyphs.cs`), and keyboard keys as caps
cut from the same wooden button sprite as the rest of the window. Four sets: free mouse, mouse held,
pad flying, pad walking the panels. `ViewportHost.ShowHints` picks one by a key and the bar rebuilds
only when that key changes, because it is called every frame.

Its words are dark and bold (`UiTheme.TextOnPicture`) on the plain material
(`UiTheme.FontMaterial`), with no edge and no shadow. A white edge was tried and taken out at the
user's request. Only the key caps stay white, they sit on their own wooden background.

### The panel walk

`Ui/FocusNav.cs` walks three regions (top bar, left panel, right panel). Tab or L3 enters,
Shift+Tab and L1/R1 change region, Enter and cross press, Esc and circle leave.

- The right stick scrolls the region (`FocusNav.Scroll`): the list round the focused widget when it
  has more than it shows, else the tallest one in the region. The ring hides while its widget is
  scrolled out of sight, and the next step scrolls it back (`ShowInList`).
- Tab steps in hierarchy order (`Move`). The arrows, the D-pad and the left stick step by screen
  position (`Step`): up and down go to the nearest row on that side, lined up by the left edge; left
  and right stay on the row.
- At a region's edge the step goes on into the region on that side (the top bar sits over both
  panels, the panels face each other across the pane), never out of a dialog, and nothing wraps. The
  step straight back returns to the widget it came from.
- A widget scrolled out of its list is only reached from inside that list.
- The top bar's buttons are right-aligned, so on a normal screen down from any of them lands in the
  right panel, the closer one.
- It keeps the EventSystem's selection null for buttons, or a real pad fires the game's input module
  and ours in the same frame.
- Palette tiles, the "In blueprint" rows and the problem rows are plain Images, not Selectables, so
  the walk cannot reach them and they stay mouse only.

### A dialog is the fourth region

Blueprints (the list), Save as, the help and every question take the walk on their own, so a controller can work
them: `Dialogs` names the widget to start on (`Dialogs.FocusStart`, set by `Start()` at the end of
each builder), `FocusNav.EnterDialog` goes in and `LeaveDialog` puts the walk back where it was. L1
and R1 cannot walk out of a dialog, and `FocusNav.ShowInList` scrolls a row into view so a long file
list can be walked. While one is up `Bindings.DialogKey` is the whole keyboard map (Tab, the arrows,
Enter) and `PadBindings` step 2 is the whole pad. `Dialogs.Tick` stands back on Enter while the walk
is in the dialog, or one press would fire twice.

### Text boxes and the pad

A text box that is typing is the one widget the EventSystem really selects, so the game's own UI
module (`InputSystemUIInputModule`, which reads the real pad) sends it events: the pad's cross as
Submit (`*/{Submit}` is Enter and cross), circle as Cancel, the D-pad as Move. A plain
`TMP_InputField` fires `onSubmit` on Submit: Save as saved its first name ("New blueprint") and closed
on one cross. On Cancel it stopped typing and put the old text back before the editor's tick saw the
press, and the editor then took the same circle as "close the dialog". So every box is a `TextBox`
(`UiBuild.cs`) that ignores all three; keys typed into it still work (Enter submits, Esc puts the text
back), because TMP reads those through its own key events, not these.

Two more things threw a typing box out of the keyboard:

- The game fires `ZInput.OnInputLayoutChanged` on every switch between keyboard/mouse and pad (and
  when a pad is plugged in). The build menu, alive behind the editor, answers with
  `SetSelectedGameObject(null)`. `BuildUiOnLayoutChangedPatch` skips that while the window is up.
- The box reads its keys in the game's UI update, which may come before the editor's tick in the
  same frame. `ModUi.JustTyping` (typing now or last frame) makes that key the box's: an Esc the box
  took cannot also close the dialog, and the Enter that submitted a taken name (which opens "Replace
  the file?") cannot also press Replace. Before, it did, and the file was written over unasked.

On the pad: circle leaves a typing box and keeps its text; a D-pad or stick step leaves it and steps
on (the pad has no keys to type with, so a box must never hold the walk). Save as starts typing only
when the keyboard or mouse was used last (`EditorInput.PadInUse`, seeded from the game's
`ZInput.IsGamepadActive()` when the window opens); from the pad the ring waits on the box, so down
and cross save under the name shown. The autotest proves the first two with a real test pad device
the game's UI reads (`FocusSaveAsTyping`): with a plain box the same presses saved the file.

### Deleting a blueprint

Each of the player's rows in Blueprints has a Delete button on its own line to the right, not
inside the row: the walk steps by screen position, and a button inside the row's box is never "to
the right" of it. Kits have none (they live in the DLL). The question (`Dialogs.ClickDelete`) is a
`Confirm` with `risky`, so the walk starts on Cancel and a second cross does no harm, the way the
game's own "are you sure" dialogs start on No. Every way of cancelling it (Esc, circle, the X, a
click beside it, Cancel) goes through `Dialogs.Dismiss`, which runs its `back`: the list again, on
the same row's Delete button. After a delete the walk lands on the row that took its place, or the
one above. A blueprint open in the editor from the deleted file stays open as not saved
(`BlueprintDocument.LoseFile`), so nothing is lost without a second question. Unfinished builds keep
their own copy of the blueprint, so a delete does not touch them.

### Row chips are tinted dark

`item_background` is a pale sprite, and every label in the window is white, so a row that carries
text has to be tinted `UiTheme.Slot` or the text is white on white. That is what the Blueprints list,
the "In blueprint" list and the problem list do. Icon-only tiles (the palette, the piece menu, the
materials list's icons) keep the sprite as it is. A `Button` on such a row also needs
`Selectable.Transition.None`, or its colour transition tints the chip a second time.

### An empty blueprint is a real file

New, then Save as, before a single piece is placed, is the first thing the editor does, so
`BlueprintFormat` writes and reads a blueprint with no pieces and `DocumentStore` saves one.
`BlueprintLibrary.TryAdd` keeps empty ones out of `All`, because the build tool has nothing to build
from one and `ResolvedBlueprint` has no piece to take an icon from. The problem list calls it a
warning, not an error.

### Closing keeps everything

F7 or Esc hides the window and nothing else: the blueprint with its unsaved changes and undo, the
selection, what is in hand, the camera, the left tab, the palette search and filters, the piece
menu, a dialog that is up and the panel walk all wait for the next open. The 3D scene stays too,
switched off (`ViewportHost.Sleep`/`Wake`), so the next open is instant; only its texture is let go.
A world change kills the scene, and `Wake` builds it again from the document with the camera where
it was (`EditorCamera.TakePose`). The mouse is the one thing not kept: the window always opens with
the cursor free. `EditorSession.Forget()` is the old full wipe, and the autotest's reset between
scenarios calls it.

### The hand wins

The hand wins over what was kept, the way it always did: F7 with a blueprint in the build tool that
is not the one left open opens that one, through `EditorCommands.Take`, which asks first when the
kept one has unsaved changes. The world capture opens the same way. The same file (or the same kit)
in hand just comes back to the kept session.

### A prefab copy is made with no parent

`BlueprintPreview.Build` instantiates with no parent, then moves the copy under its parent. Under a
switched-off parent (the ghost is built hidden) the copy's `ZNetView` woke up later, after
`m_forceDisableInit` was off, and every piece put in hand became a real piece saved in the world
8000 m under the player. The ghost also lost its model two frames later. `editor_support` counts
world objects under -1000 m before and after and fails on any new one.

### Support colours

`Placement/Support.cs` ports `WearNTear.UpdateSupport` the way `Placer` ports the placing rule:
plain maths, a mesh collider is its box, the ground is y = 0. The standing pieces are solved once
per index (`EditorState.Stability`), the pieces in hand are measured against that each time the spot
changes (`EditorState.Weigh`, into `PlaceResult.Support/Falls/WouldFall`). The ghost wears
`Support.ColorOf`, the game's own `WearNTear.Highlight` formula; `CommitPlacement` refuses a drop
that would fall; `Checks` lists pieces that would fall; the piece under the aim takes the tint
through a property block, as the game's `MaterialMan` does. The material numbers come from the
game's own `GetMaterialProperties`, never typed in.

A build in the world passes a ground callback to `Support.Solve` (`PartialBuild.GroundUnder`: the
terrain height under a point, in the blueprint's space). A collider touches the ground when its
lowest point is at or under the terrain right there. Null keeps the editor's y = 0. `Evaluate` takes
the callback from the map, so always pass it a real map.

What the numbers are: the steady state. Built one piece at a time, the game lands on exactly these
(pinned to 0.01 in `editor_support`). Built all at once, as a blueprint is, the game can keep a
piece *stronger* for good, because `UpdateSupport` keeps its old value while the pieces under it do
not change. Never weaker. So the editor errs on the careful side: it may refuse a piece a blueprint
would get away with (the ninth 2 m pole on a stack), never the other way round.

### The materials list in the editor

The blueprint panel's card copy shows the same `MaterialList` under the name and text, in place of
the six squares it had. The text is the hammer card's own (`BlueprintInfoCard.Description`): the
description, then "16 pieces.", no controls. There is no 6-slot limit and no "card shows only 6"
problem any more.

- **Numbers:** `BlueprintCard.Materials(document, sources, costsOff, at)`, the document's hammer
  pieces (others are left out, the problem list already flags them) through `MaterialTally.For` on a
  plain list of pieces. Have = the bag and the chests in range of the player where they stand in the
  world; a station is "in range" of the player, "in blueprint" when the document holds one.
- **No footer** (`MaterialList.FooterShown = false`): the blueprint stands nowhere, so "Can build
  now" has no spot, and the card text already counts the pieces.
- One column (two only from 516 wide, the hammer card's width).
- Worked out when the document changes and once a second while the window is open;
  `MaterialSources.Around` at most once a second (its counts are live), never while closed.
- The blueprint region grows with the list (`EditorWindow.FitBlueprint`): never under its old 368,
  never so far that the problem list keeps less than 160; past that it scrolls with the wheel, or the
  right stick with the panel walk in the right panel. The selection region moves down with it.
- The rows are plain images, so the panel walk never enters the list.

---

## 4. The pad

The user plays on a pad, so every feature works on one (the rule is in `CLAUDE.md`, Scope).

### In the world: the game's button names

Every key the mod reads with the window closed has a pad twin, read in `Input/WorldPad.cs` through
the game's own button names (ZInput), so the game's layout decides: "L2" is the game's modifier
`JoyAltKeys` (L2 in the default layout, L1 in the alternative one). The table of combos is in
`CLAUDE.md` ("The pad"), the player's tables in `README.md`.

- The mod reads the game's button objects directly (`ZInput.instance.GetButtonDef`), so the D-pad's
  own repeat applies. The editor reads the pad itself (`PadReader`), so its close
  (`PadBindings.ClosePressed`) maps the game's modifier to a `PadButton`
  (`WorldPad.ModifierButton`): the same two buttons open and close. Square alone still moves.
- **Held back from the game** (`ZInputTryGetButtonStatePatch`, one private method every `GetButton`,
  `GetButtonDown` and `GetButtonUp` goes through, Update and FixedUpdate alike): every game button
  bound to the D-pad or circle while a capture is up (hotbar, forsaken power, camera and minimap
  zoom, jump, `JoySit` in the other layouts), and every one bound to square or triangle while L2 is
  held (the inventory's `JoyButtonY`). Grouped by binding path, not by name. A button stays held
  back until it is let go (`WorldPad.Tick`), so the jump read in the next FixedUpdate never sees the
  circle that stopped the capture.
- Only where the combos work (`WorldPad.Live`): mod on, window closed, `EditorSession.CanOpen`, and
  no game menu (inventory, build menu, map, radial, trader). There the game has every button as
  usual.
- Hints: in blueprint mode, Continue and a capture the game's own hint row along the bottom lists
  them (`HintRow`, §9): the game's key caps, and on a pad the game's own icons
  (`Localization.GetBoundKeyString`), so they follow the game's layout and the pad in hand. The card
  and the capture's status line carry no controls.

### The pad works off the crosshair

Square moves, triangle copies and R1 deletes, each on its own with no second button and nothing
selected first: `PadBindings.TakeAimed` selects what the middle of the view is on, keeps the whole
selection when that piece is part of it, and falls back to the existing selection when the crosshair
is on nothing. They used to disagree (square and triangle needed R2 first, delete did not, copy was
`L2 + R1`) and no one could tell what they did. That is why the three stay the same.

### The editor's look is the game's

The right stick in the 3D pane turns exactly like the game's camera (`PlayerController.LateUpdate`):
110 degrees a second at full stick, times the game's own Gamepad sensitivity
(`PlayerController.m_gamepadSens`), with its invert X and Y settings. The game's settings screen
writes those statics live, so a change there shows in the editor at once. The mod has no pad look
setting any more (`PadLookSensitivity` is gone, and `EditorConfig.Drop` takes its line, the mouse's
`LookSensitivity` (§3) and the old `CameraMode` one out of the player's file: BepInEx keeps an
unknown line forever).

- **The sticks are read raw** (`ReadUnprocessedValue`), then ZInput's radial dead zone (0.2, rescaled
  to 0..1), the way `ZInput.ReadValueDef` does. `ReadValue()` adds Unity's own stick filter, which the
  game sets to 0.4 to 0.75 (measured). With the old reader the stick did nothing up to about half
  way and was at full speed by three quarters (raw 0.5 read 0.11, raw 0.7 read 0.82, the game 0.375
  and 0.625): the "clunky" look the user reported. Frame steps were already even.
- The pane's field of view is 45 degrees, the game's camera 65, so the same degrees a second cross
  the pane about 1.4 times faster than the game's screen.
- `editor_pad` (`AutoTest.PadLook`) holds this through the made-up DualSense, the real read path:
  both sticks against `ZInput.GetJoyRightStick` / `GetJoyLeftStick` at nine positions, the speed,
  the same step every frame, twice the sensitivity and invert Y.

### What the autotest cannot press

A real pad pressing cross while the walk is on. The fake pad is invisible to
`InputSystemUIInputModule`, so the test can only prove the rule that prevents the double-fire
(nothing of ours selected, 0 violations).

---

## 5. The capture

It runs with the window closed and works like Homestead's area save. Its key is
`Editor/CaptureKey`, default **F8** (free in vanilla, and next to F7).

| Input | Pad | What it does |
|---|---|---|
| F8 | L2 + triangle | a rectangle (8 x 8 m at first) on the ground under the aim; it follows the aim |
| Wheel | D-pad left, right | turns it 22.5 degrees |
| Shift / Alt / Shift + Alt + wheel | L2 + D-pad left, right / L2 + D-pad up, down / D-pad up, down | width / depth / both sides, 2 m a step, 2 to 64 m |
| F8 again | L2 + triangle | captures: the editor opens on it with Save as up, "Captured build" in the box |
| Esc | circle | stops it. Every glow comes off |

- **Taken:** `IsPlacedByPlayer()`, pivot inside the turned rectangle, pivot from 8 m under to 64 m
  over the ground at the centre, and a piece the hammer has. The rest inside is skipped and counted.
- The blueprint is in the rectangle's own frame, so a house built at 45 degrees comes back square.
  The origin is `EditorState.BottomCentre`, the same rule as the Center origin button.
- **The glow.** `CaptureTint` glows the pieces yellow, and orange the ones whose colliders cross the
  edge with the pivot outside, through the game's `MaterialMan` (the calls `WearNTear.Highlight`
  makes). A refresh (0.3 s moving, 1 s still) touches only what changed, plus a piece aimed at in the
  last 2 s: the game's own hover highlight resets that one. At most 1600 glow. It is cleared on
  capture, Esc, mod off, window open and world change.
- Save as opens only once the captured blueprint is the open one: `OpenDocument(doc, opened)` and
  `EditorCommands.Take(..., taken)` run it after the "Discard?" question, never instead of it.
- **Input.** `ModUi.Blocking` stays false, so the game keeps its input. Two input patches also look
  at the capture: the wheel reads 0 while `WorldCapture.Active` (the camera does not zoom, the
  capture reads `Mouse.current.scroll` itself with the game's scale), and `Menu.Update` skips the one
  frame the capture uses Esc (`WorldCapture.TakesEscape`), or Esc would also pause the game. The
  pad's D-pad and circle are held back from the game while it is up (§4).
- **The status line** (`CaptureHud`) says the size, the turn and the counts, nothing else. The
  controls are in the game's hint row along the bottom (`HintRow`, capture set), and no "press the
  key again" message comes up when a capture starts.
- The status line sits at (28, -170) HUD units, under the game's own top-left message line (124 to
  154 down, "Built ..."), not on it.

---

## 6. Hammer builds

Built by `.claude/plans/21-09-2026-capture-and-partial-build-done.md`. Its log, with every measured
number and gotcha, is `.claude/handoff/build-sites.md`. Phase 8 (ItemDrawers) was skipped at the
user's request.

### The world-save check

The grep in `CLAUDE.md` names its folders. With plain `src/`, macOS grep prints `src//Dev/...`, and a
`grep -v "src/Dev/"` after it lets the autotest's own two lines through (`editor_support` marks its
test pieces as the player's).

### Where the materials come from

`MaterialSources.Around(player)`, made once per click or refresh: the bag, then every chest within
`Build/ChestRange` (20 m, 0 to 100, measured in 3D from the player to the chest's pivot), nearest
first. `Build/UseChests` off means the bag only. Chests are found with `Piece.GetAllPiecesInRadius`,
then `GetComponentInChildren<Container>`: a cart's and a karve's hold is a child object with no
`Piece`. A chest counts when all of these hold:

- a player placed it;
- the player may open it (`Container.CheckAccess`; a hold with no `Piece` runs the same rule on the
  piece that was found);
- the ward lets the player in, when the chest checks wards;
- this client owns its ZDO. `Container` saves only on the owner, so a take from a chest someone else
  owns would come back on its next load.

Counts are read live, so one list serves a whole click. `Take` counts first (`RemoveItem` returns
nothing), then takes from the bag, then from the nearest chest on.

### Which parts a click builds

`PartialBuild.Plan` reads the world and changes nothing.

1. Costs off: every unbuilt part, bottom to top.
2. The materials pay for every unbuilt part: all of them by pivot height, no support filter, as the
   old all-or-nothing build did.
3. Else: support is solved once for the whole blueprint. A part that falls even when all is built
   (the model is careful) is exempt from the support step below.
4. The unbuilt parts are sorted: pivot height, then distance from the footprint's centre, then file
   order.
5. Rounds, until one adds nothing. Each round solves support for built + chosen, then for each part:
   cost 0 when its free-build key is set; skipped, not stopped, when it costs more than is left;
   skipped when it needs a station that is neither in range at the part's own spot nor the
   blueprint's own (built or chosen, within its range); skipped when it would fall; else chosen and
   paid off the budget. A new round starts only while some part left is paid for and has its
   station.
6. Each chosen piece is paid just before it goes down. One that can no longer be paid is skipped,
   never placed free.

The ground is the terrain under each point (`PartialBuild.GroundUnder`, one read per 5 cm). 400
pieces plan in 15 to 60 ms. The messages: "Built 4 of 16 pieces of Workshop. Still missing: 20
Wood.", "Workshop planned, nothing built yet. Missing: ...".

### No copy is a crafting station

`BlueprintPreview.StripToVisuals` switches off and destroys every `CraftingStation` on a copy and
hides its area marker. Before that, the hammer's preview of the Workshop counted as a workbench
(`HaveBuildStationInRange` found the ghost), so a piece that needs one could be built with no real
bench near. The game's own one-piece ghost still keeps its station, and its first range call throws
in `CraftingStation.GetExtensions`. So read `m_buildRange` / `m_rangeBuild`, never
`GetStationBuildRange()`, on a station that may be a ghost.

---

## 7. Unfinished builds (sites)

A click that leaves parts unbuilt, even all of them, keeps a site and goes straight to Continue on
it (see "After a click" below).

### The file, the built match, the ghosts, the plan cache

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

### The ghost looks

`SetPart`: a built part is hidden, a part the next click builds is **light blue**, the rest is
**red** (waiting for materials, a station or support). Both tints go through the game's
`MaterialMan` on `_Color` and `_EmissionColor` (the calls `Piece.SetInvalidPlacementHeightlight`
makes), with the glow at 0.7 of the colour, as the game's red. The colours sit side by side in
`BlueprintPreview`: `RedTint` (the game's `Color.red`) and `ReadyTint` (0.35, 0.7, 1).

The blue is needed: the game's ghost material is nearly opaque, and an untinted part looks like
built wood. Only a site's ghost is blue (`PreviewStyle.Site()`); the hammer's preview of a new
blueprint keeps the plain ghost look. A part that changes look loses its old tint (ready to built:
none, ready to waiting: red).

### Continue

The blueprint key offers every site within 40 m of its box first, nearest first ("Continue: Workshop
(6/16)", plus ", 6 m behind" when two sites share a name), then the normal blueprints. In Continue
`BlueprintMode.CurrentSite` is the site and `Current` its blueprint; the preview is the tracker's own
ghost (no second copy), so nothing follows the aim and the wheel and the pad turn do nothing.

The click reads the world again, takes the plan from the tracker's cache (`Site.ReadyOrder`),
refuses on a `BlockedReason`, then leaves out each part whose own box (grown 0.6 m) holds a
character, and whatever would need it (`PartialBuild.Without`); a part left out stays ready. The
click that puts the last part up deletes the file and ends blueprint mode.

While blueprint mode is on (Continue too) the game's own `UpdatePlacement` does not run, so Remove
never takes down the aimed piece there. The site's ghost is never tinted by the click, so nothing
stays red after Continue ends. On the pad: square is the key, R2 the click, R1 (the game's
`JoyRemove`) is Remove.

### After a click

`BlueprintMode.Build`, the mouse and R2 alike:

| The click | Then |
|---|---|
| builds all of it | blueprint mode ends (`Exit`): the hammer is back on its own piece, as after picking one from the build menu |
| leaves parts, even all of them | keeps a site and goes to Continue on it (`ContinueAfterClick`), with the key's label ("Continue: Workshop (6/16)"). No second message |
| Continue's last part | "Workshop finished.", the file goes, blueprint mode ends. No normal blueprint is left in hand |

The site's ghost is built over the next few frames (8 pieces a frame, 0.05 s at most for the
Workshop), so for those frames nothing shows the missing parts. If the site file cannot be written,
the blueprint stays in hand as before. The card's title reads "Continue: Workshop" in Continue, from
the key or from a click.

### Remove in Continue

`BlueprintMode.PressRemove`: the game's `Remove` on release, or `JoyRemove`, never with L2 held. It
reads the world first.

| What stands | What one press does |
|---|---|
| none of the build | the plan goes at once (file and ghost), blueprint mode ends, "Plan for Workshop removed." No window |
| some of it | the remove window: "Remove Workshop?", "6 of 16 pieces are built.", three buttons |

| Button | Does |
|---|---|
| Cancel (left) | nothing. Continue stays on. Also Esc, circle, or cross while Cancel is picked |
| Unbuilt parts | the plan goes, the built pieces stay. "Plan for Workshop removed. The 6 built pieces stay." |
| Whole structure | the plan goes and every built piece comes down (`SiteRemoval.TakeDown`). "Workshop removed: 6 pieces taken down." |

Either removal ends blueprint mode.

### The remove window

`SiteRemovePopup`:

- **The game's own popup.** `UnifiedPopup` has one row for two buttons (No, Yes), so this pushes a
  popup of its own `PopupType` (100, none of the game's). The game shows its panel, dark background
  and title for it, and none of its own buttons. Three copies of the game's No button
  (`buttonLeft`) go on the panel, which widens to fit (about 580 units, the game's is 400). The
  panel's width and the popup's default button go back, and the copies hide, whenever this popup is
  not the one showing (closed, or a game popup pushed over it). Copies are made once per popup
  object; a world load makes a new one.
- **It blocks like the game's popups**, through the game's own checks: `Menu.IsVisible()` is true
  while a `UnifiedPopup` shows, so the player cannot move, act, build, open the inventory, the map or
  the editor, and the cursor is free. No input patch was needed for it.
- **Pad.** Cancel is picked at first (the game's "are you sure" dialogs start on No). The D-pad and
  the left stick move along the row (explicit navigation, the game's UI module), cross presses the
  picked button, circle cancels (the Cancel copy keeps the game's `UIGamePad` on Esc and
  `JoyButtonB`). The icons sit where the game puts them, on the button's top right corner: circle on
  Cancel, cross on the picked button. Their text is set from the game's `$KEY_JoyButtonB` /
  `$KEY_JoyButtonA`: the copies are not in the game's list of texts it translates again, and the
  prefab's own hint text reads `MISSING BUTTON DEF "ButtonB"`.
- **The closing press stays out of the game.** Circle that closed it was a jump in the next physics
  step, when the popup no longer holds the player (1.4 m, measured), so every game button bound to
  cross and circle is reset (`ZInput.ResetButtonStatus`, as `InventoryGui` does). The Esc that
  cancelled would open the pause menu in the same frame, so `Menu.Update` skips that frame
  (`SiteRemovePopup.TakesEscape`).

### Whole structure

`SiteRemoval.TakeDown` follows the rules of the hammer's Remove (`Player.RemovePiece`):

- The world is read again (`SiteTracker.BuiltPieces`). Refused as a whole, with the plan kept and
  Continue on: more than 40 m from the site's box, no stamina for one swing.
- Each piece gets the game's checks, silently: the tool can remove it (`m_canRemovePieces`, feasts
  apart), `m_canBeRemoved`, not in a no-build zone, `PrivateArea.CheckAccess` (no flash), the station
  rule of `CheckCanRemovePiece` (the station near the **player**, unless costs are off or
  `NoWorkbench`), a `ZNetView`, `Piece.CanBeRemoved()` (a chest must be empty, a ship empty).
- Then the body of `RemovePiece`, call for call: `IRemoved.OnRemoved`, `WearNTear.Remove()` (which
  drops the materials and plays the break), else the same fallbacks. So the materials come back
  exactly as the hammer's Remove gives them: dropped where each piece stood, picked up as usual. Per
  piece the skill's remove debt and the game's "pieces removed" count, as `UpdatePlacement` does. One
  swing for the whole take-down (stamina, durability, noise), as for a build click.
- Top to bottom: the reverse of the build order (`PartialBuild.BuildOrder`), all in one frame.
- A refused piece stays, and so does every piece it needs to stand (the support model, tried top to
  bottom near it), so nothing is left to fall. The message counts them: "Workshop removed: 12 of 15
  pieces taken down. Left standing: 1 can't be removed, 2 hold it up.", "... 5 need a workbench
  nearby." The plan is removed all the same.
- The station check reads the range the station worked out last (`m_buildRange`, `m_rangeBuild`)
  and counts only real network stations, never a ghost (§6, "No copy is a crafting station").

---

## 8. The materials list on the hammer card

In blueprint mode `BlueprintInfoCard` hides the card's six requirement slots and puts a
`MaterialList` on the card's right, bottom edges level, never lower than the card.

- **Rows.** A row per item: its icon, its name, have / need (have green when enough, red when short)
  and a bar under it; then a row per station: "in range", "not in range", "in blueprint" or "not
  needed". Past 10 rows it takes two columns, nothing is hidden.
- **The card's text** is the blueprint's own description and "16 pieces." (Continue: the description
  only). No controls: those are in the game's hint row (`HintRow`, §9).
- **The footer** is one line: "Can build now: 6 of 16 pieces", in Continue "Built 6 of 16. Can build
  now: 3 more." Where the materials come from (bag, chests) is not shown: the user took that line
  out. When the list is shorter than the card, the footer still sits at the bottom
  (`MaterialList.MinHeight`) and the spare room goes above its line, so nothing is empty under it.
- **Where it sits.** It is a child of the card (`SelectedInfo`, scale 1.25), so it hides with the HUD
  (Ctrl+F3). It does not grow up over the card: the game moves the stamina and eitr bars to y 320 and
  285 above the card in build mode. Its background is the card's own `Bkg2` sprite, at 70 % black
  instead of 50 % so red numbers read over bright grass.
- **Nothing to undo.** The game sets its slots every frame, so a normal piece gets them back; the
  postfix only hides the list.
- **Numbers:** `MaterialTally.For` every 0.5 s and at once after a click (`BlueprintMode.TryBuild`
  calls `RefreshSoon`). In Continue the rows are the missing parts' cost and "Can build now" is the
  tracker's cached `site.ReadyCount`.
- **The plan cache.** For a normal blueprint the card keeps its own: item counts, stations within
  64 m, free-build keys, costs on or off, and the preview's spot (again after 0.5 m or 7.5 degrees).
  A plan over 5 ms waits until the preview holds still for one refresh, so moving a 400-piece
  blueprint (15 ms) never stutters. With no aim it keeps the last spot the preview stood on.
- `MaterialList` knows nothing of the card (parent, width, most columns and least height come from
  the caller).

---

## 9. The controls row

`HintRow` puts the controls of blueprint mode, Continue and the capture in the game's own hint row
along the bottom of the screen (`KeyHints`), in its look:

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
  When the mode ends the game's update runs again and sets every group as always: the vanilla row is
  back with nothing to undo.
- **When not.** Only where the game would show its row: its key hints setting on, the player alive,
  no chat, no pause, no skills or trophies panel, no inventory, radial, build menu or barber. Those
  keep the game's row. The editor window open: the game's row too, as before.
- **Texts.** Pad icons: `Localization.GetBoundKeyString("Joy...")`, the same call the game's own
  entries go through, so the same family (xbox, ps5, switch2) and layout. Keys: the game's names for
  its own buttons (`Attack`, `Remove`, `BuildMenu`), `ZInput.KeyCodeToDisplayName` for the mod's
  config keys and Esc; Shift and Alt are plain "Shift" and "Alt" (either side works). The pad's build
  menu button is `JoyUse`, or `JoyBuildMenu` in the alternative layouts
  (`Player.UpdateBuildGuiInput`); Rotate is `JoyRotate` + `JoyRStick` in the default layout,
  `JoyRotate / JoyRotateRight` in the others. Written again only when a text changes; a rebound key
  shows within a second.
- Our copies are not in the game's localization table, so its re-localizing never touches them.
- Rotate is the last blueprint entry, where the game's own row has it: the wheel is taller than a key
  cap, and further left it sat right under the materials list.

---

## 10. The autotest

### What each scenario checks

`./scripts/autotest.sh <name>`, Debug builds only.

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
| `editor_keys` | every key, the wheel, the mouse, the top bar, the dialogs; deleting one of your blueprints with a real mouse click, Esc back to the list, the right arrow and Enter |
| `editor_pad` | every controller button through a made-up pad, and the piece menu. The help table has 22 rows, the close row included. The right stick reads and turns like the game's: its stick numbers, 110 degrees a second times the game's Gamepad sensitivity, its invert Y (§4) |
| `editor_focus` | the pad and Tab walk the top bar and both panels, and press what they find; deleting blueprints in Blueprints with the pad alone (right to Delete, the question on Cancel, circle and Cancel back to the same row, the walk on the next row after, the open blueprint left as not saved). Screenshot `editor-focus-delete` |
| `editor_keep` | close and open again finds everything as it was, with F7 and with L2 + square, the hand blueprint asks over unsaved changes, a dead pane is built again on the same view, Forget leaves nothing |
| `editor_build` | a blueprint made in the editor, built in the world (blueprint mode off after), taken back with the key, then edited again |
| `editor_capture` | F8 on bare ground; a kit built turned 45 degrees, a wall across the edge and a cultivator piece inside; real wheel notches turn and size the rectangle (no camera zoom); yellow and orange glow counts; the capture holds the kit file unturned; Save as up, and after "Discard?" over a kept blueprint; Esc leaves no colour; the whole capture on a pad the game reads (L2 + triangle, each D-pad step, the status line one line with no controls and the game's hint row on the capture's pad set, taken into Save as, a second one stopped by circle) with the hotbar, the forsaken power, the camera and minimap zoom, the inventory and the jump untouched, then the same presses with no capture up reaching the game; the glow's cost on 400 floors. Screenshot `editor-capture-3-pad` |
| `editor_support` | the support rule against the game's own numbers on test structures and a kit, the ghost's colours on the picture, a refused drop, nothing left in the world. `VT_SUPPORT_EDITOR_ONLY=1` skips the world half |
| `editor_all` | all of the above except `probe_build`, in one game: the 17 editor ones and `blueprints`, then `build_sources`, `build_partial`, `build_sites`, `build_continue`, `card_materials`, `hint_row`. Then the art guard. **This is the one to run.** |

`editor_all` took about 11 minutes on 21-09-2026: 23 scenarios, 1308 checks, with the pad checks.

### The shared build spot

Every world-building scenario shares one build spot (`AutoTest.MoveToBuildSpot`): searched from the
middle of the world, found once a run, always faced the same way, and **levelled flat with a terrain
op** before anything is built. All three matter. Valheim's meadows roll by 1 to 2 m over a 16 m
square, and on a slope a kit piece loses its support and the check is a coin flip.

### The reset between scenarios

Between two scenarios the chain resets (`AutoTest.Reset`): the editor closes, the capture stops, the
blueprint leaves the hammer, the fake pad is dropped, the library reloads, every unfinished build is
forgotten and `.devtest/sites` deleted, and every editor and build setting goes back to its default,
so no run changes the player's config file. `VT_CHAIN` exists to reproduce a scenario that leaves
something behind without sitting through all twenty-three.

### The quiet test world

`src/Dev/AutoTestPeace.cs` runs at the start of every scenario, before the first check. It stops
every AI (a prefix on `BaseAI.UpdateAI`), stops new spawns (`SpawnSystem.m_nospawn`), sets the raid
chance to zero, and despawns whatever is already standing around (17 creatures at the spawn point on
one run). Without it a greydwarf could wander in, hit the structure under test, and a support check
failed for a reason that had nothing to do with the code. That was the real cause of the flaky runs.
Debug builds only, like `AutoTest` itself.

### The art guard

The art rule is in `CLAUDE.md`. Everything the mod draws is made at runtime from what the game
already loaded: prefab meshes are cloned, shaders come from `Shader.Find` among the loaded ones,
icons come from the piece's or the item's own sprite, line materials are made in code.

`editor_all` ends with a guard (`CheckNoArtWritten`) that walks every folder the mod can write to and
fails on anything else. The chain deletes the sites folder between scenarios, so `ClearSites` runs
the same walk first and keeps what it found, plus the files the store says it kept. The guard fails
when the walks saw no site file, or missed one the store kept, so a folder list that lost `sites/`
cannot pass. A `VT_CHAIN` run in which no scenario kept a site skips that part with a logged note.
`autotest.sh` then walks the repo for image, mesh and bundle files and prints `art guard:`.

---

## 11. Tomrer

Tomrer is the desktop editor in the separate repo `../Tomrer`. Since Phase 11 the in-game editor
covers everything it does, so it may be removed. While it exists it reads and writes the same
`blueprints/*.blueprint` files.

Its blueprint check compiles `src/Blueprints/Blueprint.cs` and `BlueprintFormat.cs` on their own,
against `UnityEngine.CoreModule` alone. That is why those two files stay free of other game code,
whether Tomrer stays or goes: it is a cheap way to keep the format readable outside the game.
