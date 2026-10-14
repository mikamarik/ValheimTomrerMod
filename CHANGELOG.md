# Changelog

## 0.2.0

### Foundation anchors and ground preparation

- Fix misplaced previews caused by smoke/range utility colliders being treated
  as foundation geometry. Measure placement bodies near the actual visible model.
- Add a placement height offset of up to +/-5 metres in 0.25 m steps via
  Page Up/Page Down or Ctrl + wheel, shown on the build card. Clamp to the
  foundation's editable terrain depth and, when leveling, maximum height.
- Preserve the original ghost model lifecycle and LOD setup. Keep Debug-level creation and
  first-placement geometry/rendering diagnostics to diagnose missing previews.
- Extend whole-blueprint aiming to 50 metres by default, configurable from 50 to
  100 metres, including the third-person camera offset. Extend Continue to 50 metres.
- Add bottom-center and four corner anchors measured from the blueprint's lowest
  solid foundation layer. Q/E or controller D-pad left/right select an anchor.
- Snap the selected whole-blueprint anchor to nearby built-piece snap points.
- Add optional **Prepare ground on placement**, available in the editor and
  toggled with T or controller D-pad up while aiming. It starts off.
- Preview the lowest foundation's rotated rectangular footprint and its terrain
  grid margin. Raise and lower that area on a valid build click.
- Check proposed and applied support before construction, preserve terrain paint,
  respect vanilla height limits and access rules, and restore the terrain data
  when an operation fails before placing any pieces.
- Keep prepared terrain once construction begins; partial-build Continue does
  not repeat terrain preparation.
- Add a Debug foundation/terrain regression scenario.

### Editor scrolling and camera

- Scroll anywhere inside an editor list, including the gaps between piece icons
  and material toggles.
- Move one row per mouse-wheel step instead of a fixed pixel distance. Piece grids
  use their current icon-row height; material lists use their material-row height.
- Add `Editor.MenuRowsPerNotch` (default 1, range 1–10) to adjust menu scrolling
  independently of camera zoom.
- Stop the character camera from zooming when using the wheel in the editor or
  building capture mode.
- Apply `Editor.MenuScrollMultiplier` to the editor's 3D zoom, with a default of 10.
  Keep previously saved values. This setting no longer changes menu scrolling.

### Gathering blueprint materials

- Add a **Pull materials...** button pinned to the top of the editor's right-hand
  blueprint panel, outside its scrolling content.
- Choose individual required materials, or use **Except wood/stone**,
  **Non-teleportable**, **All**, and **None** presets.
- Include crafting stations and station-upgrade components such as flint, deer hide,
  and obsidian when the corresponding pieces are part of the blueprint.
- Pull only the selected blueprint's requirements that are missing from the player's
  inventory. Repeated pulls fill the remaining requirement.
- Use nearby eligible containers, including carts and ship holds, through the existing
  `Build.UseChests` and `Build.ChestRange` settings (20 m by default).
- Fill compatible stacks before empty slots. Leave materials that do not fit in
  their source containers, and report what moved or could not be gathered.
- Allow the gathered materials to exceed the player's carry-weight limit so they
  can be loaded into a cart or ship manually.
- Add `Build.PullExcludedPrefabs` for the default wood/stone exclusion list.
  Individual selections are remembered for the current game session.
- Support the existing keyboard and controller navigation in the new dialog.

### Development and compatibility

- Add optional Linux build and packaging helpers that detect Steam game assemblies
  and BepInEx installations, including Gale/r2modman profiles. Build Debug locally or
  create a Release package without automatically installing it.

- Fix compilation with C# 14 by renaming the local `field` variable in
  `ModUi.Typing` to avoid the new property-accessor keyword.
- Add Debug regression scenarios for material transfers, inventory capacity,
  material selection, dialog navigation, camera scrolling, and row-based scrolling.

Material pulling currently targets single-player and retains the existing
locally owned-container and access rules. Multiplayer operation has not been
validated. It does not add station upgrades absent from the blueprint or
automatically load materials into a cart or ship.

## 0.1.0

- First release.
