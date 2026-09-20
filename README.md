# ValheimTomrer

Make blueprints in the game, then build them with the hammer. **Valheim 1.0**, single player.

## Features

- **Blueprint editor in the game.** F7 opens a window with a 3D view, the full piece list and
  the game's own snapping. No second program, no alt-tab.
- **Build a whole blueprint with the hammer.** B cycles your blueprints, the preview follows your
  aim, one click puts every piece down.
- **The game's rules still apply.** Real materials, a workbench in range, and only pieces this
  character has unlocked.
- **Mouse and controller.** Placing, moving, turning and deleting all have a pad button. The
  side panels still need the mouse.
- **Plain text files.** A blueprint is a small text file you can read, edit or send to a friend.
- **Nothing added to your world.** No custom pieces, no custom items, no files but the blueprints.

## Requirements

- Valheim **1.0** or later
- [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) **5.4.2350+**

## Installation

**With a mod manager** (r2modman, Gale, Thunderstore Mod Manager), install from
Thunderstore and launch through the manager. Nothing else to do.

**Manually**, drop the `ValheimTomrer` folder into:

```
Valheim/BepInEx/plugins/
```

## Make a blueprint

1. **Press F7.** The editor opens on one of the kits that ship with the mod. No hammer needed.
2. **New** in the top bar starts an empty one.
3. **Pick a piece** in the Pieces list on the left. It follows the mouse in the 3D view.
4. **Click to place it.** It snaps to what is already there, the same way the hammer does.
   Hold Shift for no snapping. Q and E pick which corner of the piece goes on the spot you aim at.
   The piece stays in hand, so click again for the next one. Esc stops placing.
5. **Fix mistakes:** click a piece to select it, G moves it, R turns it, Del removes it,
   Ctrl+Z undoes. Drag a box over several pieces to take them all.
6. **Name it** on the right, write a line of description, and pick the icon the build card shows.
   The Checks list under it says what is still wrong.
7. **Ctrl+S saves it** to `BepInEx/config/ValheimTomrer/blueprints/`.
8. **Build this** in the top bar wants the hammer already in hand. It hands the blueprint over and
   closes the window. Aim at the ground and click, like any other piece.

The kits that ship with the mod are read only. Edit one and Save as makes your own copy.

## Build a blueprint

1. Take the hammer out.
2. Press **B** until the blueprint you want is on screen. After the last one it goes back to
   normal building.
3. Aim. The whole thing shows where it will land. The wheel turns it.
4. Click. Every piece goes down at once, and costs what it would cost one by one.

It refuses when a piece is not unlocked, a material is missing, the workbench is out of range or
the ground is blocked. The message says which.

## Keys in the editor

| Key | What it does |
|---|---|
| `B` | Orbit camera or free camera. Free looks around with the mouse, like flying in the game. |
| `W` `A` `S` `D` | Fly forward, back, left, right |
| `Space` `Ctrl` | Fly up, down. Hold `Shift` to fly 3 times faster. |
| Right drag | Turn around the point in front (orbit), or look around (free) |
| Middle drag, `Shift` + right drag | Pan |
| Wheel | Zoom toward the cursor. While placing it turns the piece 22.5 degrees. |
| Click | Select a piece, or drop what is in hand. `Shift`+click adds or removes. |
| Drag on the view | Select everything in the box (orbit camera) |
| `Ctrl+A` | Select all |
| `G` | Move the selection |
| `Ctrl+D` | Duplicate. Copies keep coming until `Esc`. |
| `R`, `Shift+R` | Turn 22.5 degrees: the piece in hand, else the selection |
| Arrow keys | Nudge 0.5 m along the ground axis closest to the camera (`Alt` 0.1 m) |
| `PageUp` `PageDown` | Nudge up, down |
| `Shift` (hold) | No snapping while held, like in the game |
| `Q` `E` | Pick the snap point that goes on the spot you aim at |
| `Delete` `Backspace` | Delete the selection |
| `Ctrl+Z`, `Shift+Ctrl+Z`, `Ctrl+Y` | Undo, redo |
| `F` | Look at the selection, or at everything |
| `Ctrl+S` | Save |
| `H` `?` | The help window, with the same tables |
| `Esc` | Stop placing, else clear the selection, else close the editor |

