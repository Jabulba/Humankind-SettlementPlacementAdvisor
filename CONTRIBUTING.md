# Commit Messages

Strict Conventional Commits: `type(scope)!: subject`. Changelogs are GitHub
Releases, generated from these messages by git-cliff.

## Format

    <type>[(<scope>)][!]: <subject>

- Subject: imperative mood, lowercase, as concise as grammar allows, no
  trailing period. Target 50 characters, hard cap 72.
- Body: blank line after the subject. Required for `fix` commits, in the
  shape Cause / Fix / Ruled out / Verification (see `git log 746e81b`).
  Optional elsewhere. Wrap bodies at 72 characters.
- Breaking changes: append `!` before the colon and add a
  `BREAKING CHANGE: <what and migration>` footer.
- No attribution trailers.

## Types (closed set)

`feat` new player-visible behavior; `fix` bug fix; `refactor` change with no
behavior change; `docs` documentation; `test` tests/harness assertions;
`build` build system and dependencies (csproj, Directory.Build.props, MinVer);
`ci` CI workflows; `chore` repo plumbing (.gitignore, LICENSE, hooks); `revert`
revert of a prior commit, subject `revert: "<original subject>"`.

## Scopes (closed set, optional)

| Scope | Covers |
|---|---|
| `advisor` | `Advisor.cs`, `Patches.cs` |
| `picks` | `PickComputation.cs`, `PickSet.cs` |
| `gameapi` | `GameSurface.cs`, `GameApi.cs` |
| `evaluation` | `EvaluationWriter.cs` |
| `stars` | `StarRenderer.cs` |
| `harness` | `scripts/test-range.sh`, `scripts/rangecircuit/` |

Scopeless: `Plugin.cs` (composition root), docs, `LICENSE`, `.gitignore`,
build plumbing. When a commit touches several scopes, use the dominant one
or split the commit. Vocabulary derives from the Modules section of
GLOSSARY.md; extend the list in this file and the hook together.

## Setup (per clone)

    git config core.hooksPath scripts/hooks
    git config commit.template .gitmessage

`scripts/hooks/commit-msg` rejects: bad type/scope, subject over 72, empty
subject, `fix` without a body.

## Feed and CI

Restore resolves the compile-time game assemblies from the private
`Humankind.GameLibs` package: feed
`https://nuget.pkg.github.com/Jabulba/index.json`, repo `Jabulba/Humankind-GameLibs`.
Authentication: `GITHUB_TOKEN` env var (interpolated in `nuget.config`).

- CI: per-run `GITHUB_TOKEN` (`packages: read`); nothing to set up.
- Local: export a PAT with `read:packages` as `GITHUB_TOKEN` in your shell profile.
- Game update: repack with `scripts/publish-gamelibs.sh <game-build-number>` using a
  PAT with `write:packages`; the package version records the game build the DLLs came from.

Push CI (`.github/workflows/push.yml`): commit-message lint over the pushed range,
Release build, `scripts/test-range.sh`.


## Versioning and releases

MinVer derives the version from git tags; it is stamped into the
`BepInPlugin` attribute at compile time (`GenerateModVersion` target).
Untagged builds report `0.0.0-alpha.0.<n>` (distance from the latest tag).

Release = push an annotated tag `vX.Y.Z`; `.github/workflows/release.yml` builds,
generates the pre-generated cfg (`scripts/gen-cfg.sh`), zips
`{SettlementPlacementAdvisor.dll, LICENSE, README.md, cfg}` and publishes the
GitHub Release with git-cliff notes (pinned v2.14.2 binary). `CHANGELOG.md` is
not kept; Releases is the changelog.
