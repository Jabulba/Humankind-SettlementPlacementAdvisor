#!/usr/bin/env bash
# Write-side contract harness: asserts the advisor writes through the active mode's pipeline into
# that mode's evaluation only, scrubs the tail an activation inherits, keeps the slot, barrier and
# zero-fill invariants, and renders the published pick set as the shown set.
# Non-zero exit = a contract violation.
set -euo pipefail

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run --project "$here/scripts/noopcircuit/Noopcircuit.csproj" "$@"
