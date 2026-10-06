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

xcrun swiftc -O \
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
guard let run = suite.testRun, run.executionCount == 4, run.hasSucceeded else { exit(1) }
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
