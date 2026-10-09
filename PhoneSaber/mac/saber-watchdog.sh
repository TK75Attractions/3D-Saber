#!/bin/bash
# open -W の成功は crash の有無を示さない。新しい Unity build の正常終了マーカーを併用する。
set -u
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd -P)" || exit 1
STATION="${1:?station required}"
GAME_APP="${2:?app required}"
LOG_FILE="$SCRIPT_DIR/Start-Saber-$STATION.log"
STOP_FILE="$SCRIPT_DIR/Start-Saber-$STATION.STOP"
# 受信 port (5005/5006) は台 A/B で同じなので、同じ PC で動かす監視は1つだけにする。
LOCK_DIR="$SCRIPT_DIR/.saber-watchdog.lock"
LOCK_OWNED=0
# ログは 1 MiB を超えたら起動時に .log.1 へ回す (1世代だけ残す)。
LOG_MAX_BYTES=1048576
RESTART_COUNT=0
OPEN_PID=""
MARKER_DIR="$(mktemp -d "${TMPDIR:-/tmp}/phonesaber-watchdog.XXXXXX")" || exit 1
QUIT_MARKER="$MARKER_DIR/clean-quit"
# built .app の中からは repo の P2P bridge launcher を見つけられないため、場所を環境変数で渡す。
# 既に PHONESABER_P2P_BRIDGE_SCRIPT が設定されていればそれを優先。見つからなければ LAN だけで動く。
BRIDGE_SCRIPT="${PHONESABER_P2P_BRIDGE_SCRIPT:-$SCRIPT_DIR/../ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py}"
OPEN_ENV=()
BRIDGE_STATE=no
if [[ -f "$BRIDGE_SCRIPT" ]]; then
    BRIDGE_SCRIPT="$(cd "$(dirname "$BRIDGE_SCRIPT")" && pwd -P)/$(basename "$BRIDGE_SCRIPT")"
    OPEN_ENV=(--env "PHONESABER_P2P_BRIDGE_SCRIPT=$BRIDGE_SCRIPT")
    BRIDGE_STATE=yes
fi
log() { printf '[%s] station=%s %s restart=%s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$STATION" "$*" "$RESTART_COUNT" >> "$LOG_FILE"; }
stop_file() {
    if [[ -e "$STOP_FILE" ]]; then printf '%s\n' "$STOP_FILE"; return 0; fi
    if [[ -e "$SCRIPT_DIR/STOP" ]]; then printf '%s\n' "$SCRIPT_DIR/STOP"; return 0; fi
    return 1
}
stopped() { stop_file >/dev/null; }
# lock を持っている監視の PID を返す。PID が再利用されていたら持ち主なしとみなす。
lock_holder() {
    local pid
    pid="$(cat "$LOCK_DIR/pid" 2>/dev/null)" || return 1
    [[ -n "$pid" ]] || return 1
    case "$(ps -ww -p "$pid" -o command= 2>/dev/null)" in
        *saber-watchdog.sh*) printf '%s\n' "$pid" ;;
        *) return 1 ;;
    esac
}
acquire_lock() {
    local attempt
    for attempt in 1 2; do
        if mkdir "$LOCK_DIR" 2>/dev/null; then
            LOCK_OWNED=1
            printf '%s\n' "$$" > "$LOCK_DIR/pid"
            return 0
        fi
        # PID を書き込む途中の lock を古いと誤判定しないよう、少し待ってから確かめる。
        sleep 1
        lock_holder >/dev/null && return 1
        rm -f "$LOCK_DIR/pid"
        rmdir "$LOCK_DIR" 2>/dev/null
    done
    return 1
}
# 前回 Ctrl+C で監視だけ止めた場合など、同じ名前のゲームが既に動いていれば二重起動しない。
# (grep だと grep 自身のコマンド行に一致するため、bash の文字列比較で調べる)
game_running() {
    local pattern command
    pattern="/$(basename "$GAME_APP")/Contents/MacOS/"
    while IFS= read -r command; do
        [[ "$command" == *"$pattern"* ]] && return 0
    done < <(ps -axww -o command= 2>/dev/null)
    return 1
}
cleanup() {
    [[ -z "$OPEN_PID" ]] || kill "$OPEN_PID" 2>/dev/null || true
    rm -f "$QUIT_MARKER"
    rmdir "$MARKER_DIR" 2>/dev/null || true
    if [[ "$LOCK_OWNED" -eq 1 ]]; then
        rm -f "$LOCK_DIR/pid"
        rmdir "$LOCK_DIR" 2>/dev/null || true
    fi
}
trap cleanup EXIT
trap 'log "watchdog-stopped signal"; printf "\n監視を終了しました。起動中のゲームはそのままです (終了はゲームの Quit)。\n"; exit 130' INT TERM HUP
if [[ -f "$LOG_FILE" && $(wc -c < "$LOG_FILE") -gt $LOG_MAX_BYTES ]]; then
    mv -f "$LOG_FILE" "$LOG_FILE.1"
