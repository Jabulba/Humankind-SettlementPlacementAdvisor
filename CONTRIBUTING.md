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

## Docs and their roles

| File                          | Written for                     | Role                                                                                                                      |
|---|---|---|
| `README.md`                   | Players installing the mod      | The shipped human document: install, usage, settings, troubleshooting. Player words; a rule summary, not the rule source. |
| `AGENTS.md`                   | AI coding assistants            | The working guide: commands, setup, gotchas, conventions, doc pointers.                                                   |
| `GLOSSARY.md`                 | Everyone working in the code    | Module and domain vocabulary. Name things with these words.                                                               |
| `CONTRIBUTING.md` (this file) | Contributors, humans and agents | Commit standard (format, types, scopes) and per-clone setup.                                                              |
| `RESOURCES.md`                | Anyone needing evidence         | External docs and local anchors for vanilla-behavior facts.                                                               |

Commit every behavior change as one commit containing the code and the contract comments that state the new rule.
