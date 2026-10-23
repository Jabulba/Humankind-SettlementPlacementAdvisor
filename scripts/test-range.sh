#!/usr/bin/env bash
# Range-rule harness: drives the real PickComputation + the real placement-neighborhood relation
# over synthetic territory graphs. Non-zero exit = a pick outside the placement neighborhood, a
# budget/cap/truncation rule that survived the scan decision, or an unfiltered adjacency relation.
set -euo pipefail

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run --project "$here/scripts/rangecircuit/Rangecircuit.csproj"
