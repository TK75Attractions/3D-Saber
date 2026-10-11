#!/bin/bash
# Unity の作業はプロジェクトの「複製」で行う（ユーザーが Editor で開いている checkout には触れない）。
#
#   PhoneSaber/tools/unity_copy.sh sync                 複製を現在の checkout に合わせる
#   PhoneSaber/tools/unity_copy.sh editmode [filter]    EditMode テスト（filter は -testFilter の正規表現）
#   PhoneSaber/tools/unity_copy.sh build-mac            macOS Player をビルドし、Builds/Mac/3D-Saber.app を差し替える
#
# 複製の場所: $PHONESABER_UNITY_COPY（既定 ~/Library/Caches/PhoneSaber/unity-copy）。Library を含むので数 GB。
# 消えても sync で作り直せる（初回の import に数分かかる）。PlayMode 全件は unity_playmode_batches.py を使う。
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
    python3 - "$OUT/editmode.xml" "$OUT/editmode.log" <<'PY'
import sys, xml.etree.ElementTree as ET
try:
    root = ET.parse(sys.argv[1]).getroot()
except Exception as error:
    print("EditMode: 結果 XML がありません（compile error の可能性。log を確認）:", sys.argv[2], error)
    sys.exit(2)
failed = [c.get("fullname") for c in root.iter("test-case") if c.get("result") == "Failed"]
print(f"EditMode: total={root.get('total')} passed={root.get('passed')} failed={root.get('failed')}")
for name in failed:
    print("FAIL", name)
# 譜面エディタの ChartRecordingWorkflowTests 2 件は batchmode で以前から失敗する（PhoneSaber と無関係）。
other = [n for n in failed if not n.startswith("ChartRecordingWorkflowTests.")]
sys.exit(1 if other or int(root.get("total", 0)) == 0 else 0)
PY
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
    sed -n '2,9p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
    exit 2
    ;;
esac
