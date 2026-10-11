#!/bin/bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
source_dir="$script_dir/PhoneSaberSender"
test_source="$script_dir/../PhoneSaberSenderTests/StaticBGRADetectionTests.swift"
build_dir="$(mktemp -d "${TMPDIR:-/tmp}/phonesaber-static.XXXXXX")"
binary="$build_dir/static-bgra-tests"
module_cache="$build_dir/module-cache"
mkdir -p "$module_cache"
trap 'rm -rf -- "$build_dir"' EXIT

if ! command -v xcrun >/dev/null 2>&1; then
    echo "xcrun is required" >&2
    exit 1
fi

guard_flags=()
case "${PHONESABER_CORELINE_GUARD:-1}" in
    1) ;;
    0) guard_flags=(-D PHONESABER_DISABLE_CORELINE_GUARD) ;;
    *) echo "PHONESABER_CORELINE_GUARD must be 0 or 1" >&2; exit 1 ;;
esac

# macOS の Bash 3.2 は set -u 下で空配列の展開を未定義と扱う。
xcrun swiftc -O ${guard_flags[@]+"${guard_flags[@]}"} \
    "$source_dir/DetectionCore.swift" \
    "$source_dir/BGRADetection.swift" \
    "$test_source" \
    -module-cache-path "$module_cache" \
    -o "$binary"
"$binary"

# シミュレータ不要で、Xcodeと同じ端末状態のXCTestを実行する。
cat > "$build_dir/main.swift" <<'SWIFT'
import XCTest
import Darwin
let suite = DeviceHealthTests.defaultTestSuite
suite.run()
// 件数は「少なくとも」で確認する（テスト追加のたびにここを直さずに済むように。0 件実行は失敗のまま）。
guard let run = suite.testRun, run.executionCount >= 6, run.hasSucceeded else { exit(1) }
SWIFT
xctest_developer="$(xcode-select -p)/Platforms/MacOSX.platform/Developer"
xctest_frameworks="$xctest_developer/Library/Frameworks"
xcrun swiftc -O -D DEVICE_HEALTH_STANDALONE \
    "$source_dir/DeviceHealth.swift" \
    "$script_dir/../PhoneSaberSenderTests/DeviceHealthTests.swift" \
    "$build_dir/main.swift" \
    -I "$xctest_developer/usr/lib" -L "$xctest_developer/usr/lib" \
    -F "$xctest_frameworks" -Xlinker -rpath -Xlinker "$xctest_frameworks" \
    -Xlinker -rpath -Xlinker "$xctest_developer/usr/lib" \
    -module-cache-path "$module_cache" \
    -o "$build_dir/device-health-tests"
"$build_dir/device-health-tests"
