#!/usr/bin/env bash
# Build the Settlement Placement Advisor mod and deploy it to the game's BepInEx/plugins folder.
# The csproj copies the DLL into the game directory itself (CopyToPlugins target).
set -euo pipefail

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet build "$here/SettlementPlacementAdvisor.csproj" -c Release "$@"
