# ValheimTomrer

Build whole structures from blueprints in **Valheim 1.0**.

> **Early development.** The plugin loads and is stable, but no gameplay features have
> shipped yet. Watch this space.

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

## Configuration

A config file is generated on first launch at:

```
Valheim/BepInEx/config/com.mikamarik.valheimtomrer.cfg
```

| Setting | Default | Description |
|---|---|---|
| `Enabled` | `true` | Master switch. Turn off to neutralise the mod without uninstalling it. |

Editing it in-game is easiest with
[Configuration Manager](https://thunderstore.io/c/valheim/p/Azumatt/Official_BepInEx_ConfigurationManager/) (F1).

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
