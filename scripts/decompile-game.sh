#!/usr/bin/env bash
# Decompile HUMANKIND's managed (.NET) assemblies into ./reference for offline reading.
#
# Decompiled game code must NEVER be committed: everything under reference/ is
# gitignored (see .gitignore). This is a local convenience cache so we don't have
# to re-run ILSpy on every investigation.
#
# Requirements:
#   - dotnet SDK in ~/.dotnet with ilspycmd installed as a global tool
#       dotnet tool install -g ilspycmd
#   - Humankind installed via Steam
#
# Usage:
#   scripts/decompile-game.sh                    # full decompile + type lists
#   scripts/decompile-game.sh <FullyQualifiedTypeName>
#                                                # print a single type to stdout
#
# Overridable env: GAME_MANAGED, OUT (cache root for the managed copy), OUTGAME_DIR
# (default: the sibling outgame dir of OUT), DOTNET_ROOT. Keep both inside the
# gitignored reference/ tree: decompiled game code must stay out of git.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAME_MANAGED="${GAME_MANAGED:-$HOME/.local/share/Steam/steamapps/common/Humankind/Humankind_Data/Managed}"
OUT="${OUT:-$REPO_ROOT/reference/managed}"
OUTGAME_DIR="${OUTGAME_DIR:-$OUT/../outgame}"

# ILSpy needs DOTNET_ROOT set and the global-tool dir on PATH; without DOTNET_ROOT
# ilspycmd exits silently with no output.
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT/tools:$PATH"

if ! command -v ilspycmd >/dev/null 2>&1; then
    echo "ilspycmd not found. Install with: dotnet tool install -g ilspycmd" >&2
    exit 1
fi

FIRSTPASS="$GAME_MANAGED/Amplitude.Mercury.Firstpass.dll"
OUTGAME="$GAME_MANAGED/Assembly-CSharp.dll"
[ -f "$FIRSTPASS" ] || { echo "Game assembly not found: $FIRSTPASS" >&2; exit 1; }
[ -f "$OUTGAME" ] || { echo "Game assembly not found: $OUTGAME" >&2; exit 1; }

# Single-type mode: find the assembly that defines the type. The listing covers classes,
# interfaces, structs, delegates and enums; the mod's types live across Amplitude*.dll
# (StaticString, FixedPoint, Framework) and Assembly-CSharp.dll (UI/presentation).
if [ "$#" -ge 1 ]; then
    for dll in "$GAME_MANAGED"/Amplitude*.dll "$OUTGAME"; do
        # Each listing line is "<Kind> <Full.Name>"; nested types use '+', which the C#
        # dotted query form must match too. Exact field compare: names carry '.', '+',
        # '`1' and '<>c__', so no regex matching.
        if ilspycmd -l cise "$dll" 2>/dev/null | awk -v t="$1" '{n=$2; gsub(/\+/,".",n)} n==t || $2==t {found=1} END{exit !found}'; then
            ilspycmd -t "$1" "$dll"
            exit 0
        fi
    done
    echo "type not found in any game assembly: $1" >&2
    exit 1
fi

mkdir -p "$OUT/lists"

echo "==> Simulation assembly (Firstpass): simulation, sandbox, AI snapshot, camp placement evaluation"
ilspycmd "$FIRSTPASS" -o "$OUT"
mv "$OUT/Amplitude.Mercury.Firstpass.decompiled.cs" "$OUT/Firstpass.decompiled.cs"

echo "==> Outgame assembly (Assembly-CSharp): Unity presentation, placement cursors, FIMS pins"
mkdir -p "$OUTGAME_DIR"
ilspycmd "$OUTGAME" -o "$OUTGAME_DIR"

echo "==> Type lists"
ilspycmd -l c "$FIRSTPASS" > "$OUT/lists/firstpass_types.txt"
ilspycmd -l c "$OUTGAME" > "$OUT/lists/outgame_types.txt"

echo "==> Done: $OUT"
