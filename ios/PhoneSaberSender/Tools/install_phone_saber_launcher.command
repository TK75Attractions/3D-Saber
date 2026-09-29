#!/bin/bash
set -euo pipefail

tools_dir="$(CDPATH='' cd -P -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
desktop_dir="${HOME:?HOME is not set}/Desktop"
mkdir -p "$desktop_dir"

names=("Start PhoneSaber.command" "Open PhoneSaber Log.command")
sources=("$tools_dir/Start PhoneSaber.command" "$tools_dir/Open PhoneSaber Log.command")
destinations=("$desktop_dir/Start PhoneSaber.command" "$desktop_dir/Open PhoneSaber Log.command")

# Check all destinations before creating either link so a name conflict never
# replaces or partially obscures an existing Desktop file.
for index in "${!names[@]}"; do
    destination="${destinations[$index]}"
    source="${sources[$index]}"
    if [ -L "$destination" ] && [ "$(readlink "$destination")" = "$source" ]; then
        continue
    fi
    if [ -e "$destination" ] || [ -L "$destination" ]; then
        printf 'Refusing to replace existing Desktop item: %s\n' "$destination" >&2
        exit 1
    fi
done

for index in "${!names[@]}"; do
    destination="${destinations[$index]}"
    source="${sources[$index]}"
    if [ ! -L "$destination" ]; then
        ln -s "$source" "$destination"
    fi
done

printf 'Desktop launchers are ready:\n  %s\n  %s\n' \
    "${destinations[0]}" "${destinations[1]}"
