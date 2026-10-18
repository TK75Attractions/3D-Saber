#!/bin/bash
# Unity の作業はプロジェクトの「複製」で行う（ユーザーが Editor で開いている checkout には触れない）。
#
#   PhoneSaber/tools/unity_copy.sh sync                 複製を現在の checkout に合わせる
#   PhoneSaber/tools/unity_copy.sh editmode [filter]    EditMode テスト（filter は -testFilter の正規表現）
#   PhoneSaber/tools/unity_copy.sh build-mac            macOS Player をビルドし、起動して確かめてから Builds/Mac/3D-Saber.app を差し替える
#   PhoneSaber/tools/unity_copy.sh shots [app]          ビルド済み Player を隠して起動し、各 scene の画面を _out/shots/*.png に残す
#   PhoneSaber/tools/unity_copy.sh smoke [app]          ビルド済み Player を窓なしで起動し、入力・切断・画面遷移を確認
#   PhoneSaber/tools/unity_copy.sh smoke-window [app]   窓を開く（前面へは出さない）。実描画 fps を perf 行で確認
#
# 複製の場所: $PHONESABER_UNITY_COPY（既定 ~/Library/Caches/PhoneSaber/unity-copy）。Library を含むので数 GB。
# 消えても sync で作り直せる（初回の import に数分かかる）。PlayMode 全件は unity_playmode_batches.py を使う。
# 既知失敗を許容するときだけ PHONESABER_EDITMODE_KNOWN_FAILURES に完全名の JSON 配列ファイルを指定する。
# JSON 形式例（仮の名前）: ["ExampleTests.KnownFailure", "OtherTests.KnownFailure(1)"]
# 未指定・空配列ならすべての失敗を非ゼロで返す。クラス名・前方一致・正規表現では除外しない。
# 既知の譜面エディタ失敗2件は完全名の記録がないため、推測でクラス全体を除外しない。
set -u
ROOT="$(CDPATH='' cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)" || exit 1
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

# Title → SongSelect → Game → Result → 判定調整 → SongSelect → Title。Error も失敗にする。
# (PhoneSaberPlayerSmoke.cs。UDP 5005-5007 を数十秒使うので、ゲームが起動中のときは呼ばない)
smoke_player() {
    local app="$1" mode="${2:-batch}" exe log="$OUT/smoke.log" pid waited=0 limit="${PHONESABER_SMOKE_LIMIT:-75}"
    local args=(-phonesaberSmoke "${PHONESABER_SMOKE_SECONDS:-5}" -logFile "$log")
    mkdir -p "$OUT" || return 1
    exe="$(find "$app/Contents/MacOS" -maxdepth 1 -type f | head -1)"
    [[ -x "$exe" ]] || { echo "smoke: 実行ファイルが見つかりません: $app"; return 1; }
    # 古い成功ログを削除できない場合は、今回の結果として再利用しない。
    rm -f "$log" || { echo "smoke: ログを初期化できません ($log)"; return 1; }
    if [[ "$mode" == window ]]; then
        echo "smoke-window: Player の窓を開きます（実描画 fps 用、batchmode ではありません）"
        /usr/bin/open -g -n -W "$app" --args "${args[@]}" -screen-fullscreen 0 -screen-width 1280 -screen-height 720 >/dev/null 2>&1 &
    else
        run_hidden "$exe" -batchmode "${args[@]}" >/dev/null 2>&1 &
    fi
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
    # 成功行を1つ含むだけでは、重複起動や別の失敗した完了を隠せる。
    if [[ "$(grep -cF '[PhoneSaberSmoke] done' "$log")" -ne 1 ]]; then
        echo "smoke: 完了行が複数あります ($log)"; return 1
    fi
    if [[ "$code" -ne 0 || "$count" -ne 0 ]]; then
        echo "smoke: 例外 $count 件 (exit=$code, $log)"; grep -nE '^[A-Za-z.]*Exception' -A4 "$log" | head -30; return 1
    fi
    if ! grep -qE '^[[]PhoneSaberSmoke[]] done exceptions=0 errors=0 input=ok cuts=[1-9][0-9]* flow=ok$' "$log"; then
        echo "smoke: Error / 入力 / 切断 / 画面遷移の確認に失敗 ($log)"; grep -n "PhoneSaberSmoke" "$log" | tail -4; return 1
    fi
    # batchmode の fps は実際の窓描画の値として扱わない。
    echo "smoke: perf mode=${mode}（batch は実描画 fps の判定に使わない）"
    local scene
    for scene in Title SongSelect Game Result; do
        grep -qF "[PhoneSaberSmoke] perf scene=$scene " "$log" || { echo "smoke: $scene の perf 行がありません ($log)"; return 1; }
    done
    grep -F '[PhoneSaberSmoke] perf ' "$log"
    echo "smoke: Title / SongSelect / Game / Result / 判定調整 / Title 通過、Error なし・実カット確認 (${waited} 秒)"
}

