# Humankind — Settlement Placement Advisor

Standalone BepInEx mod. While the camp (outpost) placement cursor is active, it shows
recommended-tile stars for the hovered territory (top 2) and every territory in the land-adjacency
range (top 1 each), with FIMS pins. Counts are mandatory and ignore the vanilla minimum-FIMS gate;
unexplored, owned and ocean tiles get no stars.

Only camp placement is affected. City founding shares the game's placement evaluation, so the
advisor is a strict no-op there: no stars, no pins, vanilla glyphs and cursor untouched. A tail
reserved by an earlier camp session is released when a city session starts.

## Layout

- repo root — plugin source (`com.jabulba.settlementplacementadvisor`, net472)
- `scripts/build-advisor.sh` — build; copies the DLL into the game's `BepInEx/plugins`
- `scripts/launch-game.sh` — launch and boot detection
- `scripts/test-range.sh` — range-rule harness (`scripts/rangecircuit`)
- `scripts/test-noop.sh` — no-op contract harness (`scripts/noopcircuit`)
- `scripts/decompile-game.sh` — local decompile cache under `reference/` (gitignored);
  `<FullyQualifiedTypeName>` prints one type
- `Directory.Build.props` — references resolve from HintPaths and the target-framework directory
  only, never from copies inside the tree
- `CONTRIBUTING.md` — commit standard, game-libs feed setup, versioning, releases
- `.github/workflows/` — push CI (lint + build + harness); tag CI (release zip + git-cliff notes)
- `nuget.config` — private `Humankind.GameLibs` feed: compile-time game assemblies
- `scripts/publish-gamelibs.sh` — repack the game-libs package after a game update

## Build & deploy

    scripts/build-advisor.sh

Requires the dotnet SDK at `~/.dotnet` (the script sets `DOTNET_ROOT`/`PATH`). The game install
path resolves per machine: `-p:GameDir=...`, then `GAME_DIR`, then
`~/.local/share/Steam/steamapps/common/Humankind`; without it the build reports and skips
deployment. BepInEx loads plugins only at process start, so restart the game after a rebuild.

Per clone once: `git config core.hooksPath scripts/hooks` and
`git config commit.template .gitmessage`. Restore needs `GAMELIBS_PAT` (a `read:packages` PAT;
see CONTRIBUTING.md).

Config: `BepInEx/config/com.jabulba.settlementplacementadvisor.cfg` (`TopHovered=2`,
`TopNeighbour=1`, `RangeLevels=2`). `RangeLevels` counts the center as level 1, so 2 = hovered +
adjacent and 3 adds adjacent-to-adjacent. Land adjacency only: sea, cliff and mountain edges never
count as a range step.

## Test

    scripts/test-range.sh

Drives the real pick computation and land filter over synthetic territory graphs (land chain,
land-sea-land, bay fan, cliff edge). Non-zero exit when a pick comes from outside the
land-adjacency range.

    scripts/test-noop.sh

Replays the real patch entry points on city and camp evaluations. Non-zero exit if the advisor
touches a city evaluation, loses its camp tail behaviour, leaves the FIMS pin gate claimed while
inactive, or leaks camp tail slots into a city session.

Logs: grep `SettlementPlacementAdvisor` in
`~/.local/share/Steam/steamapps/common/Humankind/BepInEx/LogOutput.log`. Awake-path exceptions
appear only in the Proton prefix `Player.log`.
