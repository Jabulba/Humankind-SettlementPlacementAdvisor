# Shape

BepInEx 5 / Harmony mod for HUMANKIND settlement placement.

| Project | Target | Role |
|---|---|---|
| `SettlementPlacementAdvisor.csproj` (root) | net472, C# 7.3 | the plugin |
| `tests/Tests.csproj` | net10.0, xunit | headless suite; compiles the mod sources itself, not the plugin DLL |
| `tools/GameBuild/GameBuild.csproj` | net10.0 | the version reader: the build-time stamp, the cfg writer's decode, the test rows |
| `tools/CfgGen/CfgGen.csproj` | net10.0 | writes the cfg shipped in the release zip, reporting the game build it ran on |

`scripts/` holds the shell entry points and the commit hook; `tools/` holds the .NET tool projects the build and
release flows run, one project per folder as `tools/<Name>/<Name>.csproj` - the shape Humankind-GameLibs uses for
its derive tool too. A C# project does not live under `scripts/`.

`CfgGen.csproj` compiles only the cfg writer's closure: `Program.cs`, `composition/AdvisorConfig.cs`, and the
version reader.

`Humankind-SettlementPlacementAdvisor.sln` adds the decompiled `reference/` trees and both `tools/` projects as
build-disabled projects, so building the solution compiles plugin, tests and CfgGen: the plugin build builds the
version reader itself, in both reference modes. `reference/` is proprietary and never committed.

Plugin sources are the five module folders - `gameapi/`, `pipeline/`, `presentation/`, `composition/`, `logger/` - all
taken by the root csproj's default glob (it only removes `scripts/`, `tests/`, `tools/`, `reference/`). Nothing else is
plugin source.

The module sources and their contract comments are the behavior contract and the only source of truth for the code: read
the comments on the class you are about to change before changing a rule.

## Commands

- `scripts/build-advisor.sh [msbuild args]` - Release build of the plugin; the `CopyToPlugins` target copies the DLL to
  `$GAME_DIR/BepInEx/plugins`. `-p:GameDir=...` wins, then `GAME_DIR`, then the Steam Linux default. Feed references with
  no install: exit 0 plus a loud `NOT deployed` line; `LocalGameLibs`: a missing installation fails the build.
- `dotnet test tests` - headless xunit suite: T1 pipeline rules against a scripted `GameSurface`, T2 Harmony takeover on
  real game types, T3 startup binding against the restored game assemblies. No game launch: the in-game banner stays the
  manual smoke gate.
- Contract-comment check must stay free of CS1570, CS1573, CS1584, and CS1587:
  `dotnet build SettlementPlacementAdvisor.csproj -c Release -p:GenerateDocumentationFile=true -p:NoWarn=CS1591`.
  The project path is required: the repo root holds both the csproj and the sln, so a bare `dotnet build` stops at
  MSB1011.
- `scripts/launch-game.sh [--kill-first]` - Steam launch; prints `READY` when the banner `SPA advisor patches
  applied` appears in this boot's `LogOutput.log`, `FAIL` on timeout, `FAIL` plus both log tails when the game dies
  after the log rewrite. Knobs: the script header lists them. Restart the game after every build: BepInEx loads
  plugins only at the start.
- `scripts/decompile-game.sh [FullyQualifiedTypeName]` - decompile vanilla code under `reference/` (needs the
  `ilspycmd` global tool), then `scripts/gen-explorer.sh` wires those trees into the solution; the decompiler fixes
  to re-apply after a full decompile are listed in `gen-explorer.sh`'s header.
- Logs: grep `[DPS]` in `$GAME_DIR/BepInEx/LogOutput.log`; the advisor's info lines carry the
  `[SPA]` prefix and its failures read `advisor failure:`. `Plugin.Awake` failures appear only in the Proton prefix
  `Player.log`. A load failure also appends `BepInEx/SettlementPlacementAdvisor-load-failure.log` (`SpaLogger.Fatal`).
- Release tooling, not part of a mod change: `scripts/gen-cfg.sh [version] [path]` (the cfg in the zip). The release
  itself is `push.yml`-checked work plus a `vX.Y.Z` tag: `release.yml` builds, tests, generates the cfg, zips the
  DLL with `LICENSE`/`README.md`, and publishes the git-cliff notes as the GitHub Release.
- Scripts assume the SDK at `~/.dotnet` and set `DOTNET_ROOT`/`PATH` themselves.

## References, pins, noise

- Two reference sources, chosen automatically: `GAMELIBS_PAT` set -> the private `Humankind.GameLibs` GitHub Packages feed
  (CI and releases; needs `read:packages`); unset -> `LocalGameLibs` references `$GAME_DIR` in place, so a local build
  needs only the game install.
- Game references are compile-time only; the game ships its own copies, so nothing from the install lands in the output.
  Never copy game DLLs into the tree: `Directory.Build.props` drops `CandidateAssemblyFiles` from `AssemblySearchPaths`,
  so a copy in the tree (e.g. `tools/CfgGen/bin`) cannot silently replace the real game assemblies.
