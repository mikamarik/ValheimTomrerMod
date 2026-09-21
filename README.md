# ValheimTomrer

Make blueprints in the game, then build them with the hammer. **Valheim 1.0**, single player.

## Features

- **Blueprint editor in the game.** F7 opens a window with a 3D view, the full piece list and
  the game's own snapping. No second program, no alt-tab.
- **Build a whole blueprint with the hammer.** B cycles your blueprints, the preview follows your
  aim, one click puts every piece down that your materials pay for.
- **Materials from your chests.** The hammer takes what a blueprint costs from your bag and from
  the chests near you (carts and ship holds too).
- **Build what you can now, the rest later.** Short of materials? One click builds the part they
  pay for, from the bottom up, and only pieces that would stand. The rest waits as ghost pieces
  until you come back with more.
- **A clear materials list.** The build card shows every material with its icon, what you have,
  what you need and a bar. The editor shows the same list.
- **See what holds before you place it.** The piece in hand wears the colour the hammer shows on
  a built piece: blue on the ground, then green, yellow, orange and red as it gets weaker. A piece
  that would fall down blinks red and cannot be placed. Pointing at a placed piece shows its colour.
- **Copy what you already built.** F8 puts a rectangle on the ground. Turn it and size it with the
  wheel, the pieces inside glow, and everything in it becomes a blueprint.
- **The game's rules still apply.** Real materials, a workbench in range, and only pieces this
  character has unlocked.
- **Mouse and controller.** Placing, moving, turning and deleting all have a pad button, and
  L3 walks the top bar, the side panels and every window, so Save, Open, the filters and the name
  field work without a mouse. The controls along the bottom of the 3D view show the game's own
  button icons.
- **Plain text files.** A blueprint is a small text file you can read, edit or send to a friend.
- **Nothing added to your world.** No custom pieces, no custom items, no files but the blueprints.
  A build you have not finished yet is a blueprint file too, not part of your world save.

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

1. **Press F7.** The editor opens on one of the blueprints that ship with the mod. No hammer needed.
2. **New** in the top bar starts an empty one.
3. **Pick a piece** in the Pieces list on the left. It follows the mouse in the 3D view.
4. **Click to place it.** It snaps to what is already there, the same way the hammer does.
   Hold Shift for no snapping. Q and E pick which corner of the piece goes on the spot you aim at.
   The piece stays in hand, so click again for the next one. Esc stops placing.
   Its colour says how well it would hold, the same colours the hammer shows in the game. Red and
   blinking means it would fall down: the click does nothing, so hold it up first (a pole or a
   wall under it).
5. **Fix mistakes:** click a piece to select it, G moves it, R turns it, Del removes it,
   Ctrl+Z undoes. Drag a box over several pieces to take them all.
6. **Name it** on the right, write a line of description, and pick the icon the build card shows.
   The Checks list under it says what is still wrong, including pieces that would fall down after
   you removed what held them.
7. **Ctrl+S saves it** to `BepInEx/config/ValheimTomrer/blueprints/`.
8. **Build this** in the top bar wants the hammer already in hand. It hands the blueprint over and
   closes the window. Aim at the ground and click, like any other piece.

The blueprints that ship with the mod are read only. Edit one and Save as makes your own copy.

## Copy a building you already have

1. Stand where you can see the whole thing. No hammer needed.
2. **Press F8.** A yellow rectangle (8 x 8 m) lies on the ground where you aim, and follows your aim.
3. **Fit it to the building:**

   | Input | What it does |
   |---|---|
   | Wheel | Turn it 22.5 degrees |
   | `Shift` + wheel | Width, 2 m a notch |
   | `Alt` + wheel | Depth, 2 m a notch |
   | `Shift` + `Alt` + wheel | Both sides |

   The pieces it will take glow yellow. A piece that crosses the edge but stays out glows orange.
   The top left of the screen shows the size, the turn and how many pieces are in.
4. **Press F8 again.** The editor opens on what stood in the rectangle, with Save as open and a
   name to fill in. `Esc` stops it instead, and the glow goes away.

The rectangle takes everything from 8 m under the ground to 64 m over it, so a roof comes along.
A house built at an angle comes back straight. Only pieces the hammer can build come along.
Anything else (a planted turnip, a piece of another mod) is left out, and the message says how
many.

## Build a blueprint

1. Take the hammer out.
2. Press **B** until the blueprint you want is on screen. After the last one it goes back to
   normal building.
3. Aim. The whole thing shows where it will land. The wheel turns it.
4. Look at the materials list next to the build card (below).
5. Click. Every piece it can pay for goes down at once, and costs what it would cost one by one.

