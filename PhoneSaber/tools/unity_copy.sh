#!/bin/bash
# Unity の作業はプロジェクトの「複製」で行う（ユーザーが Editor で開いている checkout には触れない）。
#
#   PhoneSaber/tools/unity_copy.sh sync                 複製を現在の checkout に合わせる
#   PhoneSaber/tools/unity_copy.sh editmode [filter]    EditMode テスト（filter は -testFilter の正規表現）
#   PhoneSaber/tools/unity_copy.sh build-mac            macOS Player をビルドし、起動して確かめてから Builds/Mac/3D-Saber.app を差し替える
#   PhoneSaber/tools/unity_copy.sh shots [app]          ビルド済み Player を隠して起動し、各 scene の画面を _out/shots/*.png に残す
#   PhoneSaber/tools/unity_copy.sh smoke [app]          ビルド済み Player を窓なしで起動し、例外が出ないか確かめる
#
# 複製の場所: $PHONESABER_UNITY_COPY（既定 ~/Library/Caches/PhoneSaber/unity-copy）。Library を含むので数 GB。
# 消えても sync で作り直せる（初回の import に数分かかる）。PlayMode 全件は unity_playmode_batches.py を使う。
# 既知失敗を許容するときだけ PHONESABER_EDITMODE_KNOWN_FAILURES に完全名の JSON 配列ファイルを指定する。
# JSON 形式例（仮の名前）: ["ExampleTests.KnownFailure", "OtherTests.KnownFailure(1)"]
# 未指定・空配列ならすべての失敗を非ゼロで返す。クラス名・前方一致・正規表現では除外しない。
# 既知の譜面エディタ失敗2件は完全名の記録がないため、推測でクラス全体を除外しない。
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)" || exit 1
COPY="${PHONESABER_UNITY_COPY:-$HOME/Library/Caches/PhoneSaber/unity-copy}"
UNITY="${PHONESABER_UNITY:-/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/MacOS/Unity}"
OUT="${PHONESABER_UNITY_OUT:-$COPY/_out}"

sync_copy() {
    mkdir -p "$COPY/PhoneSaber/tools" "$OUT" || return 1
    rsync -a --delete "$ROOT/Assets" "$ROOT/Packages" "$ROOT/ProjectSettings" "$COPY/" || return 1
    # 一部の EditMode テストは PhoneSaber/tools/fixtures の共有ベクトルを読む。
    rsync -a --delete "$ROOT/PhoneSaber/tools/fixtures" "$COPY/PhoneSaber/tools/" || return 1
}

# macOS では実行ファイルを直接起動すると、batchmode でも Dock に出て前面のアプリからフォーカスを奪う
# （作業中のユーザーの画面に割り込む。2026-10-13 ユーザー指摘）。.app の中の実行ファイルは
# open -g -j（前面に出さない・隠して起動）で起動し、終了まで待つ。
# open 経由では終了コードが取れないので、成否は必ずログ・結果ファイルで判定する。
run_hidden() {   # run_hidden <実行ファイル> [引数...]
    local exe="$1"; shift
    local app="${exe%/Contents/MacOS/*}"
    if [[ "$app" != "$exe" && -d "$app" && -x /usr/bin/open && -z "${PHONESABER_UNITY_FOREGROUND:-}" ]]; then
        /usr/bin/open -g -j -n -W "$app" --args "$@"
    else
        "$exe" "$@"
    fi
}

# ビルド済み Player を窓なしで起動し、Title → SongSelect → Game を順に開いて例外を数える。
# (PhoneSaberPlayerSmoke.cs。UDP 5005-5007 を数十秒使うので、ゲームが起動中のときは呼ばない)
smoke_player() {
    local app="$1" exe log="$OUT/smoke.log" pid waited=0 limit="${PHONESABER_SMOKE_LIMIT:-150}"
    exe="$(find "$app/Contents/MacOS" -maxdepth 1 -type f | head -1)"
    [[ -x "$exe" ]] || { echo "smoke: 実行ファイルが見つかりません: $app"; return 1; }
    rm -f "$log"
    run_hidden "$exe" -batchmode -phonesaberSmoke "${PHONESABER_SMOKE_SECONDS:-8}" -logFile "$log" >/dev/null 2>&1 &
    pid=$!
    while kill -0 "$pid" 2>/dev/null; do
        if (( waited >= limit )); then
            # open 経由のときは Player が別プロセスなので、起動確認の引数で特定して止める（遊んでいるゲームは対象外）。
            pkill -KILL -f -- "-phonesaberSmoke" 2>/dev/null; kill -KILL "$pid" 2>/dev/null
            echo "smoke: ${limit} 秒で終わらないため中止 ($log)"; return 1
        fi
        sleep 1; waited=$((waited + 1))
    done
    # open 経由では Player の終了コードは分からない（常に 0）。下のログの判定が本体。
    wait "$pid"; local code=$?
    local count
    count="$(grep -cE '^[A-Za-z.]*Exception' "$log" 2>/dev/null)"
    if ! grep -q "\[PhoneSaberSmoke\] done" "$log" 2>/dev/null; then
        echo "smoke: 最後まで進みませんでした (exit=$code, $log)"; grep -nE '^[A-Za-z.]*Exception' -A3 "$log" 2>/dev/null | head -12; return 1
    fi
    if [[ "$code" -ne 0 || "$count" -ne 0 ]]; then
        echo "smoke: 例外 $count 件 (exit=$code, $log)"; grep -nE '^[A-Za-z.]*Exception' -A4 "$log" | head -30; return 1
    fi
    if ! grep -q "\[PhoneSaberSmoke\] done exceptions=0 input=ok" "$log"; then
        echo "smoke: 座標の受信か剣の部品が確認できません ($log)"; grep -n "PhoneSaberSmoke" "$log" | tail -4; return 1
    fi
    echo "smoke: Title / SongSelect / Game を例外なしで通過、座標の受信と剣の部品も確認 (${waited} 秒)"
}

