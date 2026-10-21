# Humankind — Camp Placement Advisor

Standalone BepInEx mod: while the camp (outpost) placement cursor is active, shows recommended-tile stars for the
hovered territory (top 2) and for every territory in the adjacency range (top 1 each), with FIMS pins, mandatory
counts that ignore the vanilla minimum-FIMS gate, exploration filtering, and no stars on unexplored/owned/ocean tiles.

## Layout

- repo root — plugin source (`com.jabulba.campplacementadvisor`, net472)
- `scripts/build-advisor.sh` — build; copies the DLL into the game's `BepInEx/plugins`
- `scripts/launch-game.sh` — launch and boot detection

## Build & deploy

    scripts/build-advisor.sh

Requires the dotnet SDK at `~/.dotnet` (script sets `DOTNET_ROOT`/`PATH`). Game install path is set by `GameDir` in the
csproj (override with `-p:GameDir=...`). BepInEx only loads plugins at process start: after a rebuild, restart the game.

Config: `BepInEx/config/com.jabulba.campplacementadvisor.cfg` (`Enabled`, `TopHovered=2`, `TopNeighbour=1`,
`RangeLevels=2`: center counts as level 1, so 2 = hovered + adjacent, 3 adds adjacent-to-adjacent).

Logs: grep `CampPlacementAdvisor` in `~/.local/share/Steam/steamapps/common/Humankind/BepInEx/LogOutput.log`;
Awake-path exceptions appear only in the Proton prefix `Player.log`.
