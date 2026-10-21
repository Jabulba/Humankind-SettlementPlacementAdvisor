#!/usr/bin/env bash
# Launch Humankind and report when THIS boot's plugin banner appears in the BepInEx log.
#
# Why the old approach failed: BepInEx truncates LogOutput.log when the game process starts,
# so every boot writes the same single "Loading [...]" line. Counting marker lines across boots
# (count > count-before) can never be true: 1 > 1 is false, so the script always timed out even
# though the game reached "game: ready".
#
# New detection: snapshot the log's mtime/size/inode before launching, wait for the file to be
# rewritten (the new boot's own content), then look for the plugin banner that Plugin.Awake
# prints after Harmony finishes patching. No dependency on any other mod's markers.
#
# Usage: scripts/launch-game.sh [--kill-first]
# Prints READY (exit 0) on detection, FAIL (exit 1) on timeout.
# Knobs: LAUNCH_TIMEOUT (seconds), GAME_APPID, MOD_LOG_PATTERN, GAME_DIR.
set -u

GAME_DIR="${GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Humankind}"
LOG="${GAME_DIR}/BepInEx/LogOutput.log"
TIMEOUT="${LAUNCH_TIMEOUT:-300}"
APPID="${GAME_APPID:-1124300}"
PATTERN="${MOD_LOG_PATTERN:-Ready: advisor patches active}"

snapshot() {
    stat -c '%Y %s %i' "$LOG" 2>/dev/null || echo "0 0 0"
}

BEFORE="$(snapshot)"

if [ "${1:-}" = "--kill-first" ]; then
    pkill -f "Humankind[.]exe" 2>/dev/null
    # Wait for the process to die: a live game would rewrite the log mid-poll.
    for _ in $(seq 1 20); do
        pgrep -f "Humankind[.]exe" >/dev/null || break
        sleep 1
    done
    sleep 2
fi

setsid steam -applaunch "$APPID" >/dev/null 2>&1 < /dev/null &
disown

DEADLINE=$((SECONDS + TIMEOUT))
while [ $SECONDS -lt $DEADLINE ]; do
    sleep 2
    NOW="$(snapshot)"
    # The new boot truncates the log, so a changed triple means fresh content.
    if [ "$NOW" != "$BEFORE" ] && grep -q "$PATTERN" "$LOG" 2>/dev/null; then
        echo READY
        exit 0
    fi
done

echo "FAIL: no '${PATTERN}' in a rewritten ${LOG} after ${TIMEOUT}s"
exit 1