**Where the materials come from.** Your bag first, then every chest within 20 m, nearest first.
Carts and ship holds count too. A chest counts only when you may open it (not someone else's
private chest, not behind someone else's ward). Change the range or turn chests off in the config
(`Build` section, below).

**Not enough materials?** The click still builds what they pay for:

- from the bottom up;
- only pieces that would stand on what is already there;
- the message says how many went down and what is still missing.

With nothing at all to pay with, nothing is built, but the plan is kept anyway. Either way the rest
waits for you as an unfinished build (next section).

It refuses when a piece is not unlocked, the workbench is out of range or the ground is blocked.
The message says which.

**The materials list.** In blueprint mode the build card gets a list on its right, one row per
material:

- the item's icon and name;
- what you have (bag and chests) and what the blueprint needs, green when there is enough, red
  when not;
- a bar that fills as you gather it;
- a row for each crafting station: in range, not in range, or in the blueprint itself;
- at the bottom: how many pieces the next click would build.

The editor (F7) shows the same list under the blueprint's name, counted from your bag and the
chests near where you stand.

## Finish a build later

Near an unfinished build, with the hammer out:

- the pieces still missing show as ghost pieces. **Light blue**: the next click builds them.
  **Red**: they wait for more materials;
- **B** offers "Continue: Workshop (6/16)" first, before your normal blueprints. It only shows
  within 40 m;
- in Continue the preview stays on the build, whatever you aim at. Click to build what your
  materials pay for now. Stand inside the house if you like: a piece where you stand is left out
  and the message says so;
- the last piece up says "Workshop finished." and the plan is gone;
- to drop a plan, press the hammer's **Remove** button (the one that takes a piece down) twice
  within 3 seconds. The pieces already built stay.

Quit and come back later: the plan is still there. What is built is read from the world, so a
piece you built or broke by hand counts too. Unfinished builds are kept per world in
`BepInEx/config/ValheimTomrer/sites/`.

The check whether a piece would stand is made for flat ground. On a steep slope the game may still
let a piece fall; its materials drop on the ground.

## Keys in the editor

| Key | What it does |
|---|---|
| Click | Select a piece, or drop what is in hand. `Shift`+click adds or removes. |
| Right drag | Look around. The cursor stays where it is. |
| Middle drag, `Shift` + right drag | Pan |
| Wheel | Zoom toward the cursor. While placing it turns the piece 22.5 degrees. |
| Drag on the view | Select everything in the box. Only while the cursor is free. |
| `W` `A` `S` `D` | Fly forward, back, left, right |
| `Space` `Ctrl` | Fly up, down. Hold `Shift` to fly 3 times faster. |
| `C` | Hold the mouse in the pane, so it looks around like flying in the game. `Esc` gives it back. |
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
| `Tab`, `Shift+Tab` | Walk the top bar and the two side panels, on and back. In a window like Open or Save as, it walks what that window holds. |
| Arrows, `Enter` (while walking) | Step to the next thing, and press it. A text box starts typing. |
| `Esc` | In this order: give a text box back, close the window, stop placing, give the mouse back, leave the walk, clear the selection, close the editor |

On a Mac, `Cmd` works everywhere `Ctrl` does.

## Controller in the editor

PlayStation names first, Xbox names in brackets.

| Button | What it does |
|---|---|
| Left stick | Fly forward, back, left, right |
| L1 (LB) + left stick | Fly 3 times faster |
| Right stick | Look around |
| D-pad up, down | Fly up, down |
| R2 (RT) | Place, else select the piece in the middle of the view |
| L1 (LB) + R2 (RT) | Add that piece to the selection, or take it out |
| L2 (LT) + right stick left, right | Turn 22.5 degrees |
| L1 (LB) hold | No snapping while held |
| L3, R3 (stick clicks) | While placing: the snap point. Else: L3 walks the panels, R3 looks at the selection. |
| L3 (LS), nothing in hand | Walk the top bar and the two side panels. An orange ring shows where you are. |
| While walking: D-pad, left stick | Step to the next thing |
| While walking: L1 (LB), R1 (RB) | Change panel: top bar, left, right. In a window they do nothing, it has nowhere to walk to. |
| While walking: × (A) | Press what the ring is on. A text box starts typing. |
| While walking: ○ (B) | Back to the 3D view. In a name box it gives the keyboard back first. |
| × (A) | Pieces menu: D-pad chooses, L1 R1 change the tab, × places, ○ closes |
| ○ (B) | The same order as `Esc`: give a text box back, close the window, stop placing, leave the walk, clear the selection, close the editor |
| □ (X) | Move the piece in the middle of the view. It follows the crosshair, R2 drops it. |
| △ (Y) | Copy it. Copies keep coming until ○ stops them. |
| R1 (RB) | Delete it |
| The three above | Take the whole selection when the aimed piece is part of it, and the selection on its own when the crosshair is on nothing |
| L2 (LT) + R2 (RT) | Place another piece of the kind in the middle of the view |
| D-pad left, right | Undo, redo |
| Options (Menu) | This help |

**L3 reaches the top bar and both side panels.** An orange ring marks where you are, and every
button, tab, chip and text box on the walk can be pressed with ×. That covers Build this, Save,
Open, the name and description, the icon and the piece filters. Three lists are still mouse only:
the piece grid, In blueprint and Checks. The pieces menu on × is the way to place without a mouse.

**Windows take the walk on their own.** Open, Save as and every question put the ring on
themselves the moment they show up: the D-pad steps through the blueprints or the buttons, × picks
one, ○ closes. A long list scrolls to follow the ring. The rest of the pad does nothing while a
window is up.

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
| Editor | `CaptureKey` | `F8` | Puts a rectangle on the ground where you aim. Press again and what stands in it becomes a blueprint. |
| Editor | `ShowAllPieces` | `false` | Every piece in the editor, instead of only the ones this character has unlocked. |
| Editor | `SnapDots` | `true` | Show the snap dots while placing. Snapping itself is always on. |
| Editor | `Boxes` | `false` | Draw pieces as plain boxes instead of models. |
| Editor | `LookSensitivity` | `1.0` | Mouse look speed in the 3D view. 2 is twice as fast. |
| Editor | `PadLookSensitivity` | `1.0` | Right stick look speed. 2 is twice as fast. |
| Build | `UseChests` | `true` | A blueprint build also takes materials from chests near you, after your bag. Off: the bag only. |
| Build | `ChestRange` | `20` | How far a chest may be from you, in metres (0 to 100), and still give materials. |

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

A blueprint with no pieces in it yet is a normal file: New, then Save as, names it before you have
placed anything. The B key skips it until it has something to build.

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
