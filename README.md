# Humankind — Settlement Placement Advisor

Standalone BepInEx mod: while the camp (outpost) placement cursor is active, shows recommended-tile stars for the
hovered territory (top 2) and for every territory in the land-adjacency range (top 1 each), with FIMS pins, mandatory
counts that ignore the vanilla minimum-FIMS gate, exploration filtering, and no stars on unexplored/owned/ocean tiles.

## Layout

- repo root — plugin source (`com.jabulba.settlementplacementadvisor`, net472)
- `scripts/build-advisor.sh` — build; copies the DLL into the game's `BepInEx/plugins`
- `scripts/launch-game.sh` — launch and boot detection
- `scripts/test-range.sh` — range-rule harness (`scripts/rangecircuit`), non-zero exit on a range violation
- `scripts/decompile-game.sh` — decompile cache under `reference/` (gitignored, never committed); `<FullyQualifiedTypeName>` prints one type
- `Directory.Build.props` — assembly references resolve from HintPaths and the target-framework directory only, never from copies inside the tree
- `CONTRIBUTING.md` — commit standard, game-libs feed setup, versioning, releases
- `.github/workflows/` — push CI (lint + build + harness); tag CI (release zip + git-cliff notes)
- `nuget.config` — private `Humankind.GameLibs` feed: compile-time game assemblies
- `scripts/publish-gamelibs.sh` — repack the private game-libs package after a game update

## Build & deploy

    scripts/build-advisor.sh

Requires the dotnet SDK at `~/.dotnet` (script sets `DOTNET_ROOT`/`PATH`). Game install path is set by `GameDir` in the
csproj (override with `-p:GameDir=...`). BepInEx only loads plugins at process start: after a rebuild, restart the game.
Per clone once: `git config core.hooksPath scripts/hooks` and `git config commit.template .gitmessage`.
Restore resolves compile-time game assemblies from the private GameLibs feed: export `GITHUB_TOKEN=<read:packages PAT>` (CONTRIBUTING.md).

Config: `BepInEx/config/com.jabulba.settlementplacementadvisor.cfg` (`TopHovered=2`, `TopNeighbour=1`,
`RangeLevels=2`: center counts as level 1, so 2 = hovered + adjacent, 3 adds adjacent-to-adjacent. Land adjacency only:
sea, cliff and mountain edges never count as a range step.

## Test

    scripts/test-range.sh

Range-rule harness (`scripts/rangecircuit`): drives the real pick computation and the real land filter over synthetic
territory graphs (land chain, land-sea-land, bay fan, cliff edge) and exits non-zero when a pick comes from a
territory outside the land-adjacency range.

Logs: grep `SettlementPlacementAdvisor` in `~/.local/share/Steam/steamapps/common/Humankind/BepInEx/LogOutput.log`;
Awake-path exceptions appear only in the Proton prefix `Player.log`.
