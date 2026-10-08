#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd "$script_dir/../../.." && pwd -P)"
mono_root="${UNITY_MONO_ROOT:-/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/Resources/Scripting/MonoBleedingEdge}"
bench_dir="$(mktemp -d "${TMPDIR:-/private/tmp}/phonesaber-unity-alloc.XXXXXX")"
trap 'rm -rf "$bench_dir"' EXIT
input_dir="$repo_root/Assets/Scripts/Managers/Inputsystem/Input"
"$mono_root/bin/mono" "$mono_root/lib/mono/4.5/csc.exe" \
    /nologo /optimize+ /langversion:9.0 /define:PHONESABER_ALLOC_BENCH \
    "/out:$bench_dir/bench.exe" \
    "$script_dir/Program.cs" "$script_dir/LegacyInputStats.cs" \
    "$input_dir/PhoneSaberPacketParser.cs" "$input_dir/PhoneSaberInputStats.cs" "$input_dir/PhoneSaberEventLog.cs" \
    "$repo_root/Assets/Tests/Editor/PhoneSaberPacketParserTests.cs"
"$mono_root/bin/mono" "$bench_dir/bench.exe"
