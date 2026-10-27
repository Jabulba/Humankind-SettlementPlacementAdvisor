#!/usr/bin/env bash
# Generate the pre-generated BepInEx config shipped in the release zip (release.yml runs this on a
# vX.Y.Z tag; cfggen prints the game build it wrote with and stops if it cannot read it.
# Production binds, no copies: tools/CfgGen.
# Usage: scripts/gen-cfg.sh [version] [output-path]
set -euo pipefail

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(dirname "$here")"
out="${2:-$repo/dist/com.jabulba.settlementplacementadvisor.cfg}"
mkdir -p "$(dirname "$out")"
dotnet run --project "$repo/tools/CfgGen/CfgGen.csproj" -c Release -- "${1:-0.0.0-dev}" "$out"
