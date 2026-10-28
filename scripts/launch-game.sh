#!/usr/bin/env bash
# Launch Humankind and report when THIS boot's plugin banner appears in the BepInEx log.
#
# BepInEx truncates LogOutput.log at process start, so marker-line counting across boots
# never works (1 > 1 is false). Instead snapshot the log's mtime/size/inode, wait for the
# rewrite, then look for the banner Plugin.Awake prints after Harmony patches.
# If the game process dies after its log rewrite (crash or early exit), FAIL immediately
# with the last 5 entries of LogOutput.log and of the Proton prefix's Player.log.
#
# Usage: scripts/launch-game.sh [--kill-first]
# Prints READY (exit 0) on detection, FAIL (exit 1) on timeout or game death.
# Knobs: LAUNCH_TIMEOUT (seconds), GAME_APPID, MOD_LOG_PATTERN, GAME_DIR, PLAYER_LOG.
set -u

GAME_DIR="${GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Humankind}"
LOG="${GAME_DIR}/BepInEx/LogOutput.log"
TIMEOUT="${LAUNCH_TIMEOUT:-300}"
APPID="${GAME_APPID:-1124300}"
PATTERN="${MOD_LOG_PATTERN:-SPA advisor patches applied}"
# Player.log lives in the Proton prefix under steamapps/compatdata/<APPID>; derive the
# steamapps root from GAME_DIR so a relocated Steam install follows.
STEAMAPPS="${GAME_DIR%/*/*}"
PLAYER_LOG="${PLAYER_LOG:-${STEAMAPPS}/compatdata/${APPID}/pfx/drive_c/users/steamuser/AppData/LocalLow/AMPLITUDE Studios/Humankind/Player.log}"
snapshot() {
    stat -c '%Y %s %i' "$LOG" 2>/dev/null || echo "0 0 0"
}

# Last 5 entries of both startup logs; Player.log holds Plugin.Awake failures that
# never reach LogOutput.log.
log_tails() {
    echo "--- tail ${LOG} ---"
    tail -n 5 "$LOG" 2>/dev/null
    echo "--- tail ${PLAYER_LOG} ---"
    tail -n 5 "$PLAYER_LOG" 2>/dev/null
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
    # Death check only after the log rewrite proves this boot started; a game that
    # closed without ever rewriting the log can only be reported by the timeout.
    if [ "$NOW" != "$BEFORE" ] && ! pgrep -f "Humankind[.]exe" >/dev/null; then
        echo "FAIL: game closed before '${PATTERN}' appeared"
        log_tails
        exit 1
    fi
done

echo "FAIL: no '${PATTERN}' in a rewritten ${LOG} after ${TIMEOUT}s"
exit 1
