#!/usr/bin/env bash
# Decompile HUMANKIND's managed assemblies into ./reference for offline reading.
# reference/ is gitignored: decompiled game code must NEVER be committed.
#
# Requirements: dotnet SDK in ~/.dotnet with ilspycmd as a global tool
#   (dotnet tool install -g ilspycmd), and Humankind installed via Steam.
#
# Usage:
#   scripts/decompile-game.sh                    # full decompile into project trees + type lists
#   scripts/decompile-game.sh <FullyQualifiedTypeName>
#                                                # print a single type to stdout
#
# Full decompile layout (project mode: one file per type, one folder per namespace):
#   $OUT/firstpass/<Namespace>/<Type>.cs         # Amplitude.Mercury.Firstpass.dll
#   $OUTGAME_DIR/assembly-csharp/<Namespace>/<Type>.cs
#   $OUT/lists/                                  # type listings
#
# Env: GAME_MANAGED, OUT (managed cache root), OUTGAME_DIR (default: sibling outgame
# dir), DOTNET_ROOT. Keep both output dirs inside the gitignored reference/ tree.
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
mkdir -p "$OUT/firstpass"
ilspycmd -p -o "$OUT/firstpass" "$FIRSTPASS"

echo "==> Outgame assembly (Assembly-CSharp): Unity presentation, placement cursors, FIMS pins"
mkdir -p "$OUTGAME_DIR/assembly-csharp"
ilspycmd -p -o "$OUTGAME_DIR/assembly-csharp" "$OUTGAME"
echo "==> Type lists"
ilspycmd -l c "$FIRSTPASS" > "$OUT/lists/firstpass_types.txt"
ilspycmd -l c "$OUTGAME" > "$OUT/lists/outgame_types.txt"

echo "==> Done: $OUT"