# ビルド済み Player を隠したまま起動し、各 scene の画面を PNG に残す（見た目の確認用。窓は出さない）。
# 万一 Player が前面に出たら、すぐ隠し直す。PHONESABER_SHOTS_REFRESH_HZ=30 で「画面の更新が遅い」注意の表示を確かめられる。
shots_player() {
    local app="$1" exe dir="$OUT/shots" log="$OUT/shots.log" pid game waited=0 args
    exe="$(find "$app/Contents/MacOS" -maxdepth 1 -type f | head -1)"
    [[ -x "$exe" ]] || { echo "shots: 実行ファイルが見つかりません: $app"; return 1; }
    # 古い成功結果を再利用しない。削除・作成に失敗したら起動前に止める。
    if ! rm -rf "$dir" "$log" || ! mkdir -p "$dir"; then
        echo "shots: 出力先を初期化できません ($dir, $log)"; return 1;
    fi
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
    # 途中終了やResult/調整の撮影失敗も成功にしない。openの終了コードではPlayerの成否は分からない。
    local scene
    for scene in Title SongSelect Game Result Calibration CalibrationReturn TitleReturn; do
        [[ -s "$dir/$scene.png" ]] || { echo "shots: $scene の画像がありません、または空です ($log)"; return 1; }
    done
    [[ "$(grep -cF '[PhoneSaberSmoke] done' "$log")" -eq 1 ]] || {
        echo "shots: 完了行が無い、または複数あります ($log)"; return 1;
    }
    grep -qE '^[[]PhoneSaberSmoke[]] done exceptions=0 errors=0 input=ok cuts=[1-9][0-9]* flow=ok$' "$log" || {
        echo "shots: Player の正常完了を確認できません ($log)"; return 1;
    }
    echo "shots: $dir"
}

# 長時間試験も常に隠して起動。openの終了コードではなく今回の完了ログで判定する。
soak_player() {
    local minutes="$1" app="$2" log pid waited=0 limit
    limit="$(python3 -c 'import math,sys; v=float(sys.argv[1]); g=float(sys.argv[2]); assert math.isfinite(v) and .1 <= v <= 1440 and math.isfinite(g) and 8 <= g <= 600; print(math.ceil(v*60+g)+90)' "$minutes" "${PHONESABER_SOAK_GAME_SECONDS:-40}")" || {
        echo "soak: 分数は0.1〜1440、Game秒数は8〜600の有限数を指定してください"; return 2;
    }
    [[ -d "$app/Contents/MacOS" ]] || { echo "soak: appがありません: $app"; return 1; }
    mkdir -p "$OUT" || return 1
    log="$(cd "$OUT" && pwd -P)/soak-$$.log" || return 1
    rm -f "$log" || { echo "soak: ログを初期化できません ($log)"; return 1; }
    local args=(-batchmode -phonesaberSoak "$minutes" -logFile "$log")
    [[ -n "${PHONESABER_SOAK_GAME_SECONDS:-}" ]] && args+=(-phonesaberSoakGameSeconds "$PHONESABER_SOAK_GAME_SECONDS")
    /usr/bin/open -g -j -n -W "$app" --args "${args[@]}" >/dev/null 2>&1 &
    pid=$!
    while kill -0 "$pid" 2>/dev/null; do
        if (( waited >= limit )); then
            # 別の試験・通常ゲームを巻き込まないよう、今回だけの絶対logFileで特定する。
            python3 "$ROOT/PhoneSaber/tools/unity_soak_stop.py" "$log"
            kill -TERM "$pid" 2>/dev/null
            wait "$pid" 2>/dev/null
            echo "soak: 判定=FAILED（${limit}秒で中止、${log}）"; return 1
        fi
        sleep 1; waited=$((waited + 1))
    done
    wait "$pid"; local code=$?
    python3 "$ROOT/PhoneSaber/tools/unity_soak_summary.py" "$log"; local verdict=$?
    echo "soak: ログ=${log}（${waited}秒）"
    [[ "$code" -eq 0 && "$verdict" -eq 0 ]]
}

case "${1:-}" in
soak)
    soak_player "${2:-60}" "${3:-$ROOT/Builds/Mac/3D-Saber.app}"
    ;;
smoke)
    smoke_player "${2:-$ROOT/Builds/Mac/3D-Saber.app}"
    ;;
smoke-window)
    smoke_player "${2:-$ROOT/Builds/Mac/3D-Saber.app}" window
    ;;
shots)
    shots_player "${2:-$ROOT/Builds/Mac/3D-Saber.app}"
    ;;
sync)
    sync_copy && echo "synced: $COPY"
    ;;
editmode)
    sync_copy || exit 1
    rm -f "$OUT/editmode.xml" "$OUT/editmode.log" || { echo "editmode: 結果を初期化できません ($OUT)"; exit 1; }
    args=(-batchmode -projectPath "$COPY" -runTests -testPlatform EditMode -testResults "$OUT/editmode.xml" -logFile "$OUT/editmode.log")
    [[ -n "${2:-}" ]] && args+=(-testFilter "$2")
    run_hidden "$UNITY" "${args[@]}"
    unity_exit=$?
    result_args=("$OUT/editmode.xml" "$OUT/editmode.log")
    [[ -n "${PHONESABER_EDITMODE_KNOWN_FAILURES:-}" ]] && result_args+=(--known-failures "$PHONESABER_EDITMODE_KNOWN_FAILURES")
    python3 "$ROOT/PhoneSaber/tools/unity_editmode_results.py" "${result_args[@]}" --unity-exit-code "$unity_exit"
    ;;
