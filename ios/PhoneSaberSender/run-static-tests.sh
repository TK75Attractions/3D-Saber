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
