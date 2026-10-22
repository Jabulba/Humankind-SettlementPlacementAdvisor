#!/usr/bin/env bash
# No-op contract harness: asserts the advisor writes nothing on a non-camp placement
# evaluation, keeps its camp tail behaviour, and hands the FIMS pin gate back to vanilla.
# Non-zero exit = a contract violation.
set -euo pipefail

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run --project "$here/scripts/noopcircuit/Noopcircuit.csproj" "$@"
