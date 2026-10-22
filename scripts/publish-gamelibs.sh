#!/usr/bin/env bash
# Stage the exact game+BepInEx assemblies into the private GameLibs repo,
# pack Humankind.GameLibs, push to the private GitHub Packages feed.
# Requires GITHUB_TOKEN exported: a PAT with write:packages.
# Version argument = the game build the DLLs came from (e.g. 1.10.25620).
set -euo pipefail

version="${1:?usage: scripts/publish-gamelibs.sh <game-build-version>}"
: "${GITHUB_TOKEN:?export GITHUB_TOKEN=<PAT with write:packages> first}"

GAMEDIR="${GAMEDIR:-/var/home/jabulba/.local/share/Steam/steamapps/common/Humankind}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
libs="${GAMLIBS_REPO:-$(dirname "$here")/Humankind-GameLibs}"

mkdir -p "$libs/dlls"
for d in Amplitude.Mercury.Firstpass Amplitude.Mercury.Data Amplitude Amplitude.Core \
         Amplitude.Framework Assembly-CSharp UnityEngine UnityEngine.CoreModule; do
    cp "$GAMEDIR/Humankind_Data/Managed/$d.dll" "$libs/dlls/"
done
cp "$GAMEDIR/BepInEx/core/BepInEx.dll" "$GAMEDIR/BepInEx/core/0Harmony.dll" "$libs/dlls/"

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

dotnet pack "$libs/GameLibs.csproj" -c Release -p:GameLibsVersion="$version" -o "$libs/pkg"
dotnet nuget push "$libs/pkg/Humankind.GameLibs.$version.nupkg" \
    --source https://nuget.pkg.github.com/Jabulba/index.json \
    --api-key "$GITHUB_TOKEN"