# ビルド済み Player を隠したまま起動し、各 scene の画面を PNG に残す（見た目の確認用。窓は出さない）。
# 万一 Player が前面に出たら、すぐ隠し直す。PHONESABER_SHOTS_REFRESH_HZ=30 で「画面の更新が遅い」注意の表示を確かめられる。
shots_player() {
    local app="$1" exe dir="$OUT/shots" log="$OUT/shots.log" pid game waited=0 args
    exe="$(find "$app/Contents/MacOS" -maxdepth 1 -type f | head -1)"
    [[ -x "$exe" ]] || { echo "shots: 実行ファイルが見つかりません: $app"; return 1; }
    rm -rf "$dir" "$log"; mkdir -p "$dir"
    args=(-phonesaberSmoke "${PHONESABER_SMOKE_SECONDS:-5}" -phonesaberSmokeShots "$dir" -logFile "$log")
    [[ -n "${PHONESABER_SHOTS_REFRESH_HZ:-}" ]] && args+=(-phonesaberSmokeRefreshHz "$PHONESABER_SHOTS_REFRESH_HZ")
    run_hidden "$exe" "${args[@]}" >/dev/null 2>&1 &
    pid=$!
    while kill -0 "$pid" 2>/dev/null; do
        if (( waited >= 240 )); then pkill -KILL -f -- "-phonesaberSmokeShots" 2>/dev/null; kill -KILL "$pid" 2>/dev/null; echo "shots: 終わらないため中止 ($log)"; return 1; fi
        game="$(pgrep -f -- "-phonesaberSmokeShots" | head -1)"
        if [[ -n "$game" ]] && [[ "$(osascript -e 'tell application "System Events" to get unix id of first process whose frontmost is true' 2>/dev/null)" == "$game" ]]; then
            osascript -e "tell application \"System Events\" to set visible of (first process whose unix id is $game) to false" >/dev/null 2>&1
            echo "shots: Player が前面に出たので隠しました"
        fi
        sleep 0.5; waited=$((waited + 1))
    done
    grep "PhoneSaberSmoke\] \(shot\|done\)" "$log" 2>/dev/null
    ls "$dir"/*.png >/dev/null 2>&1 || { echo "shots: 画像がありません ($log)"; return 1; }
    echo "shots: $dir"
}

case "${1:-}" in
smoke)
    smoke_player "${2:-$ROOT/Builds/Mac/3D-Saber.app}"
    ;;
shots)
    shots_player "${2:-$ROOT/Builds/Mac/3D-Saber.app}"
    ;;
sync)
    sync_copy && echo "synced: $COPY"
    ;;
editmode)
    sync_copy || exit 1
    rm -f "$OUT/editmode.xml"
    args=(-batchmode -projectPath "$COPY" -runTests -testPlatform EditMode -testResults "$OUT/editmode.xml" -logFile "$OUT/editmode.log")
    [[ -n "${2:-}" ]] && args+=(-testFilter "$2")
    run_hidden "$UNITY" "${args[@]}"
    result_args=("$OUT/editmode.xml" "$OUT/editmode.log")
    [[ -n "${PHONESABER_EDITMODE_KNOWN_FAILURES:-}" ]] && result_args+=(--known-failures "$PHONESABER_EDITMODE_KNOWN_FAILURES")
    python3 "$ROOT/PhoneSaber/tools/unity_editmode_results.py" "${result_args[@]}"
    ;;
build-mac)
    sync_copy || exit 1
    rm -rf "$COPY/Builds/Mac"
    rm -f "$OUT/build-mac.log"
    run_hidden "$UNITY" -batchmode -quit -projectPath "$COPY" -executeMethod PhoneSaberPlayerBuild.BuildMac -logFile "$OUT/build-mac.log"
    if ! grep -q "Result: Success" "$OUT/build-mac.log" || [[ ! -d "$COPY/Builds/Mac/3D-Saber.app" ]]; then
        echo "build-mac: 失敗（$OUT/build-mac.log）"; exit 1
    fi
    if pgrep -f "/3D-Saber.app/Contents/MacOS/" >/dev/null; then
        echo "build-mac: ビルド成功。ゲームが起動中なので差し替えていません: $COPY/Builds/Mac/3D-Saber.app"; exit 3
    fi
    # 差し替える前に、作った Player を実際に起動して確かめる(Editor では動くのに Player だけ落ちる不具合を止める)。
    smoke_player "$COPY/Builds/Mac/3D-Saber.app" || { echo "build-mac: 動作確認に失敗したので差し替えていません"; exit 4; }
    mkdir -p "$ROOT/Builds/Mac" "$OUT/previous-app"
    rm -rf "$OUT/previous-app/3D-Saber.app"
    [[ -d "$ROOT/Builds/Mac/3D-Saber.app" ]] && mv "$ROOT/Builds/Mac/3D-Saber.app" "$OUT/previous-app/"
    ditto "$COPY/Builds/Mac/3D-Saber.app" "$ROOT/Builds/Mac/3D-Saber.app" || exit 1
    echo "build-mac: $ROOT/Builds/Mac/3D-Saber.app を $(git -C "$ROOT" log --oneline -1 | cut -c1-8) で差し替えました（1つ前は $OUT/previous-app）"
    ;;
*)
    sed -n '2,/^set -u/{s/^# \{0,1\}//p;}' "${BASH_SOURCE[0]}"
    exit 2
    ;;
esac
