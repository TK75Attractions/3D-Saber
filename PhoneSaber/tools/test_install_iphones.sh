#!/usr/bin/env bash
# 実機・署名アカウントに触れず、PATH 上の偽コマンドで運営用 CLI を確認する。
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
installer="$script_dir/install_iphones.sh"
sandbox="$(mktemp -d "${TMPDIR:-/tmp}/phonesaber-install-test.XXXXXX")"
trap 'rm -rf "$sandbox"' EXIT
export HOME="$sandbox/Home space" TMPDIR="$sandbox"
export CALLS="$sandbox/calls" FIXTURE="$sandbox/devices.json" PROFILE="$sandbox/profile.plist"
mkdir -p "$sandbox/bin" "$HOME"
export PATH="$sandbox/bin:$PATH"
unset PHONESABER_TEAM PHONESABER_BUNDLE_ID LIST_FAIL BUILD_FAIL INSTALL_FAIL PROFILE_FAIL NO_DEVICES

python3 - "$FIXTURE" "$PROFILE" <<'PY'
import datetime
import json
import plistlib
import sys

def device(udid, name, state="available", kind="iPhone", pairing="paired", reality="physical"):
    return {"state": state, "identifier": "core-" + udid,
            "hardwareProperties": {"deviceType": kind, "udid": udid, "reality": reality},
            "deviceProperties": {"name": name},
            "connectionProperties": {"pairingState": pairing}}

devices = [device("UDID-A", "受付 A の iPhone"),
           device("UDID-B", "Station B's phone", "connected (no DDI)"),
           device("OFFLINE", "Offline iPhone", "unavailable"),
           device("IPAD", "iPhone と名付けた iPad", kind="iPad"),
           device("UNPAIRED", "未ペアリング", pairing="unpaired"),
           device("SIM", "Simulator", reality="simulated"),
           device("UDID-A", "同じ端末の重複")]
with open(sys.argv[1], "w", encoding="utf-8") as target:
    json.dump({"result": {"devices": devices}}, target)
with open(sys.argv[2], "wb") as target:
    plistlib.dump({"ExpirationDate": datetime.datetime(2030, 1, 8, 12, 34, 56)}, target)
PY

cat >"$sandbox/bin/fake" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
tool="${0##*/}"
printf '%s\n' "$tool" >>"$CALLS"
if [[ "$tool" == xcrun ]]; then exec "$@"; fi
printf '%q ' "$@" >>"$CALLS"
printf '\n' >>"$CALLS"
case "$tool" in
  devicectl)
    if [[ "$1 $2" == 'list devices' ]]; then
      if [[ -n "${LIST_FAIL:-}" ]]; then echo "$LIST_FAIL" >&2; exit 1; fi
      while [[ $# -gt 0 ]]; do
        case "$1" in
          --filter) filter="$2"; shift ;;
          --json-output) output="$2"; shift ;;
        esac
        shift
      done
      # devicectl 側が State フィルターを適用する契約も検証する。
      [[ "$filter" == "State BEGINSWITH 'available' OR State BEGINSWITH 'connected'" ]]
      python3 - "$FIXTURE" "$output" <<'PY'
import json
import os
import sys
with open(sys.argv[1]) as source:
    data = json.load(source)
data["result"]["devices"] = [d for d in data["result"]["devices"]
                              if d["state"].startswith(("available", "connected"))
                              and not os.environ.get("NO_DEVICES")]
with open(sys.argv[2], "w") as target:
    json.dump(data, target)
