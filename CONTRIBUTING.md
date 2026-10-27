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

## Versioning and releases

MinVer derives the version from git tags; it is stamped into the `BepInPlugin` attribute at compile time
(`GenerateModVersion` target). Untagged builds compute `1.0.0-alpha.0.<n>` (`<n>` is the distance from the latest tag).

Release = push an annotated tag `vX.Y.Z`; `.github/workflows/release.yml` builds, generates the pre-generated cfg
(`scripts/gen-cfg.sh`), zips `{SettlementPlacementAdvisor.dll, LICENSE, README.md, cfg}` and publishes the GitHub
Release with git-cliff notes (pinned v2.14.2 binary). `CHANGELOG.md` is not kept; Releases is the changelog.
