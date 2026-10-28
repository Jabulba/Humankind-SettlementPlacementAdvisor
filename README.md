# Settlement Placement Advisor

[![build](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/actions/workflows/push.yml/badge.svg)](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/actions/workflows/push.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Replace the vanilla mechanic that suggests where to place a settlement with a more coherent logic: two recommendations
for the terrain with the cursor and the tile at the cursor, and one recommendation for adjacent territories. Minimum
FIMs, territory border, and cost filter removed. When top recommendations tie, they are shown in full, so the count of
suggestions can be higher than two and one. The suggestions appear as the game's own white recommended tiles and FIMS
pins - the mod draws nothing new on the map.

The vanilla code suggests at most the two best tiles, picked from the tiles your army can reach in two turns, filtering
out any tile with less than 25 FIMS score, at the territory border or that you can't afford.

## Installation

### Requirements

- HUMANKIND.
- [BepInEx 5](https://docs.bepinex.dev/) (Mono) installed in the game directory.

### Steps

1. Download the latest release from the
   [Releases page](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/releases).
2. Copy `SettlementPlacementAdvisor.dll` into the game's `BepInEx/plugins` folder.
3. Optionally copy `com.jabulba.settlementplacementadvisor.cfg` into `BepInEx/config`.
4. Start or restart the game. BepInEx loads plugins only at startup.

Typical plugin locations:

```text
# Windows
<Steam>\steamapps\common\Humankind\BepInEx\plugins\

# Linux / Proton
~/.local/share/Steam/steamapps/common/Humankind/BepInEx/plugins/
```

### The tile under the cursor

The tile your cursor sits on keeps the game's own marker and pin, exactly as in the vanilla placement cursor; when that
tile is also a recommendation, the game shows it as a hovered recommended tile. The game hides that marker when the tile
can't be afforded at the cursor, and hiding it there changes nothing else: the recommendation on that tile stays, and
every other recommendation keeps its marker and pin.

## Settings

The config file lives at `BepInEx/config/com.jabulba.settlementplacementadvisor.cfg`; the release zip ships a
ready copy, and the game generates the same defaults on first launch without it.

| Knob | Type | Default | Bounds | Effect |
|---|---|---:|---|---|
| `TopHovered` | int | `2` | >= 1, clamped | Admission quota on the hovered territory: this many tiles minimum, plus every tile tied with the last admitted score |
| `TopNeighbour` | int | `1` | >= 1, clamped | Same quota on every other territory in the placement neighborhood; 0 is not allowed, so the display is never hovered-only |
| `RangeLevels` | int | `2` | >= 1, no upper bound | Placement-neighborhood hops from the hovered territory, which counts as level 1: 3 = hovered + adjacent + adjacent-to-adjacent. Every adjacency edge counts as a hop: sea, cliff, mountain and closed-border edges included |

## How it decides, briefly

- It works out which territories the search covers for the tile you are hovering.
- It drops the territories your current action cannot use, then the tiles that cannot host the settlement (unexplored,
  water, wrong terrain, blocked by the game's own placement rules).
- It scores the remaining tiles with the game's own settlement scoring and takes the best ones per territory, always
  including every tie.
- Results are cached per territory, so moving the mouse is nearly free; a territory is only re-scored when something
  that affects it changes (a claim, an exploration, a patrol, a settlement appearing). The cache belongs to the game
  you are playing: a new game re-scores from scratch.
- A recommendation is advice, not a guarantee: the game's own rules still decide whether the order is legal when you
  give it (army state, costs, enemy armies, and so on).

## Compatibility

- The mod is built for a specific game version. Download the correct mod version for your version of the game.
- Requires BepInEx 5 on the Mono/Unity player.

## Troubleshooting

Check the BepInEx log first:

```text
<game>/BepInEx/LogOutput.log
```

Search for `SettlementPlacementAdvisor`.

- **The mod did not load at all, game closed**: one of the load failure lines above. A line naming a game build newer
  than the mod needs a mod build for your game version; a line naming your own game build means the game internals are
  there and something else is replacing them - test with this mod alone.
- **Loaded, no recommendations**: check the placement mode is one of the three the mod advises (camp, city founding,
  city founding with a settler), and look for advisor lines in the log.

Under Proton, early startup errors from this mod appear in the Proton prefix's `Player.log`
rather than in `LogOutput.log`.

## Building from source

### Prerequisites

- .NET SDK 10. The helper scripts expect it at `~/.dotnet` unless `DOTNET_ROOT` is set.
- HUMANKIND installed. The mod compiles against the game's own assemblies and deploys into the install, so the build
  needs it. It is found from `-p:GameDir=...`, then `GAME_DIR`, then the standard Steam Linux location
  (`~/.local/share/Steam/steamapps/common/Humankind`).

```bash
git clone https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor.git
cd Humankind-SettlementPlacementAdvisor

scripts/build-advisor.sh               # Release build, deploys to BepInEx/plugins
```

The mod compiles against the game's own assemblies: from the private `Humankind.GameLibs`
feed when `GAMELIBS_PAT` is exported, otherwise from your game install (a missing install then fails the build with a
pointer to `GAME_DIR`). When the build succeeds against the feed on a machine with no install, deployment is skipped
with a printed warning.

[CONTRIBUTING.md](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/blob/main/CONTRIBUTING.md)
covers the commit standard and the per-clone setup. Please read it before submitting a pull request.

## Reporting problems

Bug reports and pull requests are welcome on the
[repository issues page](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/issues). Please include:

- The game version.
- The relevant `LogOutput.log` excerpt.
- The placement mode: settlement placement, city founding, or city founding with a settler.
- A clear description of the recommendations you expected and the ones you got.

## License

MIT. See [LICENSE](LICENSE).

This is an unofficial community mod and is not affiliated with Amplitude Studios or Sega.
