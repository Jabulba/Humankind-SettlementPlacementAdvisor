#!/usr/bin/env bash
# Stage game + BepInEx assemblies into the private GameLibs repo, pack
# Humankind.GameLibs, and push it to GitHub Packages. Requires GITHUB_TOKEN
# (PAT with write:packages). Version argument = the game build (e.g. 1.10.25620).
# Knobs: GAME_DIR, GAMLIBS_REPO (staging repo dir), DOTNET_ROOT.
set -euo pipefail

version="${1:?usage: scripts/publish-gamelibs.sh <game-build-version>}"
: "${GITHUB_TOKEN:?export GITHUB_TOKEN=<PAT with write:packages> first}"

# Game install path, same resolution as the csproj and scripts/launch-game.sh:
# GAME_DIR, then the standard Steam Linux location.
GAME_DIR="${GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Humankind}"
# Both consumed subdirectories: staging aborts cleanly BEFORE the first copy
# (a mid-run find failure leaves the previous build's DLLs staged in $libs/dlls).
for d in Humankind_Data/Managed BepInEx/core; do
    [ -d "$GAME_DIR/$d" ] || { echo "Game install not complete: $GAME_DIR/$d missing (export GAME_DIR=...)" >&2; exit 1; }
done
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
libs="${GAMLIBS_REPO:-$(dirname "$here")/Humankind-GameLibs}"
mkdir -p "$libs/dlls"
# Superset staging: every Managed DLL + every BepInEx core DLL. The compile
# closure (RAR from the old HintPath form) spans the full Unity module set,
# Sirenix, MonoMod/Cecil: shipping the whole directory keeps the runtime graph
# complete for headless harness use.
find "$GAME_DIR/Humankind_Data/Managed" -maxdepth 1 -name '*.dll' -exec cp {} "$libs/dlls/" \;
# 0Harmony20 is BepInEx's net20 fallback profile: same types as 0Harmony,
# keeping both yields CS0433 type duplication in every mod compile.
find "$GAME_DIR/BepInEx/core" -maxdepth 1 -name '*.dll' ! -name '0Harmony20.dll' -exec cp {} "$libs/dlls/" \;

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

dotnet pack "$libs/GameLibs.csproj" -c Release -p:GameLibsVersion="$version" -o "$libs/pkg"
dotnet nuget push "$libs/pkg/Humankind.GameLibs.$version.nupkg" \
    --source https://nuget.pkg.github.com/Jabulba/index.json \
    --api-key "$GITHUB_TOKEN"
