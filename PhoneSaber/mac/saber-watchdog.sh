#!/bin/bash
# open -W の成功は crash の有無を示さない。新しい Unity build の正常終了マーカーを併用する。
set -u
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd -P)" || exit 1
STATION="${1:?station required}"
GAME_APP="${2:?app required}"
LOG_FILE="$SCRIPT_DIR/Start-Saber-$STATION.log"
STOP_FILE="$SCRIPT_DIR/Start-Saber-$STATION.STOP"
RESTART_COUNT=0
OPEN_PID=""
MARKER_DIR="$(mktemp -d "${TMPDIR:-/tmp}/phonesaber-watchdog.XXXXXX")" || exit 1
QUIT_MARKER="$MARKER_DIR/clean-quit"
log() { printf '[%s] station=%s %s restart=%s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$STATION" "$*" "$RESTART_COUNT" >> "$LOG_FILE"; }
stopped() { [[ -e "$STOP_FILE" || -e "$SCRIPT_DIR/STOP" ]]; }
cleanup() {
    [[ -z "$OPEN_PID" ]] || kill "$OPEN_PID" 2>/dev/null || true
    rm -f "$QUIT_MARKER"
    rmdir "$MARKER_DIR" 2>/dev/null || true
}
trap cleanup EXIT
trap 'log "watchdog-stopped signal"; exit 130' INT TERM HUP
if [[ ! -d "$GAME_APP" ]]; then
    printf 'ゲーム .app が見つかりません: %s\n' "$GAME_APP" >&2
    exit 1
fi
while ! stopped; do
    rm -f "$QUIT_MARKER"
    log "launch"
    printf '台 %s を起動します。再起動回数: %s [Ctrl+C: 監視終了]\n' "$STATION" "$RESTART_COUNT"
    /usr/bin/open -W -n "$GAME_APP" --args -phonesaberStation "$STATION" -phonesaberQuitMarker "$QUIT_MARKER" &
    OPEN_PID=$!
    wait "$OPEN_PID"
    OPEN_EXIT=$?
    OPEN_PID=""
    # open の終了コードに加え、正常終了の印が無ければ crash / 強制終了として扱う。
    GAME_EXIT=$OPEN_EXIT
    if [[ "$GAME_EXIT" -eq 0 && ! -f "$QUIT_MARKER" ]]; then GAME_EXIT=1; fi
    log "exit-code=$GAME_EXIT open-exit-code=$OPEN_EXIT clean-quit=$([[ -f "$QUIT_MARKER" ]] && echo yes || echo no)"
    [[ "$GAME_EXIT" -ne 0 ]] || exit 0
    stopped && break
    RESTART_COUNT=$((RESTART_COUNT + 1))
    printf '異常終了。5秒後に再起動します。\n'
    for delay in 1 2 3 4 5; do
        stopped && break
        sleep 1
    done
done
log "watchdog-stopped STOP-file"