build-mac)
    sync_copy || exit 1
    rm -rf "$COPY/Builds/Mac" || { echo "build-mac: 古い build を削除できません"; exit 1; }
    rm -f "$OUT/build-mac.log" || { echo "build-mac: ログを初期化できません"; exit 1; }
    run_hidden "$UNITY" -batchmode -quit -projectPath "$COPY" -executeMethod PhoneSaberPlayerBuild.BuildMac -logFile "$OUT/build-mac.log"
    unity_exit=$?
    if [[ "$unity_exit" -ne 0 ]] || ! grep -q "Result: Success" "$OUT/build-mac.log" || [[ ! -d "$COPY/Builds/Mac/3D-Saber.app" ]]; then
        echo "build-mac: 失敗（$OUT/build-mac.log）"; exit 1
    fi
    if pgrep -f "/3D-Saber.app/Contents/MacOS/" >/dev/null; then
        echo "build-mac: ビルド成功。ゲームが起動中なので差し替えていません: $COPY/Builds/Mac/3D-Saber.app"; exit 3
    fi
    # 差し替える前に、作った Player を実際に起動して確かめる(Editor では動くのに Player だけ落ちる不具合を止める)。
    if ! smoke_player "$COPY/Builds/Mac/3D-Saber.app"; then
        # 例外・Error・入力は正常で、画面の流れ（判定調整の実カットなど）だけ失敗したときは1回だけやり直す。
        # 2026-10-17: 並行の xcodebuild で負荷 68 のとき、判定調整のカットが 0 になった（再実行で 6 回）。
        grep -qE '^[[]PhoneSaberSmoke[]] done exceptions=0 errors=0 input=ok cuts=[1-9][0-9]* flow=FAILED$' "$OUT/smoke.log" 2>/dev/null \
            || { echo "build-mac: 動作確認に失敗したので差し替えていません"; exit 4; }
        cp "$OUT/smoke.log" "$OUT/smoke-first-try.log" 2>/dev/null
        echo "build-mac: 画面の流れだけ失敗（負荷: $(sysctl -n vm.loadavg 2>/dev/null)）。1回だけやり直します"
        smoke_player "$COPY/Builds/Mac/3D-Saber.app" || { echo "build-mac: 動作確認に失敗したので差し替えていません"; exit 4; }
    fi
    mkdir -p "$ROOT/Builds/Mac" "$OUT/previous-app" || exit 1
    # コピー失敗・容量不足でも旧 Player は起動できる場所に残す。完成後だけ同じ volume 内で差し替える。
    staged="$ROOT/Builds/Mac/.3D-Saber-new.app"
    rm -rf "$staged" || exit 1
    ditto "$COPY/Builds/Mac/3D-Saber.app" "$staged" || exit 1
    # ゲーム本体のアイコン（PhoneSaber/mac/app/icon.icns）を入れて、ad-hoc 署名をやり直す。ProjectSettings は変えない。
    if [[ -f "$ROOT/PhoneSaber/mac/app/icon.icns" && -f "$staged/Contents/Resources/PlayerIcon.icns" ]]; then
        cp "$ROOT/PhoneSaber/mac/app/icon.icns" "$staged/Contents/Resources/PlayerIcon.icns" &&
            codesign --force --deep --sign - "$staged" >/dev/null 2>&1 || {
                echo "build-mac: アイコンの差し替え・再署名に失敗したので差し替えていません"; exit 1;
            }
    fi
    # smoke / コピーの待ち時間に運営側が起動した場合も、稼働中の app は差し替えない。
    if pgrep -f "/3D-Saber.app/Contents/MacOS/" >/dev/null; then
        echo "build-mac: ビルド成功。ゲームが起動したので差し替えていません: $staged"; exit 3
    fi
    rm -rf "$OUT/previous-app/3D-Saber.app" || exit 1
    if [[ -d "$ROOT/Builds/Mac/3D-Saber.app" ]]; then
        mv "$ROOT/Builds/Mac/3D-Saber.app" "$OUT/previous-app/" || exit 1
    fi
    if ! mv "$staged" "$ROOT/Builds/Mac/3D-Saber.app"; then
        [[ -d "$OUT/previous-app/3D-Saber.app" ]] && mv "$OUT/previous-app/3D-Saber.app" "$ROOT/Builds/Mac/"
        exit 1
    fi
    echo "build-mac: $ROOT/Builds/Mac/3D-Saber.app を $(git -C "$ROOT" log --oneline -1 | cut -c1-8) で差し替えました（1つ前は $OUT/previous-app）"
    ;;
*)
    sed -n '2,/^set -u/{s/^# \{0,1\}//p;}' "${BASH_SOURCE[0]}"
    exit 2
    ;;
esac
