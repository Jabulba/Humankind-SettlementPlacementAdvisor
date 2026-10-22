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
`build` build system and dependencies (csproj, Directory.Build.props,
MinVer); `ci` CI (when it exists); `chore` repo plumbing (.gitignore,
LICENSE, hooks); `revert` revert of a prior commit, subject `revert: "<original subject>"`.

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

## Versioning and releases

MinVer derives the assembly version from git tags. Untagged builds report
`0.0.0-alpha.0.<n>` (distance from the latest tag).

Release: tag the releasing commit with an annotated `vX.Y.Z` tag, then
publish the notes with git-cliff (`cargo install git-cliff`):

    git cliff --release-notes <tag>

Paste the output into the GitHub Release body. `CHANGELOG.md` is not kept;
Releases is the changelog.
