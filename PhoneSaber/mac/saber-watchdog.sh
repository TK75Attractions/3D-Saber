#!/bin/bash
# open -W の成功は crash の有無を示さない。新しい Unity build の正常終了マーカーを併用する。
set -u
SCRIPT_DIR="$(CDPATH='' cd "$(dirname "$0")" && pwd -P)" || exit 1
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
HANG_SECONDS="${PHONESABER_HANG_SECONDS:-45}"
# 無効な値は既定値へ戻す。0 はハング監視を無効にする。
case "$HANG_SECONDS" in ''|*[!0-9]*) HANG_SECONDS=45 ;; esac
# 先頭の 0 を除き、bash の八進数解釈を避ける(長すぎる値も受け付けない)。
while [[ ${#HANG_SECONDS} -gt 1 && "$HANG_SECONDS" == 0* ]]; do HANG_SECONDS="${HANG_SECONDS#0}"; done
[[ ${#HANG_SECONDS} -le 6 ]] || HANG_SECONDS=45
HEARTBEAT=""

# built .app の中からは repo の P2P bridge launcher を見つけられないため、場所を環境変数で渡す。
# 既に PHONESABER_P2P_BRIDGE_SCRIPT が設定されていればそれを優先。見つからなければ LAN だけで動く。
BRIDGE_SCRIPT="${PHONESABER_P2P_BRIDGE_SCRIPT:-$SCRIPT_DIR/../ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py}"
OPEN_ENV=()
BRIDGE_STATE=no
if [[ -f "$BRIDGE_SCRIPT" ]]; then
    BRIDGE_SCRIPT="$(CDPATH='' cd "$(dirname "$BRIDGE_SCRIPT")" && pwd -P)/$(basename "$BRIDGE_SCRIPT")"
    OPEN_ENV=(--env "PHONESABER_P2P_BRIDGE_SCRIPT=$BRIDGE_SCRIPT")
    BRIDGE_STATE=yes
fi
# iPhone の自動クリップ（c81113d）を受け取る受信機を、窓なし・Codex なしで裏に立てる（無ければ）。
# 届いたクリップは ~/Library/Application Support/PhoneSaber/diagnostics-inbox/autoclips/ に入り、まとめ（.report.md・.contact.png）が横に書かれる。
# PHONESABER_TRIAGE_RECEIVER=0 で立てない。
TRIAGE_LAUNCHER="${PHONESABER_TRIAGE_LAUNCHER:-$SCRIPT_DIR/../ios/PhoneSaberSender/Tools/phone_saber_receiver_launcher.py}"
if [[ -f "$TRIAGE_LAUNCHER" ]]; then
    TRIAGE_LAUNCHER="$(CDPATH='' cd "$(dirname "$TRIAGE_LAUNCHER")" && pwd -P)/$(basename "$TRIAGE_LAUNCHER")"
    OPEN_ENV+=(--env "PHONESABER_TRIAGE_LAUNCHER=$TRIAGE_LAUNCHER")
fi
EXIT_FILE="$SCRIPT_DIR/Start-Saber-$STATION.last-exit.log"
OPEN_ENV+=(--env "PHONESABER_WATCHDOG_EXIT_FILE=$EXIT_FILE")
start_triage_receiver() {
    case "${PHONESABER_TRIAGE_RECEIVER:-1}" in 0|[oO][fF][fF]) return 0 ;; esac
    [[ -f "$TRIAGE_LAUNCHER" ]] || return 0
    # Unity と同じ lock / health / 安全な更新判定を使う。
    nohup /usr/bin/python3 "$TRIAGE_LAUNCHER" --no-codex >/dev/null 2>&1 < /dev/null &
}
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
    local attempt stale
    for attempt in 1 2; do
        if mkdir "$LOCK_DIR" 2>/dev/null; then
            printf '%s\n' "$$" > "$LOCK_DIR/pid"
            # 同時に起動した別の監視が、この lock を「古い」と判断した直後に回収して取り直すことがある
            # (ダブルクリックの連打)。少し待って自分の PID のままか確かめ、違えば相手に譲る。
            sleep 1
            [[ "$(cat "$LOCK_DIR/pid" 2>/dev/null)" == "$$" ]] || return 1
            LOCK_OWNED=1
            return 0
        fi
        # PID を書き込む途中の lock を古いと誤判定しないよう、少し待ってから確かめる。
        sleep 1
        lock_holder >/dev/null && return 1
        # 古い lock は rename で回収する。rm + rmdir だと2つの監視が同時に回収でき、両方が起動してしまう。
        stale="$LOCK_DIR.stale.$$"
        mv "$LOCK_DIR" "$stale" 2>/dev/null || continue
        rm -rf "$stale"
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
# open -W の PID ではなく、今回の heartbeat 引数を持つ .app 内の実プロセスを探す。
# 別 run / 同名アプリや open 自身を強制終了しない。
run_game_pid() {
    local pid command found="" pattern
    pattern="/$(basename "$GAME_APP")/Contents/MacOS/"
    while read -r pid command; do
        if [[ "$command" == *"$pattern"* && "$command" == *"$HEARTBEAT"* ]]; then
            [[ -z "$found" ]] || return 1
            found="$pid"
        fi
    done < <(ps -axww -o pid= -o command= 2>/dev/null)
    [[ -n "$found" ]] || return 1
    printf '%s\n' "$found"
}
# IODisplayWrangler に電源状態が無い Mac でも、標準ライブラリだけで全画面を確認する。
# 蓋を閉じても外部画面が起きていれば監視を続ける。取得失敗時も従来どおり監視する。
display_asleep() {
    local state
    state="$(python3 -c '
import ctypes as c
cg = c.CDLL("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")
cg.CGGetOnlineDisplayList.argtypes = [c.c_uint32, c.POINTER(c.c_uint32), c.POINTER(c.c_uint32)]
cg.CGGetOnlineDisplayList.restype = c.c_int32
cg.CGDisplayIsAsleep.argtypes = [c.c_uint32]
cg.CGDisplayIsAsleep.restype = c.c_uint32
count = c.c_uint32()
if cg.CGGetOnlineDisplayList(0, None, c.byref(count)):
    raise SystemExit(1)
if not count.value:
    print("none")
    raise SystemExit(0)
displays = (c.c_uint32 * count.value)()
if cg.CGGetOnlineDisplayList(len(displays), displays, c.byref(count)):
    raise SystemExit(1)
cg.CGDisplayIsBuiltin.argtypes = [c.c_uint32]
cg.CGDisplayIsBuiltin.restype = c.c_uint32
online = displays[:count.value]
print("none" if not online else
      "asleep" if all(cg.CGDisplayIsAsleep(d) for d in online) else
      "builtin" if all(cg.CGDisplayIsBuiltin(d) for d in online) else "awake")
' 2>/dev/null)" || return 1
    case "$state" in
        asleep) return 0 ;;
        # 接続画面が無い場合だけ蓋の状態を使う（閉じた蓋だけでは外部画面を除外できない）。
        # 内蔵画面だけのときも蓋が閉じていれば描画されない（10/22 実測: 蓋閉で awake 報告のまま停止）。
        none|builtin) ioreg -r -k AppleClamshellState -d 4 2>/dev/null |
            awk '/"AppleClamshellState"[[:space:]]*=[[:space:]]*Yes([[:space:]]|$)/ { closed=1 } END { exit !closed }' ;;
        *) return 1 ;;
    esac
}
monitor_game() {
    local seen=0 modified size last_modified=0 now pid command last_tick grace_until=0 display_paused=0
    last_tick="$(date +%s)"
    while kill -0 "$OPEN_PID" 2>/dev/null; do
        # 監視自身が止まっていた（Mac のスリープ復帰など）間は、ゲームも heartbeat を書けていない。
        # 復帰直後に「止まっている」と誤判定して正常なゲームを終了させないよう、判定をいったん休む。
        now="$(date +%s)"
        if [[ $((now - last_tick)) -gt 10 ]]; then
            grace_until=$((now + HANG_SECONDS))
            log "watchdog resumed after $((now - last_tick))s; hang check paused"
        fi
        last_tick="$now"
        # 画面スリープ中は Unity の Update も止まり得るので heartbeat の古さを数えない。
        if [[ "$HANG_SECONDS" -gt 0 ]] && display_asleep; then
            [[ "$display_paused" -eq 1 ]] || log "hang-check paused display-asleep"
            display_paused=1
            sleep 1
            continue
        fi
        if [[ "$display_paused" -eq 1 ]]; then
            display_paused=0
            grace_until=$((now + HANG_SECONDS))
            log "hang-check resumed display-awake grace=${HANG_SECONDS}s"
        fi
        if [[ "$HANG_SECONDS" -gt 0 && "$now" -ge "$grace_until" ]] && ! stopped; then
            read -r modified size <<< "$(stat -f '%m %z' "$HEARTBEAT" 2>/dev/null)"
            # 作成だけ成功し、最初の書込みが容量不足で失敗した0 byteファイルは進行の証拠ではない。
            if [[ "$modified" =~ ^[0-9]+$ && "$size" == "1" ]]; then
                seen=1
                last_modified="$modified"
            fi
            if [[ "$seen" -eq 1 ]]; then
                now="$(date +%s)"
                if [[ "$seen" -eq 1 && $((now - last_modified)) -ge "$HANG_SECONDS" ]]; then
                    pid="$(run_game_pid)" || pid=""
                    if [[ -n "$pid" ]]; then
                        # 検索後に PID が再利用されても無関係なプロセスは止めない。
                        command="$(ps -ww -p "$pid" -o command= 2>/dev/null)"
                        if [[ "$command" == *"$HEARTBEAT"* && "$command" == */Contents/MacOS/* ]]; then
                            log "hang detected pid=$pid heartbeat-age=$((now - last_modified))"
                            if kill -KILL "$pid" 2>/dev/null; then HANG_DETECTED=1; return; fi
                        fi
                    fi
                fi
            fi
        fi
        sleep 1
    done
}
cleanup() {
    [[ -z "$OPEN_PID" ]] || kill "$OPEN_PID" 2>/dev/null || true
    rm -f "$QUIT_MARKER" "$MARKER_DIR"/heartbeat-*
    rmdir "$MARKER_DIR" 2>/dev/null || true
    # 自分の lock だけを片づける (別の監視が取り直した lock を消さない)。
    if [[ "$LOCK_OWNED" -eq 1 && "$(cat "$LOCK_DIR/pid" 2>/dev/null)" == "$$" ]]; then
        rm -f "$LOCK_DIR/pid"
        rmdir "$LOCK_DIR" 2>/dev/null || true
    fi
}
trap cleanup EXIT
trap 'log "watchdog-stopped signal"; printf "\n監視を終了しました。起動中のゲームはそのままです (終了はゲームの Quit)。\n"; exit 130' INT TERM HUP
if [[ -f "$LOG_FILE" && ! -d "$LOG_FILE.1" && $(wc -c < "$LOG_FILE") -gt $LOG_MAX_BYTES ]]; then
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
# 低電力モードやバッテリー駆動では画面の更新が毎秒30回に落ち、ゲーム全体が「もっさり」する(2026-10-13)。
# 設定は変えずに知らせるだけ。起動は止めない。
power_warning() {
    local low source
    low="$(pmset -g 2>/dev/null | awk '$1 == "lowpowermode" { print $2 }')"
    case "$low" in 0|1) LOW_POWER_STATE="$low" ;; *) LOW_POWER_STATE=unknown ;; esac
    source="$(pmset -g batt 2>/dev/null | head -1)"
    log "power low-power=$LOW_POWER_STATE source=$source"
    if [[ "$low" == "1" ]]; then
        printf '注意: この Mac は低電力モードが ON です。画面が毎秒30コマになり、動きが重く見えます。\n'
        printf '      システム設定 > バッテリー > 低電力モード を「しない」にしてから起動し直してください。\n'
        log "warning low-power-mode"
    fi
    if [[ "$source" == *"Battery Power"* ]]; then
        printf '注意: バッテリーで動いています。電源アダプタをつないでください。\n'
        log "warning battery-power"
    fi
}
while ! stopped; do
    power_warning
    # 最初の起動だけ: 低電力モードの注意を読めるよう少し待つ(ゲームが全画面になると端末の表示が隠れる)。
    # 無人の再起動(2回目以降)と、端末以外からの起動では待たない。PHONESABER_LOW_POWER_WAIT=0 で無効。
    if [[ "$RESTART_COUNT" == "0" && "$LOW_POWER_STATE" == "1" && -t 0 && "${PHONESABER_LOW_POWER_WAIT:-10}" != "0" ]]; then
        printf '%s 秒後にこのまま起動します(Enter ですぐ起動 / Ctrl+C で中止)。\n' "${PHONESABER_LOW_POWER_WAIT:-10}"
        read -r -t "${PHONESABER_LOW_POWER_WAIT:-10}" _ || true
        log "low-power-wait done"
    fi
    stopped && break
    rm -f "$QUIT_MARKER" "$MARKER_DIR"/heartbeat-*
    HEARTBEAT="$MARKER_DIR/heartbeat-$RESTART_COUNT"
    HANG_DETECTED=0
    start_triage_receiver
    log "launch p2p-bridge=$BRIDGE_STATE hang-seconds=$HANG_SECONDS"
    printf '台 %s を起動します。再起動回数: %s [Ctrl+C: 監視終了]\n' "$STATION" "$RESTART_COUNT"
    /usr/bin/open -W -n ${OPEN_ENV[@]+"${OPEN_ENV[@]}"} --env "PHONESABER_LOW_POWER_MODE=$LOW_POWER_STATE" "$GAME_APP" --args -phonesaberStation "$STATION" -phonesaberQuitMarker "$QUIT_MARKER" -phonesaberHeartbeat "$HEARTBEAT" &
    OPEN_PID=$!
    monitor_game
    wait "$OPEN_PID"
    OPEN_EXIT=$?
    OPEN_PID=""
    # open の終了コードに加え、正常終了の印が無ければ crash / 強制終了として扱う。
    GAME_EXIT=$OPEN_EXIT
    if [[ "$HANG_DETECTED" -eq 1 ]]; then GAME_EXIT=1; fi
    if [[ "$GAME_EXIT" -eq 0 && ! -f "$QUIT_MARKER" ]]; then GAME_EXIT=1; fi
    log "exit-code=$GAME_EXIT open-exit-code=$OPEN_EXIT clean-quit=$([[ -f "$QUIT_MARKER" ]] && echo yes || echo no)"
    # 次の Player が前回の監視結果を報告できるよう、台ごとに最後の結果を保存する。
    printf 'utc=%s exit-code=%s open-exit-code=%s clean-quit=%s hang-detected=%s\n' \
        "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$GAME_EXIT" "$OPEN_EXIT" \
        "$([[ -f "$QUIT_MARKER" ]] && echo yes || echo no)" "$HANG_DETECTED" > "$EXIT_FILE.tmp" &&
        mv -f "$EXIT_FILE.tmp" "$EXIT_FILE"
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
