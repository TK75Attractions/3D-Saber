#!/bin/bash
set -euo pipefail

if [ "$#" -gt 1 ] || { [ "$#" -eq 1 ] && [ "$1" != "--dry-run" ]; }; then
    printf 'Usage: %s [--dry-run]\n' "$(basename "$0")" >&2
    exit 2
fi
dry_run=0
if [ "$#" -eq 1 ]; then
    dry_run=1
fi

tools_dir="$(CDPATH='' cd -P -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
desktop_dir="${HOME:?HOME is not set}/Desktop"

names=(
    "Start PhoneSaber.command"
    "Open PhoneSaber Log.command"
    "Open Latest PhoneSaber Images.command"
)
sources=(
    "$tools_dir/Start PhoneSaber.command"
    "$tools_dir/Open PhoneSaber Log.command"
    "$tools_dir/Open Latest PhoneSaber Images.command"
)
destinations=(
    "$desktop_dir/Start PhoneSaber.command"
    "$desktop_dir/Open PhoneSaber Log.command"
    "$desktop_dir/Open Latest PhoneSaber Images.command"
)

# Check all destinations before creating any link so a name conflict never
# replaces or partially obscures an existing Desktop file.
for index in "${!names[@]}"; do
    destination="${destinations[$index]}"
    source="${sources[$index]}"
    if [ ! -f "$source" ]; then
        printf 'Launcher source not found: %s\n' "$source" >&2
        exit 1
    fi
    if [ -L "$destination" ] && [ "$(readlink "$destination")" = "$source" ]; then
        continue
    fi
    if [ -e "$destination" ] || [ -L "$destination" ]; then
        printf 'Refusing to replace existing Desktop item: %s\n' "$destination" >&2
        exit 1
    fi
done

if [ "$dry_run" -eq 1 ]; then
    printf 'Desktop launcher dry run:\n'
    for index in "${!names[@]}"; do
        destination="${destinations[$index]}"
        source="${sources[$index]}"
        if [ -L "$destination" ] && [ "$(readlink "$destination")" = "$source" ]; then
            printf '  already installed: %s\n' "$destination"
        else
            printf '  would install: %s -> %s\n' "$destination" "$source"
        fi
    done
    exit 0
fi

mkdir -p "$desktop_dir"
for index in "${!names[@]}"; do
    destination="${destinations[$index]}"
    source="${sources[$index]}"
    if [ ! -L "$destination" ]; then
        ln -s "$source" "$destination"
    fi
done

printf 'Desktop launchers are ready:\n'
for destination in "${destinations[@]}"; do
    printf '  %s\n' "$destination"
done
