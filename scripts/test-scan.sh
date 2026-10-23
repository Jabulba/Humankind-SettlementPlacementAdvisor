#!/usr/bin/env bash
# PROTOTYPE (wayfinder ticket 10, throwaway, never merged): perf probe of the cached
# placement-neighborhood scan over synthetic territory graphs. Prints per-call gate costs
# and per-refresh scan cost (cold / cache-hit / claim-triggered recompute).
set -euo pipefail

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run --project "$here/scripts/scancircuit/Scancircuit.csproj" -c Release
