#!/usr/bin/env bash
# 接続中の A/B 用 iPhone を、同じ署名済みアプリでまとめて更新する。
set -euo pipefail

dry_run=false
case "${1:-}" in
  --dry-run) dry_run=true; shift ;;
  -h|--help)
    echo '使い方: bash PhoneSaber/tools/install_iphones.sh [--dry-run]'
    echo '署名: PHONESABER_TEAM / PHONESABER_BUNDLE_ID で上書き可能'
    exit 0 ;;
esac
if [[ $# -ne 0 ]]; then
  echo "不明な引数: $*" >&2
  exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
project="$script_dir/../ios/PhoneSaberSender/PhoneSaberSender.xcodeproj"
derived_data="$HOME/Library/Caches/PhoneSaber/ios-device-build"
app="$derived_data/Build/Products/Debug-iphoneos/PhoneSaberSender.app"
team="${PHONESABER_TEAM:-8RPGNM372H}"
bundle_id="${PHONESABER_BUNDLE_ID:-com.satoshi.PhoneSaberSender2}"

hints() {
  if grep -Eiq '4016|unavailable' "$1"; then
    echo '対処: iPhone のロックを外してケーブルを挿し直す' >&2
  fi
  if grep -Eiq 'No Accounts' "$1"; then
    echo '対処: Xcode › Settings › Accounts でサインイン' >&2
  fi
  if grep -Eiq 'untrusted|not trusted|developer.*trust|信頼されていない' "$1"; then
    echo '対処: iPhone の 設定 › 一般 › VPN とデバイス管理 で開発元を信頼する' >&2
  fi
}

print_command() {
  printf '  '
  printf '%q ' "$@"
  printf '\n'
}

for tool in xcrun python3; do
  if ! command -v "$tool" >/dev/null; then
    echo "必要なコマンドがありません: $tool（Xcode と Python 3 を確認）" >&2
    exit 1
  fi
done
umask 077
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/phonesaber-install.XXXXXX")"
trap 'rm -rf "$work_dir"' EXIT

# State は devicectl の列名。接続可能だがトンネル未接続の available も含める。
# 表示名の iPhone 検索では iPad 等を誤選択するため、JSON の機種・ペアリングも確認。
if ! xcrun devicectl list devices --timeout 30 \
  --filter "State BEGINSWITH 'available' OR State BEGINSWITH 'connected'" \
  --json-output "$work_dir/devices.json" >"$work_dir/list.log" 2>&1; then
  echo 'iPhone 一覧の取得に失敗しました。' >&2
  cat "$work_dir/list.log" >&2
  hints "$work_dir/list.log"
  exit 1
fi
if ! python3 - "$work_dir/devices.json" >"$work_dir/devices.tsv" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    devices = json.load(source)["result"]["devices"]
seen = set()
for device in devices:
    hardware = device.get("hardwareProperties", {})
    connection = device.get("connectionProperties", {})
    if hardware.get("deviceType") != "iPhone" or connection.get("pairingState") != "paired":
        continue
    if hardware.get("reality", "physical") != "physical":
        continue
    udid = hardware.get("udid")
    if not udid or udid in seen:
        continue
    seen.add(udid)
    name = device.get("deviceProperties", {}).get("name", "iPhone")
    name = " ".join(name.split()) or "iPhone"
    print(f"{udid}\t{name}")
PY
then
  echo '端末 JSON を読み取れませんでした。Xcode の devicectl を確認してください。' >&2
  exit 1
fi

udids=()
names=()
while IFS=$'\t' read -r udid name; do
  udids+=("$udid")
  names+=("$name")
done <"$work_dir/devices.tsv"
if [[ ${#udids[@]} -eq 0 ]]; then
  echo '利用可能なペアリング済みの実機 iPhone がありません。' >&2
  echo '対処: iPhone のロックを外してケーブルを挿し直す。初回は「このコンピュータを信頼」を許可する。' >&2
  exit 1
fi

build=(xcodebuild -project "$project" -scheme PhoneSaberSender -configuration Debug)
# 全対象を同じビルド呼び出しに渡し、自動署名の登録対象にも含める。
for udid in "${udids[@]}"; do
  build+=(-destination "id=$udid")
done
build+=(-derivedDataPath "$derived_data" -allowProvisioningUpdates
  -allowProvisioningDeviceRegistration "DEVELOPMENT_TEAM=$team"
  "PRODUCT_BUNDLE_IDENTIFIER=$bundle_id" build)

printf '対象: %s 台 / Team: %s / Bundle ID: %s\n' "${#udids[@]}" "$team" "$bundle_id"
for ((i=0; i<${#udids[@]}; i++)); do
  printf '  %s / UDID: %s\n' "${names[$i]}" "${udids[$i]}"
done
if "$dry_run"; then
  echo '[dry-run] ビルドを1回実行し、各 iPhone にインストールする予定です。'
  print_command "${build[@]}"
  for ((i=0; i<${#udids[@]}; i++)); do
    printf '[予定] %s / UDID: %s\n' "${names[$i]}" "${udids[$i]}"
    print_command xcrun devicectl device install app --device "${udids[$i]}" "$app"
  done
  print_command security cms -D -i "$app/embedded.mobileprovision"
  echo '署名期限: ビルド後のプロファイルから取得（dry-run では未取得）'
  exit 0
fi

echo '署名付き Debug アプリをビルドしています…'
if ! "${build[@]}" >"$work_dir/build.log" 2>&1; then
  cat "$work_dir/build.log" >&2
  hints "$work_dir/build.log"
  for ((i=0; i<${#udids[@]}; i++)); do
    printf '[失敗] %s / UDID: %s / ビルド失敗のため未インストール\n' "${names[$i]}" "${udids[$i]}" >&2
  done
  exit 1
fi
if ! security cms -D -i "$app/embedded.mobileprovision" >"$work_dir/profile.plist" 2>"$work_dir/profile.log"; then
  echo '署名プロファイルの復号に失敗しました。インストールを中止します。' >&2
  cat "$work_dir/profile.log" >&2
  exit 1
fi
if ! expiry="$(python3 - "$work_dir/profile.plist" <<'PY'
import plistlib
import sys
from datetime import timezone

with open(sys.argv[1], "rb") as source:
    profile = plistlib.load(source)
print(profile["ExpirationDate"].replace(tzinfo=timezone.utc).isoformat())
PY
)"; then
  echo '署名プロファイルの有効期限を取得できません。インストールを中止します。' >&2
  exit 1
fi

failed=0
for ((i=0; i<${#udids[@]}; i++)); do
  printf 'インストール中: %s / UDID: %s\n' "${names[$i]}" "${udids[$i]}"
  if xcrun devicectl device install app --device "${udids[$i]}" "$app" >"$work_dir/install.log" 2>&1; then
    printf '[成功] %s / UDID: %s / 署名期限: %s\n' "${names[$i]}" "${udids[$i]}" "$expiry"
  else
    printf '[失敗] %s / UDID: %s / ビルド済み署名期限: %s\n' "${names[$i]}" "${udids[$i]}" "$expiry" >&2
    cat "$work_dir/install.log" >&2
    hints "$work_dir/install.log"
    failed=1
  fi
done
echo '起動時に開発元が未信頼の場合: 設定 › 一般 › VPN とデバイス管理 で開発元を信頼する。'
exit "$failed"
