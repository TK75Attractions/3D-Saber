#!/bin/bash
# Unity の作業はプロジェクトの「複製」で行う（ユーザーが Editor で開いている checkout には触れない）。
#
#   PhoneSaber/tools/unity_copy.sh sync                 複製を現在の checkout に合わせる
#   PhoneSaber/tools/unity_copy.sh editmode [filter]    EditMode テスト（filter は -testFilter の正規表現）
#   PhoneSaber/tools/unity_copy.sh build-mac            macOS Player をビルドし、Builds/Mac/3D-Saber.app を差し替える
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

case "${1:-}" in
sync)
    sync_copy && echo "synced: $COPY"
    ;;
editmode)
    sync_copy || exit 1
    rm -f "$OUT/editmode.xml"
    args=(-batchmode -projectPath "$COPY" -runTests -testPlatform EditMode -testResults "$OUT/editmode.xml" -logFile "$OUT/editmode.log")
    [[ -n "${2:-}" ]] && args+=(-testFilter "$2")
    "$UNITY" "${args[@]}"
    result_args=("$OUT/editmode.xml" "$OUT/editmode.log")
    [[ -n "${PHONESABER_EDITMODE_KNOWN_FAILURES:-}" ]] && result_args+=(--known-failures "$PHONESABER_EDITMODE_KNOWN_FAILURES")
    python3 "$ROOT/PhoneSaber/tools/unity_editmode_results.py" "${result_args[@]}"
    ;;
build-mac)
    sync_copy || exit 1
    rm -rf "$COPY/Builds/Mac"
    "$UNITY" -batchmode -quit -projectPath "$COPY" -executeMethod PhoneSaberPlayerBuild.BuildMac -logFile "$OUT/build-mac.log"
    if ! grep -q "Result: Success" "$OUT/build-mac.log" || [[ ! -d "$COPY/Builds/Mac/3D-Saber.app" ]]; then
        echo "build-mac: 失敗（$OUT/build-mac.log）"; exit 1
    fi
    if pgrep -f "/3D-Saber.app/Contents/MacOS/" >/dev/null; then
        echo "build-mac: ビルド成功。ゲームが起動中なので差し替えていません: $COPY/Builds/Mac/3D-Saber.app"; exit 3
    fi
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
