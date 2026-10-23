# Settlement Placement Advisor

[![build](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/actions/workflows/push.yml/badge.svg)](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/actions/workflows/push.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A BepInEx 5 mod for **HUMANKIND** that helps you choose where to place settlements.

While the settlement placement cursor is active, the advisor marks the best tiles with stars and
shows their FIMS yields as pins. The recommendation set is the placement neighborhood: the hovered
territory plus the configured number of adjacency hops over every edge - sea, cliff, mountain and
closed-border included - and every territory in it gets its own recommendations. Camp placement and
city founding are advised the same way, each through its own row of the mode-policy table.

## Features

- **Recommended-tile stars** for every territory in the placement neighborhood.
- **Stars and pins are one set**: every recommended tile gets a star, and its FIMS pin shows
  when the game's own pin rules allow it.
- **Every adjacency edge is a hop**: sea, cliff, mountain and closed-border edges extend the
  recommendation range exactly like a land edge.
- **Uses the game's own evaluation**, including its settlement score weighting.
- **City founding is advised too**: its row runs the same pipeline on the city session's own
  evaluation, and a settlement tail left by a camp session is scrubbed when a city session starts.
- **Configurable counts and range**, no external data files required.

The advisor only makes recommendations. Normal placement rules still apply.

## Installation

### Requirements

- HUMANKIND (the paths below assume the Steam install).
- [BepInEx 5](https://docs.bepinex.dev/) (Mono) installed in the game directory.

### Steps

1. Download the latest release from the
   [Releases page](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/releases).
2. Copy `SettlementPlacementAdvisor.dll` into the game's `BepInEx/plugins` folder.
3. Optionally copy `com.jabulba.settlementplacementadvisor.cfg` into `BepInEx/config`.
   If you skip this step, the game generates the file with the same defaults on first launch.
4. Start the game. BepInEx loads plugins only at startup, so restart after installing or
   updating the mod.

Typical plugin locations:

```text
# Windows
<Steam>\steamapps\common\Humankind\BepInEx\plugins\

# Linux / Proton
~/.local/share/Steam/steamapps/common/Humankind/BepInEx/plugins/
```

## Usage

1. Select an army and start **settlement placement**.
2. Hover over a territory. Recommended tiles are marked with stars.
3. Check the FIMS pins to compare the candidates.
4. Place the settlement on the tile you prefer. The advisor does not restrict your choice.

The default rules are:

| Territory | Recommendations |
|---|---|
| Hovered territory | Top 2 tiles |
| Each other territory in the neighborhood | Top 1 tile |

Every tile tied with the last recommended score is shown as well, so `TopHovered = 2` can show 3
stars, and 4 when three tiles tie at rank 2.

A tile gets no star when it is unexplored, water, not a valid settlement site for the mode, or its
territory is one the mode's filter claims nothing on: the edge of the world in both wired modes,
plus a claimed territory for camp placement. Water is filtered per tile: sea-flagged territories can
host valid camp sites, so the advisor never skips a territory for its ocean flag. The counts are
mandatory rather than a minimum-FIMS filter: tiles below the vanilla minimum-FIMS threshold are
still eligible, and a territory-border tile is no exception. The advisor does not filter on cost: a
camp or a city you cannot afford is still recommended, because the game's own placement gate reports
money and influence as post-filter costs, not as placement validity.

## Configuration

The config file lives at:

```text
BepInEx/config/com.jabulba.settlementplacementadvisor.cfg
```

| Setting | Default | Meaning |
|---|---:|---|
| `TopHovered` | `2` | Admission quota on the hovered territory |
| `TopNeighbour` | `1` | Admission quota on every other territory in the placement neighborhood |
| `RangeLevels` | `2` | Placement-neighborhood hops; the hovered territory counts as level 1 |

`RangeLevels = 2` means the hovered territory plus the territories adjacent to it. `RangeLevels = 3`
adds the territories adjacent to those. Every adjacency edge counts as a hop: sea, cliff, mountain
and closed-border edges included. Values below 1 are clamped to 1.

The tile under the mouse ghost is the advisor's own cursor slot: the mod writes it on every refresh
of a mode it has taken over, so the game's own hover highlight rule is skipped there, and a mode the
mod has not taken over keeps the vanilla cursor and its hover highlight untouched.

## How it works

For a placement mode the mode-policy table wires - camp placement and city founding - the mod takes
over the game's placement-snapshot sync
(`SettlementPlacementCursorSnapshot.EvaluatePlacement`): the advisor's own pipeline answers it
and vanilla's full settlement evaluation is never called from that snapshot. It then:

1. Dispatches on the stamped placement mode: a wired row runs the pipeline, and a mode the table
   has not wired shows nothing and leaves its evaluation to vanilla.
2. Asks the mode's army-level gate; an army that cannot build a camp, or found a city, advises
   nothing.
3. Scans the placement neighborhood: the hovered territory plus `RangeLevels` hops over every
   adjacency edge, every tile of every territory in it, with no reachability, zone-of-control,
   movement or cap rule in the way.
4. Filters the claimable territories for the mode (camp: the map edge and any territory claimed or
   carrying a settlement; city founding: the map edge and any territory carrying a settlement), then
   the tiles: water per tile, exploration, settlement capability with the mode's district definition
   (`CampDefinition` for camp, `CityCenterDefinition` for city founding), then the mode's own tile
   gate with the money/influence mask.
5. Scores each surviving tile with the game's own settlement evaluation - vanilla weights, and
   the resource-deposit exclusion vanilla applies - and admits the quota plus every tied tile per
   territory, using a per-territory cache keyed by the frames the rules read, the world claim
   frame included, so a claim, unclaim, owner steal, exploration or patrol change recomputes the
   territory it changed.
6. Writes the admitted tiles into a fixed tail of that mode's evaluation, each entry shown,
   then publishes an immutable pick set for the presentation side. Each refresh also writes
   the cursor slot - the hovered tile's own entry, with the mode gate call's failure flags and its
   money, influence and unit costs - so the game's mouse marker, the red ghost, and the hovered-pin
   rule read the advisor's facts. An unaffordable recommended tile looks exactly like an affordable
   one, star and pin included: the cost failure appears only when the cursor is on it.
7. Renders a star on every published tile and answers the FIMS pin gate from the same set: a
   member shows its pin when the game's own hovered-tile rule allows it, and nothing else shows
   a pin. The game's generic pin filters (unexplored tiles, zero-yield tiles, off-screen
   culling) keep deciding which pins you see, so a member with no FIMS yield, or one outside
   the window, gets its star and no pin.

Writes happen only through the active mode's advisor pipeline, into that mode's evaluation: the
`CreateCamp` and `FoundCity` rows write their own session's evaluation, and an activation scrubs the
tail a previous mode's session left on the shared evaluation. A mode the table has not wired writes
nothing at all.

## Compatibility

- Built against HUMANKIND game assemblies `1.0.31.4851` (GameLibs `1.0.31.4851-r2`).
- Requires BepInEx 5 on the Mono/Unity player.
- A game update can rename the internal members the mod reflects over. When that happens the
  advisor disables itself and logs `Disabled, unresolved game members` instead of crashing.
  A new mod build against the updated game assemblies is then required.

## Troubleshooting

Check the BepInEx log first:

```text
<game>/BepInEx/LogOutput.log
```

Search for `SettlementPlacementAdvisor`.

| Symptom | Check |
|---|---|
| No stars at all | Is the plugin loaded? Is the placement mode a row the mode-policy table wires? |
| A territory has no stars | Tiles must be explored and non-water, and the mode's territory filter must admit the territory |
| No recommendations anywhere on this hover | Nothing displays when the neighborhood has nothing to advise: no stars, no pins, and no vanilla recommendations taking their place |
| `Disabled, unresolved game members` | The game build changed; report the log and game version in an issue |
| Nothing appears until the cursor moves | The mod wires star rendering on the first settlement placement refresh |

Under Proton, early `Awake`-path exceptions appear in the prefix's `Player.log` rather than
`LogOutput.log`.

## Building from source

### Prerequisites

- .NET SDK 10. The helper scripts expect it at `~/.dotnet` unless `DOTNET_ROOT` is set.
- HUMANKIND installed through Steam, or `GAME_DIR` pointing at the install.
- A GitHub token with `read:packages` for the private `Humankind.GameLibs` feed, exported as
  `GAMELIBS_PAT`.

```bash
git clone https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor.git
cd Humankind-SettlementPlacementAdvisor

export GAMELIBS_PAT=$(gh auth token)   # token needs read:packages

scripts/build-advisor.sh               # Release build, deploys to BepInEx/plugins
scripts/test-range.sh                  # land-range harness
scripts/test-noop.sh                   # write-side / pin / tail contract
```

The build resolves the game directory from `-p:GameDir=...`, then `GAME_DIR`, then the standard
Steam Linux location. If the install is not found, the build still succeeds but skips deployment
and prints a warning. BepInEx only loads plugins at process start, so restart the game after a
rebuild.

The test harnesses are headless console applications and do not need a running game. They cover
the real pick computation, the real land-adjacency filter, and the real patch entry points over
synthetic territory graphs.

For the commit standard, game-libs feed setup and release process, see
[CONTRIBUTING.md](CONTRIBUTING.md).

## Contributing

Bug reports and pull requests are welcome. Please include:

- The game version.
- The relevant `LogOutput.log` excerpt.
- Whether the issue happens during settlement placement or city founding.
- A clear description of the expected and actual recommendations.

Commits follow the Conventional Commits format enforced by `scripts/hooks/commit-msg`; see
[CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT. See [LICENSE](LICENSE).

This is an unofficial community mod and is not affiliated with Amplitude Studios or Sega.
