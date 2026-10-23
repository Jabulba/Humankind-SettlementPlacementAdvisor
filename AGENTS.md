# AGENTS.md

BepInEx 5 / Harmony mod for HUMANKIND settlement placement. One plugin project at the repo
root (`net472`, C# 7.3). `README.md` is the behavior contract, `GLOSSARY.md` the module/domain
vocabulary, `CONTRIBUTING.md` the commit/release standard. Read those before changing rules.

## Commands

- `scripts/build-advisor.sh [msbuild args]` — Release build, then deploys the DLL to
  `$GAME_DIR/BepInEx/plugins` automatically (`CopyToPlugins`). Pass `-p:GameDir=...` or export
  `GAME_DIR`; if the install is missing the build *succeeds* but logs `NOT deployed`.
- `scripts/test-range.sh` — placement-neighborhood harness. `scripts/test-noop.sh` — the
  write-side, pin and tail contract.
  These are the whole test suite: plain console apps, no test framework, so `dotnet test` runs
  nothing. They have no case filter; running one runs all its cases (fast).
- `scripts/launch-game.sh [--kill-first]` — Steam launch, waits for `Ready: advisor patches
  active` in this boot's log. Restart the game after every rebuild; BepInEx loads only at start.
- `scripts/decompile-game.sh [FullyQualifiedTypeName]` — ILSpy decompile under `reference/`
  (needs `ilspycmd`). Use it for vanilla-behavior facts; `reference/` is proprietary and must
  never be committed.
- `scripts/gen-explorer.sh` — regenerate `reference/Explorer.sln` (IDE project over the
  decompiled trees: completion + navigation). Run after every full decompile; a handful
  of decompiler-artifact fixes live in the decompiled files and are listed in that
  script's header for re-application.
- `scripts/gen-cfg.sh` — release cfg generation only; `scripts/publish-gamelibs.sh <build>` —
  repack game assemblies after a game update.
- Runtime log: grep `SettlementPlacementAdvisor` in `$GAME_DIR/BepInEx/LogOutput.log`.
  `Plugin.Awake` exceptions appear only in the Proton prefix `Player.log`.

Scripts assume the SDK at `~/.dotnet` and set `DOTNET_ROOT`/`PATH` themselves.

## Setup / dependencies

- Restore pulls compile-time game assemblies from the private `Humankind.GameLibs` feed.
  `GAMELIBS_PAT` must have `read:packages`. The pin is exact (`[1.0.31.4851-r2]`); bump it in
  **all four** csproj files (root, `rangecircuit`, `noopcircuit`, `cfggen`) when updating.
- Per clone: `git config core.hooksPath scripts/hooks` and
  `git config commit.template .gitmessage`.
- Expected noise: GameLibs produces `MSB3277` on the plugin build and `NU1701` in the net10
  harnesses. Do not "fix" these.

## Architecture map

Simulation thread: `Patches.cs` → `Advisor` → `PickComputation` → `EvaluationWriter` →
`PickSet`. Presentation thread: `StarRenderer` and the FIMS-pin postfix read the captured
`PickSet`. `Plugin.cs` is only composition; `GameSurface` is the rules' seam and `GameApi` its
reflection adapter.

- **Writes happen only through the active mode's advisor pipeline, into that mode's evaluation.**
  The mode-policy table wires `CreateCamp` and `FoundCity`; the three modes share
  `CampPlacementEvaluation`, so a mode the table has not wired writes nothing and an activation
  scrubs the tail the previous mode's session left. The write-side harness enforces this.
- `EvaluationWriter` owns a tail window in the shared `ValidTiles` array, sized from config at the
  activation and grown on demand. Write pick slots in place, barrier, then raise `ValidTileCount`;
  never move vanilla entries, never sweep the array, and zero only the slots a refresh releases. A
  settlement tail left after a mode switch must be scrubbed over the whole region ever reserved, not
  merely counted down.
- `PickSet.Current` is the only cross-thread channel: immutable, volatile, version-gated, with
  entering/leaving diffs. Do not add other shared mutable state or touch Unity objects on the
  simulation thread.
- `GameApi.Resolve()` checks reflected member names at startup. If the log says
  `Disabled, unresolved game members`, the game build drifted: decompile and update those names
  rather than guessing. Territory facts fail closed; see GLOSSARY for the refresh/pick contracts.
- Source files at the repo root are globbed by the plugin automatically, but each harness uses
  explicit `<Compile Include>` lists (and substitutes `PluginShim.cs` for `Plugin.cs`). Add new
  production files to the relevant harness csproj or they silently will not be tested.
- Harnesses target `net10.0` but keep `LangVersion 7.3` to match the plugin. Do not use newer C#
  syntax; nullable is disabled.
- Never copy game DLLs into the tree. `Directory.Build.props` deliberately limits reference
  resolution to HintPaths because in-tree copies can replace the real assemblies.

## Commits and releases

Strict Conventional Commits (`type(scope)!: subject`), enforced by the hook and by push CI.
Types/scopes are closed sets; `fix` requires a Cause/Fix/Ruled out/Verification body; max 72
chars; no attribution trailers. Pushing `main`/opening a PR runs commit lint, the Release build,
and both harnesses. Release = annotated `vX.Y.Z` tag; MinVer stamps the version, git-cliff
generates notes. See `CONTRIBUTING.md` for the exact scopes and release steps.