PY
    else
      [[ "$1 $2 $3 $4" == 'device install app --device' && -d "$6" ]]
      if [[ "$5" == UDID-A && -n "${INSTALL_FAIL:-}" ]]; then
        echo "$INSTALL_FAIL" >&2; exit 1
      fi
    fi ;;
  xcodebuild)
    if [[ -n "${BUILD_FAIL:-}" ]]; then echo "$BUILD_FAIL" >&2; exit 1; fi
    while [[ $# -gt 0 ]]; do
      if [[ "$1" == -derivedDataPath ]]; then derived="$2"; shift; fi
      shift
    done
    mkdir -p "$derived/Build/Products/Debug-iphoneos/PhoneSaberSender.app"
    touch "$derived/Build/Products/Debug-iphoneos/PhoneSaberSender.app/embedded.mobileprovision" ;;
  security)
    [[ "$1 $2 $3 $4" == 'cms -D -i '* && -f "$4" ]]
    if [[ -n "${PROFILE_FAIL:-}" ]]; then echo 'invalid plist'; else cat "$PROFILE"; fi ;;
esac
SH
chmod +x "$sandbox/bin/fake"
for tool in xcrun devicectl xcodebuild security; do
  ln -s fake "$sandbox/bin/$tool"
done
cd "$sandbox"

run_ok() {
  : >"$CALLS"
  bash "$installer" "$@" >"$sandbox/output" 2>&1 || { cat "$sandbox/output"; exit 1; }
}
run_fail() {
  : >"$CALLS"
  if bash "$installer" "$@" >"$sandbox/output" 2>&1; then
    echo '失敗するべきケースが成功しました'; exit 1
  fi
}
contains() { grep -Fq -- "$1" "$sandbox/output" || { cat "$sandbox/output"; exit 1; }; }
count() { [[ "$(grep -c -- "$1" "$CALLS" || true)" -eq "$2" ]]; }

run_ok --dry-run
contains '[予定] 受付 A の iPhone / UDID: UDID-A'
contains "[予定] Station B's phone / UDID: UDID-B"
contains 'DEVELOPMENT_TEAM=8RPGNM372H'
contains 'PRODUCT_BUNDLE_IDENTIFIER=com.satoshi.PhoneSaberSender2'
contains '署名期限: ビルド後'
count '^xcodebuild$' 0
count '^security$' 0
count 'device install app' 0
[[ ! -e "$HOME/Library" ]]

run_ok
contains '[成功] 受付 A の iPhone / UDID: UDID-A / 署名期限: 2030-01-08T12:34:56+00:00'
contains "[成功] Station B's phone / UDID: UDID-B"
count '^xcodebuild$' 1
count '^security$' 1
count 'device install app' 2
grep -Fq -- '-destination id=UDID-A -destination id=UDID-B' "$CALLS"
grep -Fq -- 'Home\ space/Library/Caches/PhoneSaber/ios-device-build' "$CALLS"
! grep -Eq 'OFFLINE|IPAD|UNPAIRED|SIM' "$CALLS" "$sandbox/output"

PHONESABER_TEAM=TESTTEAM PHONESABER_BUNDLE_ID=example.test run_ok
grep -Fq -- 'DEVELOPMENT_TEAM=TESTTEAM PRODUCT_BUNDLE_IDENTIFIER=example.test' "$CALLS"

INSTALL_FAIL='CoreDeviceError 4016: unavailable' run_fail
contains '[失敗] 受付 A の iPhone'
contains "[成功] Station B's phone"
contains 'iPhone のロックを外してケーブルを挿し直す'
count 'device install app' 2

INSTALL_FAIL='Untrusted developer' run_fail
contains '対処: iPhone の 設定 › 一般 › VPN とデバイス管理'
contains "[成功] Station B's phone"

BUILD_FAIL='No Accounts' run_fail
contains 'Xcode › Settings › Accounts でサインイン'
count 'device install app' 0
count '^security$' 0

NO_DEVICES=1 run_fail
contains '利用可能なペアリング済みの実機 iPhone がありません'
count '^xcodebuild$' 0

LIST_FAIL=unavailable run_fail
contains 'iPhone 一覧の取得に失敗'
contains 'iPhone のロックを外してケーブルを挿し直す'
count '^xcodebuild$' 0

PROFILE_FAIL=1 run_fail
contains '署名プロファイルの有効期限を取得できません'
count 'device install app' 0

run_fail --unknown
count '^xcrun$' 0
echo 'PASS: iPhone installer（10 ケース、実機アクセスなし）'
