#!/usr/bin/env bash
# Generate the pre-generated BepInEx config shipped in the release zip
# (CONTRIBUTING.md release flow). Production binds, no copies: scripts/cfggen.
# Usage: scripts/gen-cfg.sh [version] [output-path]
set -euo pipefail

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(dirname "$here")"
out="${2:-$repo/dist/com.jabulba.settlementplacementadvisor.cfg}"
mkdir -p "$(dirname "$out")"
dotnet run --project "$here/cfggen/CfgGen.csproj" -c Release -- "${1:-0.0.0-dev}" "$out"
