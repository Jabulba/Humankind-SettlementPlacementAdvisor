# Settlement Placement Advisor

[![build](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/actions/workflows/push.yml/badge.svg)](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/actions/workflows/push.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A BepInEx 5 mod for **HUMANKIND** that helps you choose where to place settlements.

While the settlement placement cursor is active, the advisor marks the best tiles with stars and
shows their FIMS yields as pins. The hovered territory gets its top two recommendations; every
other territory within land-adjacency range gets its best one. City founding is left completely
alone.

## Features

- **Recommended-tile stars** for the hovered territory and nearby territories.
- **FIMS pins** so you can compare Food, Industry, Money and Science at a glance.
- **Land-aware range**: sea, cliff, mountain and closed-border edges never extend the
  recommendation range.
- **Uses the game's own evaluation**, including its settlement score weighting.
- **Safe around city founding**: cities keep their vanilla glyphs, pins and cursor, and any
  settlement tail is released when a city session starts.
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
2. Hover over a territory. Recommended tiles in the hovered territory and in nearby territories
   are marked with stars.
3. Check the FIMS pins to compare the candidates.
4. Place the settlement on the tile you prefer. The advisor does not restrict your choice.

The default rules are:

| Territory | Recommendations |
|---|---|
| Hovered territory | Top 2 tiles |
| Each territory in range | Top 1 tile |

A tile gets no star when it is unexplored, already owned, or water. Water is filtered per
tile: sea-flagged territories can host valid camp sites, so the advisor never skips a
territory for its ocean flag. The configured counts are
mandatory rather than a minimum-FIMS filter: tiles below the vanilla minimum-FIMS threshold are
still eligible.

## Configuration

The config file lives at:

```text
BepInEx/config/com.jabulba.settlementplacementadvisor.cfg
```

| Setting | Default | Meaning |
|---|---:|---|
| `TopHovered` | `2` | Recommendations on the hovered territory |
| `TopNeighbour` | `1` | Recommendations on every other territory in range |
| `RangeLevels` | `2` | Land-adjacency range; the hovered territory counts as level 1 |

`RangeLevels = 2` means the hovered territory plus directly adjacent land territories.
`RangeLevels = 3` adds territories adjacent to those. Values below 1 are clamped to 1.

The tile under the mouse ghost remains part of the vanilla placement cursor, so it can add one
visual highlight on top of the configured count.

## How it works

The mod hooks the game's settlement placement evaluation after vanilla refreshes the cursor
position. It then:

1. Walks the hovered territory and its land-adjacent neighbours.
2. Evaluates candidate tiles with the game's own settlement evaluation.
3. Keeps the best candidates per territory, using a frame-aware cache.
4. Writes the selected tiles into a fixed tail of the placement evaluation, then publishes an
   immutable pick set for the presentation side.
5. Renders stars and controls the FIMS pin gate from that pick set.

All writes are restricted to `CreateCamp`. `FoundCity` and `FoundCityWithSettler` share the same
evaluation type, which is why city sessions are a strict no-op: no stars, no pins, no glyph
changes, and no cursor interference.

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
| No stars at all | Is the plugin loaded? Are you in settlement placement, not city founding? |
| A territory has no stars | Tiles must be explored, unowned and non-water; claimed territories are skipped |
| No recommendations anywhere on this hover | Vanilla recommendations take over when every territory in range has nothing to advise |
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
scripts/test-noop.sh                   # city no-op / pin / tail contract
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