- Pins are exact version brackets, labels only: `GameLibsPin` in `Directory.Build.props` brackets the restore of
  the plugin, the tests and `tools/CfgGen`. The stamp follows the assembly: `GenerateModVersion` reads the
  `Version` attribute of the `Assembly-CSharp.dll` the compiler is given, and a label that disagrees with it
  warns. Bump each pin with the content it pins. Mechanics and contract: `tools/GameBuild/GameBuildReader.cs`,
  the `GenerateModVersion` target, `tests/GameBuildReaderTests`, `tests/GameBuildReaderDecodeTests`.
- Keep `MinVerMinimumMajorMinor` >= 1.0: BepInEx 5 parses the `BepInPlugin` version with `new System.Version(...)`, skips a
  plugin whose version it cannot parse, and MinVer's untagged default (`0.0.0-alpha.0.<n>`) is unparseable.
  `GenerateModVersion` sanitizes the numeric core and fails the build rather than ship an unparseable stamp.
- Expected noise, never "fix" it: `MSB3277` on the plugin build, and `NU1701` wherever a net10.0 project restores the
  net472 game assemblies (feed mode; `tests/Tests.csproj` suppresses it, `tools/CfgGen` does not).

## Working rules

- Class and method contracts are C# XML doc comments on the
  declaration they govern; plain `//` stays for file-scope maps and inline notes. Keep the XML well-formed under the
  documentation build check named in Commands. Contracts that are easy to deduct from code do not get a doc comment.
- A game-build drift is a load failure, not a silent disable: decompile and update the reflected names in `GameApi`,
  never guess. The binding pass leaves through `GameBuildDriftException` naming its row.
- Do not add cross-thread shared state. `PresentationView.Current` is the advisor's only channel: one volatile
  reference swap, built from ints, enums, and flags only. No Unity objects on the simulation thread (`UnityEngine`
  appears in no module source); the overlay rides vanilla's own evaluation array. `SpaRuntime`'s statics are filled
  on the load thread before the patches apply and stay read-only after. No locks, no `Interlocked`, no tasks.
- Writes happen only through the active mode's advisor pipeline, into that mode's evaluation; a mode the mode-policy table
  has not wired writes nothing. A new mode needs a `ModePolicy` row plus a `BindingShape` per gate that row names,
  bound by a `BindingPass` row; a new hot member needs that row too, or the scan pays reflection overhead the per-refresh
  bound forbids.
- Do not reintroduce the vanilla behavior the rewrite dropped: the 25-FIMS floor, the territory-border rule, the
  distance-to-army tie-break, the two-turn reachability flood fill, any scan cap or truncation, affordability filtering,
  vanilla's best-camp bookkeeping (`BestCampPlacementTileIndexes` keeps its `-1` stamps), vanilla's pin quota and
  `FimsTier` recommendation flags, or an auto-disable on unresolved members.
- Never add a runtime cap, budget or affordability prefilter to meet the 5 ms per refresh bound stated on `Advisor`. The
  T1 counters bound the scan's reach, so a rising counter is that bound's drift signal.
- A behavior change edits the code and the contract comments stating it in one commit, then the player-facing summary in
  `README.md` in the same commit.

## Tests

- Expected pick sets are literals derived from the contract comments, never read back from the code under test, and the
  `ScriptSurface` counters (`AdjacencyCalls`, `TileGateCalls`, `ArmyGateCalls`, `EvaluateTileCalls`, `FactsReads`) keep
  the rows a scan cannot answer pinned.
- The suite runs serial: every test shares process-global statics (`PresentationView.Current`, `SpaLogger`, Harmony
  patches), so `tests/Probe.cs` disables test parallelization for the assembly.
- Patching in the test host needs `Lib.Harmony` 2.4.2: the `0Harmony` shipped by the game and by GameLibs self-destructs
  in `AccessTools`' static initializer on .NET 10, so `Tests.csproj` drops every `0Harmony` reference in its place.
- The per-tier headless limits are stated in the test headers; what they leave unproven rests on the decompiled
  anchors `RESOURCES.md` names and on the manual in-game smoke check (`scripts/launch-game.sh`: the banner, and the
  `DeactivateCursor` postfix on a real deactivation).

## Docs

- Module sources and their contract comments - the behavior contract: rules, seams, and the performance bound, stated on
  the class they govern. There is no separate architecture document.
- `README.md` - player-facing install, usage, settings, troubleshooting (shipped in the zip).
- `GLOSSARY.md` - module and domain vocabulary; name things with these words.
- `CONTRIBUTING.md` - commit standard, types and scopes, hook behavior, per-clone setup.
- `RESOURCES.md` - external docs and local anchors for vanilla-behavior facts.

## Commits

Strict Conventional Commits (`type(scope)!: subject`), enforced by `scripts/hooks/commit-msg` and by push CI; the
format rules live in `CONTRIBUTING.md`.

Push CI runs commit lint, the Release plugin build and `dotnet test tests`. Its filters run for pushes to `main`
and for PRs opened against `main`.
