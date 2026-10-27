# Commit Messages

Strict Conventional Commits: `type(scope)!: subject`. Changelogs are GitHub Releases, generated from these messages by
git-cliff.

## Format

    <type>[(<scope>)][!]: <subject>

- Subject: imperative mood, lowercase, as concise as grammar allows, no trailing period. Target 50 characters, hard cap
  72.
- Body: blank line after the subject. Required for `fix` commits, in the shape Cause / Fix / Ruled out / Verification
  (see `git log 746e81b`). Optional elsewhere. Wrap bodies at 72 characters.
- Breaking changes: append `!` before the colon and add a
  `BREAKING CHANGE: <what and migration>` footer.
- No attribution trailers.

## Types (closed set)

- `feat` new player-visible behavior;
- `fix` bug fix;
- `refactor` change with no behavior change;
- `docs` documentation;
- `test` tests and test-project changes;
- `build` build system and dependencies (csproj, Directory.Build.props, MinVer);
- `ci` CI workflows;
- `chore` repo plumbing (.gitignore, LICENSE, hooks);
- `revert` revert of a prior commit, subject `revert: "<original subject>"`.

## Scopes (closed set, optional)

| Scope          | Covers                                                                                |
|---|----------------------------------------------------------------------------------------------------------|
| `logger`       | `logger/`                                                                             |
| `composition`  | `composition/` (composition root, config, load gate)                                  |
| `gameapi`      | `gameapi/` (game surface, adapter, binding pass, mode-policy table, drift)            |
| `presentation` | `presentation/` (the cross-thread presentation view)                                  |
| `pipeline`     | `pipeline/` (takeover patches, scan pass, pick computation, evaluation writer)        |
| `tests`        | test projects and harnesses                                                           |
| `build`        | build system, dependencies, CI workflows, release tooling, commit tooling             |
| `docs`         | `README.md`, `LICENSE`, `GLOSSARY.md`, `CONTRIBUTING.md`, `RESOURCES.md`, `AGENTS.md` |
| `repo`         | repo plumbing: `.gitignore`, `.gitmessage`, `.editorconfig`, `scripts/hooks/`         |

Commits that span several scopes, or touch none of them, stay scopeless.

## Setup (per clone)

    git config core.hooksPath scripts/hooks
    git config commit.template .gitmessage
    scripts/decompile-game.sh (optional)
    scripts/gen-explorer.sh (optional, requires decompile-game.sh)

`scripts/hooks/commit-msg` rejects: bad type/scope, subject over 72, empty subject, `fix` without a body, attribution
trailers.

The last pair is optional for building the mod (the csprojs build without it) but required per clone for the IDE:
the committed solution lists the decompiled projects under a build-disabled `reference` folder, and those gitignored
project files only exist after `decompile-game.sh` (needs `ilspycmd`) + `gen-explorer.sh` have run - until then the
IDE shows the two entries as missing.

## Feed and CI

Restore resolves compile-time game assemblies from the private `Humankind.GameLibs` package: feed
`https://nuget.pkg.github.com/Jabulba/index.json`, repo `Jabulba/Humankind-GameLibs`. Package content: every
`Humankind_Data/Managed` DLL + `BepInEx/core` DLLs (minus `0Harmony20`, which duplicates 0Harmony types and breaks
compiles). Version = game build number, `-rN` = package revision. Pins are exact brackets
(`Version="[1.0.31.4851-r2]"`): bare pins float upward in NuGet semantics. Authentication: `GAMELIBS_PAT` env var
(interpolated in `nuget.config`).

- CI: repo secret `GAMELIBS_PAT` (PAT with `read:packages`), exported as env in both workflows. Public-repo
  `GITHUB_TOKEN` gets 403 on personal-account private packages (upstream limitation).
- Local: export a PAT with `read:packages` as `GAMELIBS_PAT` in your shell profile (`export GAMELIBS_PAT=$(gh auth token)`).
- Machine-only folder feed: add `<path-to>/Humankind-GameLibs/pkg` to `~/.nuget/NuGet/NuGet.Config`, not to the repo's
  `nuget.config` (NuGet hard-errors on a missing source in CI, and the repo config has no `<clear/>` so machine sources
  merge in). The folder feed makes local restore instant; the GitHub Packages index needs minutes to propagate after a
  publish, so a CI restore right after one can fail with NU1102: rerun the workflow, do not republish.
- Game update: repack with `scripts/publish-gamelibs.sh <game-build-number>` using a PAT with `write:packages`, then
  bump the exact pins in all five pinning csproj files (`SettlementPlacementAdvisor.csproj` and the `cfggen`,
  `noopcircuit`, `rangecircuit`, `scancircuit` projects under `scripts/`).

Push CI (`.github/workflows/push.yml`): commit-message lint over the pushed range, Release build, and all three
harnesses (`scripts/test-range.sh`, `scripts/test-noop.sh`, `scripts/test-scan.sh`).

## Versioning and releases

MinVer derives the version from git tags; it is stamped into the `BepInPlugin` attribute at compile time
(`GenerateModVersion` target). Untagged builds compute `1.0.0-alpha.0.<n>` (`<n>` is the distance from the latest tag).

Release = push an annotated tag `vX.Y.Z`; `.github/workflows/release.yml` builds, generates the pre-generated cfg
(`scripts/gen-cfg.sh`), zips `{SettlementPlacementAdvisor.dll, LICENSE, README.md, cfg}` and publishes the GitHub
Release with git-cliff notes (pinned v2.14.2 binary). `CHANGELOG.md` is not kept; Releases is the changelog.
