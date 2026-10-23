#!/usr/bin/env bash
# No-op / shown-set contract harness: asserts the advisor writes nothing on a mode it does not own,
# keeps its settlement tail behaviour, and renders the published pick set as the shown set.
# Non-zero exit = a contract violation.
set -euo pipefail

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run --project "$here/scripts/noopcircuit/Noopcircuit.csproj" "$@"