On a Mac, `Cmd` works everywhere `Ctrl` does.

## Controller in the editor

PlayStation names first, Xbox names in brackets.

| Button | What it does |
|---|---|
| Left stick | Fly forward, back, left, right |
| L1 (LB) + left stick | Fly 3 times faster |
| Right stick | Look around (free camera), or circle the point in front (orbit) |
| D-pad up, down | Fly up, down |
| R2 (RT) | Place, else select the piece in the middle of the view |
| L1 (LB) + R2 (RT) | Add that piece to the selection, or take it out |
| L2 (LT) + right stick left, right | Turn 22.5 degrees |
| L1 (LB) hold | No snapping while held |
| L3, R3 (stick clicks) | While placing: the snap point. Else: L3 switches the camera, R3 looks at the selection. |
| × (A) | Pieces menu: D-pad chooses, L1 R1 change the tab, × places, ○ closes |
| ○ (B) | Stop placing, or clear the selection, or close the editor |
| □ (X) | Move the selection |
| △ (Y) | Duplicate the selection |
| R1 (RB) | Delete the piece in the middle of the view |
| L2 (LT) + R1 (RB) | Copies of that piece keep coming until ○ stops it |
| L2 (LT) + R2 (RT) | Place another piece of the kind in the middle of the view |
| D-pad left, right | Undo, redo |
| Options (Menu) | This help |

**The pad does not reach the side panels and the top bar.** The sticks fly and the D-pad undoes,
so there is nothing left to move a cursor with. Use the mouse for Save, Open, the name field and
the piece list. The pieces menu on × covers placing without a mouse.

## Configuration

A config file is generated on first launch at:

```
Valheim/BepInEx/config/com.mikamarik.valheimtomrer.cfg
```

| Section | Setting | Default | What it does |
|---|---|---|---|
| General | `Enabled` | `true` | Master switch. Turn off to neutralise the mod without uninstalling it. |
| Blueprints | `Key` | `B` | With a hammer in hand: the next blueprint. After the last one, back to normal building. |
| Editor | `Key` | `F7` | Opens and closes the editor window. |
| Editor | `ShowAllPieces` | `false` | Every piece in the editor, instead of only the ones this character has unlocked. |
| Editor | `CameraMode` | `Orbit` | Which camera the editor starts in: `Orbit` or `Free`. |
| Editor | `SnapDots` | `true` | Show the snap dots while placing. Snapping itself is always on. |
| Editor | `Boxes` | `false` | Draw pieces as plain boxes instead of models. |
| Editor | `LookSensitivity` | `1.0` | Mouse look speed in the free camera. 2 is twice as fast. |
| Editor | `PadLookSensitivity` | `1.0` | Right stick look speed. 2 is twice as fast. |

The top bar's **Boxes** and **Snap dots** buttons write their setting back, so the editor opens
the way you left it.

Editing the file in-game is easiest with
[Configuration Manager](https://thunderstore.io/c/valheim/p/Azumatt/Official_BepInEx_ConfigurationManager/) (F1).

## Blueprint files

```
Valheim/BepInEx/config/ValheimTomrer/blueprints/*.blueprint
```

Plain text, one line per piece. Drop a file in and it shows up in the editor and on the B key,
no restart. Files from other blueprint mods are read as far as they fit; a file the mod cannot
write back exactly opens read only, so Save as is the way out.

## Compatibility

- **Client-side only.** No server install needed, and it does not affect other players.
- **Single-player focused.** Not tested in multiplayer.
- **Safe to remove.** The mod adds no custom items or build pieces, so uninstalling it
  leaves nothing broken behind in your world.

## macOS note

On Apple Silicon, Valheim runs natively as arm64, where BepInEx currently loads no mods
at all, and does so silently. The game must be forced through Rosetta. This affects every Valheim
mod, not just this one; see the
[BepInEx tracking issue](https://github.com/BepInEx/BepInEx/issues/1303).

## License

TBD
