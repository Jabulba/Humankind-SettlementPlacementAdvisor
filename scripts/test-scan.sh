#!/usr/bin/env bash
# Scan-cost harness: drives the real PickComputation placement-neighborhood scan over synthetic
# territory graphs and reports the per-refresh cost (cold pass, cache-hit refresh, claim-triggered
# recompute) against the SPEC section 9 bound (wide scan: RangeLevels 3, ~1000 gate calls, ~4300
# tiles, within 5 ms per refresh with an explicit 4x Unity Mono inflation allowance).
# Non-zero exit = a scan-cost row over the bound, a wide-scan case off its ~1000-gate / ~4300-tile
# target, or a cap/budget/affordability prefilter that survived the scan decision.
set -euo pipefail

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run --project "$here/scripts/scancircuit/Scancircuit.csproj"
