# Settlement Placement Advisor

[![build](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/actions/workflows/push.yml/badge.svg)](https://github.com/Jabulba/Humankind-SettlementPlacementAdvisor/actions/workflows/push.yml)
[![license: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A BepInEx 5 mod for **HUMANKIND** that helps you choose where to place settlements.

While the settlement placement cursor is active, the advisor marks the best tiles with stars and
shows their FIMS yields as pins. The recommendation set is the placement neighborhood: the hovered
territory (level 1) plus `RangeLevels - 1` expansions beyond it, over every edge - sea, cliff,
mountain and closed-border included - and every territory in it gets its own recommendations. The hovered
territory gets its admission quota, every other territory in the placement neighborhood gets that
quota plus every tile tied with the last admitted score, and all three settlement modes - camp
placement, city founding, and city founding with a settler - are advised the same way, each through
its own row of the mode-policy table.

## Features

- **Recommended-tile stars** for every territory in the placement neighborhood.
- **Stars and pins are one set**: every recommended tile gets a star, and its FIMS pin shows
  when the game's own pin rules allow it.
- **Every adjacency edge is a hop**: sea, cliff, mountain and closed-border edges extend the
  placement neighborhood exactly like a land edge.
- **Uses the game's own evaluation**, including its settlement score weighting.
- **All three settlement modes are advised**: camp placement, city founding and city founding
  with a settler each run the pipeline on their own session's evaluation, and a settlement tail
  left by one mode's session is scrubbed when a session switches mode.
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

1. Select an army and start **settlement placement**, **city founding**, or **city founding with
   a settler**.
2. Hover over a territory. Recommended tiles are marked with stars.
3. Check the FIMS pins to compare the candidates.
4. Place the settlement on the tile you prefer. The advisor does not restrict your choice.

The default rules are:

| Territory | Admission quota | Recommendations |
|---|---:|---|
| Hovered territory | 2 | The top 2 tiles, plus every tile tied with the last admitted score |
| Each other territory in the neighborhood | 1 | The top tile, plus every tile tied with the last admitted score |

Every tile tied with the last recommended score is shown as well, so `TopHovered = 2` can show 3
stars, and 4 when three tiles tie at rank 2.

A tile gets no star when it is unexplored, water, not a valid settlement site for the mode, or its
territory is one the mode's filter claims nothing on: the edge of the world in all three wired
modes, plus a claimed territory for camp placement, and a settled territory for city founding
except the camp a settler replaces. Water is filtered per tile: sea-flagged territories can
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

| Knob | Type | Default | Bounds | Semantics |
|---|---|---:|---|---|
| `TopHovered` | int | `2` | >= 1, clamped | Per-territory admission quota on the hovered territory: mandatory minimum count plus the full top tie set |
| `TopNeighbour` | int | `1` | >= 1, clamped | Same quota semantics on every other territory in the placement neighborhood; 0 not allowed, no hovered-only display |
| `RangeLevels` | int | `2` | >= 1, no upper bound | Placement-neighborhood hop count over all adjacency types; the hovered territory counts as level 1 |

The config file carries these help strings:

- `TopHovered`: "Admission quota on the hovered territory: this many tiles minimum, plus every
  tile tied with the last admitted score."
- `TopNeighbour`: "Admission quota on every other territory in the placement neighborhood: this
  many tiles minimum, plus every tile tied with the last admitted score."
- `RangeLevels`: "Placement-neighborhood hops from the hovered territory, the hovered territory
  counts as level 1: 3 = hovered + adjacent + adjacent-to-adjacent. Every adjacency edge counts as
  a hop: sea, cliff, mountain and closed-border edges included."

All three settings are integers with a minimum of 1: values below 1 are clamped to 1, and
`RangeLevels` has no upper bound. There are no other knobs: no score floor, no tie cap, no mode
toggle and no master enable switch. Disabling the mod is the mod loader's job.

The tile under the mouse ghost is the advisor's own cursor slot: the mod writes it on every refresh
of a mode it has taken over, so the game's own hover highlight rule is skipped there, and a mode the
mod has not taken over keeps the vanilla cursor and its hover highlight untouched.

## How it works

For a placement mode the mode-policy table wires - camp placement, city founding, and city founding
with a settler - the mod takes over the game's placement-snapshot sync
(`SettlementPlacementCursorSnapshot.EvaluatePlacement`): the advisor's own pipeline answers it
and vanilla's full settlement evaluation is never called from that snapshot. It then:

1. Dispatches on the stamped placement mode: a wired row runs the pipeline, and a mode the table
   has not wired shows nothing and leaves its evaluation to vanilla.
2. Asks the mode's army-level gate - the shared army-state rows the game's own gates run (decoy,
   battle lock, army lock) and then the mode's own army rows, accumulating one flag set, because
   the camp and settler army overloads never run the shared rows; an army that cannot build a camp,
   found a city, or settle with its settler advises nothing.
3. Scans the placement neighborhood: the hovered territory plus `RangeLevels - 1` expansions over
   every adjacency edge, every tile of every territory in it, with no reachability, zone-of-control,
   movement or cap rule in the way. An edge set the game cannot answer degrades the whole pass to
   the hovered territory's recommendations alone and logs the surface failure.
4. Filters the claimable territories for the mode (camp: the map edge and any territory claimed or
   carrying a settlement; city founding: the map edge and any territory carrying a settlement;
   settler: the map edge and any settled territory except the camp or pre-camp the settler
   replaces), then the tiles: water per tile, exploration, settlement capability with the mode's
   district definition (`CampDefinition` for camp, `CityCenterDefinition` for city founding and
   for city founding with a settler), then the mode's own tile gate with the money/influence mask.
5. Scores each surviving tile with the game's own settlement evaluation - vanilla weights, and
   the resource-deposit exclusion vanilla applies - and admits the quota plus every tied tile per
   territory, using a per-territory cache keyed by the frames the rules read, the world claim
   frame included, so a claim, unclaim, owner steal, exploration or patrol change recomputes the
   territory it changed. A mouse move costs cache hits plus at most the newly entered territory.
6. Writes the admitted tiles into the tail of that mode's evaluation - a window sized at the
   activation from your `RangeLevels` and quotas, tie headroom included, and grown when a published
   set needs more slots - each entry shown,
   then publishes it on the presentation view - the advisor's only presentation output: the pick
   set, the mode it was published for, and the cursor slot's gate answer. Each refresh also writes
   the cursor slot - the hovered tile's own entry, with the mode gate call's failure flags and its
   money, influence and unit costs - so the game's mouse marker, the red ghost, and the hovered-pin
   rule read the advisor's facts. An unaffordable recommended tile looks exactly like an
   affordable one, star and pin included: the cost failure appears only when the cursor is on it.
7. Renders a star on every published tile and answers the FIMS pin gate from the same published
   view: a member shows its pin when the game's own hovered-tile rule, read off the published
   cursor-slot answer, allows it, and nothing else shows a pin; a mode the mod never published
   for keeps vanilla's own gate answer. The game's generic pin filters (unexplored tiles,
   zero-yield tiles, off-screen culling) keep deciding which pins you see, so a member with no
   FIMS yield, or one outside the window, gets its star and no pin.

Writes happen only through the active mode's advisor pipeline, into that mode's evaluation: the
`CreateCamp`, `FoundCity` and `FoundCityWithSettler` rows write their own session's evaluation, and
an activation scrubs the whole region a previous mode's session reserved on the shared evaluation,
so no settlement entry survives a mode switch as a phantom recommendation. A mode the table has not
wired writes nothing at all.

The advisor promises nothing about the legality of an advised tile: the game's own placement
validators stay in charge, and an advised tile can still be rejected when you give the order (army
overload, costs, a foreign army on arrival, state drift). Advice is a recommendation, never a
guarantee.

## Compatibility

- Built against HUMANKIND game assemblies `1.0.31.4851` (GameLibs `1.0.31.4851-r2`).
- Requires BepInEx 5 on the Mono/Unity player.
- The startup-bound member table is checked in full at load: it covers the gate members and the
  district definitions, the frame counters, the vision bits, the army's empire index, the
  snapshot's cursor fields, and every Harmony patch target; the write side and the patches resolve
  their targets from that table instead of re-spelling names.
- A game update can rename the internal members the mod reflects over. When that happens the mod
  refuses to load: the failure list is logged and the game closes. The line lands in `LogOutput.log`,
  in the prefix's `Player.log`, and in `BepInEx/SettlementPlacementAdvisor-load-failure.log`. A new
  mod build against the updated game assemblies is then required.

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
| `Load failure, unresolved game members` | The game build renamed a member the mod reflects over: the game closes at startup, and the failure line names the members |
| Nothing appears until the cursor moves | The mod wires star rendering on the first settlement placement refresh |

Under Proton, early `Awake`-path exceptions appear in the prefix's `Player.log` rather than
`LogOutput.log`. A load failure is written to all three: `LogOutput.log`, the prefix's `Player.log`,
and `BepInEx/SettlementPlacementAdvisor-load-failure.log`.

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
scripts/test-range.sh                  # placement-neighborhood harness
scripts/test-noop.sh                   # write-side / pin / tail contract
scripts/test-scan.sh                   # scan-cost harness (the wide-scan performance bound)
```

The build resolves the game directory from `-p:GameDir=...`, then `GAME_DIR`, then the standard
Steam Linux location. If the install is not found, the build still succeeds but skips deployment
and prints a warning. BepInEx only loads plugins at process start, so restart the game after a
rebuild.

The test harnesses are headless console applications and do not need a running game. They cover
the real pick computation, the real placement-neighborhood scan over every adjacency edge, the real
patch entry points, and the scan cost, over synthetic territory graphs.

For the commit standard, game-libs feed setup and release process, see
[CONTRIBUTING.md](CONTRIBUTING.md).

## Contributing

Bug reports and pull requests are welcome. Please include:

- The game version.
- The relevant `LogOutput.log` excerpt.
- The placement mode: settlement placement, city founding, or city founding with a settler.
- A clear description of the expected and actual recommendations.

Commits follow the Conventional Commits format enforced by `scripts/hooks/commit-msg`; see
[CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT. See [LICENSE](LICENSE).

This is an unofficial community mod and is not affiliated with Amplitude Studios or Sega.