fi
if [[ ! -d "$GAME_APP" ]]; then
    printf 'ゲーム .app が見つかりません: %s\n' "$GAME_APP" >&2
    printf 'Unity の Tools > PhoneSaber > Build > macOS Player でビルドするか、Start-Saber-%s.command の GAME_APP を直してください。\n' "$STATION" >&2
    log "not-started app-missing"
    exit 1
fi
if stopped; then
    printf 'STOP ファイルがあるため、台 %s を起動しません: %s\n' "$STATION" "$(stop_file)" >&2
    printf 'このファイルを削除してから、もう一度ダブルクリックしてください。\n' >&2
    log "not-started STOP-file"
    exit 1
fi
if ! acquire_lock; then
    holder="$(lock_holder)"
    printf 'この PC では既に監視が動いています (PID %s)。台 %s は起動しません。\n' "${holder:-?}" "$STATION" >&2
    printf '同じ PC で2つ動かすと受信 port が衝突します。先に開いた Terminal を確認してください。\n' >&2
    log "not-started already-running pid=${holder:-?}"
    exit 1
fi
if game_running; then
    printf '%s が既に起動しています。台 %s は起動しません。\n' "$(basename "$GAME_APP")" "$STATION" >&2
    printf 'ゲームを Quit してから、もう一度ダブルクリックしてください。\n' >&2
    log "not-started game-already-running"
    exit 1
fi
while ! stopped; do
    rm -f "$QUIT_MARKER"
    log "launch p2p-bridge=$BRIDGE_STATE"
    printf '台 %s を起動します。再起動回数: %s [Ctrl+C: 監視終了]\n' "$STATION" "$RESTART_COUNT"
    /usr/bin/open -W -n ${OPEN_ENV[@]+"${OPEN_ENV[@]}"} "$GAME_APP" --args -phonesaberStation "$STATION" -phonesaberQuitMarker "$QUIT_MARKER" &
    OPEN_PID=$!
    wait "$OPEN_PID"
    OPEN_EXIT=$?
    OPEN_PID=""
    # open の終了コードに加え、正常終了の印が無ければ crash / 強制終了として扱う。
    GAME_EXIT=$OPEN_EXIT
    if [[ "$GAME_EXIT" -eq 0 && ! -f "$QUIT_MARKER" ]]; then GAME_EXIT=1; fi
    log "exit-code=$GAME_EXIT open-exit-code=$OPEN_EXIT clean-quit=$([[ -f "$QUIT_MARKER" ]] && echo yes || echo no)"
    if [[ "$GAME_EXIT" -eq 0 ]]; then
        printf 'ゲームが正常終了しました。監視を終了します。\n'
        exit 0
    fi
    stopped && break
    RESTART_COUNT=$((RESTART_COUNT + 1))
    printf '異常終了。5秒後に再起動します。\n'
    for delay in 1 2 3 4 5; do
        stopped && break
        sleep 1
    done
done
printf 'STOP ファイル (%s) があるため、再起動せずに監視を終了します。\n' "$(stop_file)"
log "watchdog-stopped STOP-file"
